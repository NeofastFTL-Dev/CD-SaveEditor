using System.Buffers.Binary;
using System.IO.Compression;

namespace CDSaveEditor.Storage;

/// <summary>Read/write access to a flat guest disk, qcow2 or raw.</summary>
public interface IDiskImage : IDisposable
{
    string Path { get; }

    long Size { get; }

    byte[] ReadAt(long offset, int length);

    void WriteAt(long offset, ReadOnlySpan<byte> data);
}

public static class DiskImageFactory
{
    private static readonly byte[] Qcow2Magic = { 0x51, 0x46, 0x49, 0xFB }; // "QFI\xfb"

    public static bool IsQcow2(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Span<byte> magic = stackalloc byte[4];
        return stream.Read(magic) == 4 && magic.SequenceEqual(Qcow2Magic);
    }

    public static IDiskImage Open(string path) =>
        IsQcow2(path) ? new QCow2Image(path) : new RawImage(path);
}

/// <summary>
/// A plain disk image.
/// </summary>
/// <remarks>
/// The stream is opened without buffering so a handle that outlives a poll always
/// returns what is on disk now, including bytes another process wrote after the
/// image was opened.
/// </remarks>
public sealed class RawImage : IDiskImage
{
    private readonly FileStream _stream;

    public RawImage(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, bufferSize: 1, FileOptions.None);
        Size = _stream.Length;
    }

    public string Path { get; }

    public long Size { get; }

    public byte[] ReadAt(long offset, int length)
    {
        var buffer = new byte[Math.Max(0, length)];
        _stream.Position = offset;
        var done = 0;
        while (done < buffer.Length)
        {
            var read = _stream.Read(buffer, done, buffer.Length - done);
            if (read == 0)
            {
                break;
            }
            done += read;
        }
        return done == buffer.Length ? buffer : buffer[..done];
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> data)
    {
        _stream.Position = offset;
        _stream.Write(data);
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>Minimal qcow2 (v2/v3) reader and in-place writer.</summary>
/// <remarks>
/// Writes only into clusters that are already allocated and uncompressed, which is
/// all that is needed to patch a save in place.
/// </remarks>
public sealed class QCow2Image : IDiskImage
{
    private const ulong CompressedFlag = 1UL << 62;
    private const ulong CopiedFlag = 1UL;
    private const ulong HostOffsetMask = 0x00FFFFFFFFFFFFFFUL;

    private readonly FileStream _stream;
    private readonly int _clusterBits;
    private readonly int _clusterSize;
    private readonly int _sizeShift;
    private readonly int _sizeMask;
    private readonly ulong _offsetMask;
    private readonly int _l2Bits;
    private readonly ulong[] _l1;
    private readonly Dictionary<int, ulong[]?> _l2 = new();

    public QCow2Image(string path)
    {
        Path = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite, bufferSize: 1, FileOptions.None);

        var header = ReadRaw(0, 112);
        if (header.Length < 112)
        {
            throw new InvalidDataException("qcow2 header is truncated");
        }
        if (BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4)) != 0x514649FB)
        {
            throw new InvalidDataException("not a qcow2 image");
        }
        Version = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4, 4));
        _clusterBits = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4));
        if (_clusterBits is < 9 or > 21)
        {
            throw new InvalidDataException($"unsupported qcow2 cluster size (2^{_clusterBits})");
        }
        Size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(24, 8));
        var l1Size = (int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36, 4));
        var l1Offset = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(40, 8));

        _clusterSize = 1 << _clusterBits;
        _sizeShift = 40 - _clusterBits;
        _sizeMask = (1 << (_clusterBits - 8)) - 1;
        _offsetMask = (1UL << _sizeShift) - 1;
        _l2Bits = _clusterBits - 3;

        var table = ReadRaw(l1Offset, l1Size * 8);
        _l1 = new ulong[Math.Max(0, table.Length / 8)];
        for (var i = 0; i < _l1.Length; i++)
        {
            _l1[i] = BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(i * 8, 8));
        }
    }

    public string Path { get; }

    public long Size { get; }

    public uint Version { get; }

    public byte[] ReadAt(long offset, int length)
    {
        var output = new byte[Math.Max(0, length)];
        var done = 0;
        while (done < output.Length)
        {
            var here = offset + done;
            var within = (int)(here & (_clusterSize - 1));
            var count = Math.Min(output.Length - done, _clusterSize - within);
            var entry = Entry(here);
            if (entry == 0)
            {
                // unallocated: already zero
            }
            else if ((entry & CompressedFlag) != 0)
            {
                var clusterOffset = (long)(entry & _offsetMask);
                var sectors = (int)(((entry >> _sizeShift) & (ulong)_sizeMask) + 1);
                var decoded = Inflate(ReadRaw(clusterOffset, sectors * 512), _clusterSize);
                Array.Copy(decoded, within, output, done, count);
            }
            else if ((entry & CopiedFlag) != 0)
            {
                // zero flag set: the cluster reads as zeros
            }
            else
            {
                var clusterOffset = (long)(entry & HostOffsetMask);
                var raw = ReadRaw(clusterOffset + within, count);
                Array.Copy(raw, 0, output, done, Math.Min(raw.Length, count));
            }
            done += count;
        }
        return output;
    }

    public void WriteAt(long offset, ReadOnlySpan<byte> data)
    {
        var pos = 0;
        while (pos < data.Length)
        {
            var here = offset + pos;
            var within = (int)(here & (_clusterSize - 1));
            var count = Math.Min(data.Length - pos, _clusterSize - within);
            var entry = Entry(here);
            if (entry == 0)
            {
                throw new IOException(
                    $"cannot write to an unallocated region of the qcow2 image (offset 0x{here:x})");
            }
            if ((entry & CompressedFlag) != 0)
            {
                throw new IOException(
                    $"the save lives in a compressed qcow2 cluster, which cannot be patched in place (offset 0x{here:x})");
            }
            var clusterOffset = (long)(entry & HostOffsetMask);
            _stream.Position = clusterOffset + within;
            _stream.Write(data.Slice(pos, count));
            pos += count;
        }
        _stream.Flush();
    }

    public void Dispose() => _stream.Dispose();

    private byte[] ReadRaw(long offset, int length)
    {
        var buffer = new byte[Math.Max(0, length)];
        _stream.Position = offset;
        var done = 0;
        while (done < buffer.Length)
        {
            var read = _stream.Read(buffer, done, buffer.Length - done);
            if (read == 0)
            {
                break;
            }
            done += read;
        }
        return done == buffer.Length ? buffer : buffer[..done];
    }

    private static byte[] Inflate(byte[] blob, int limit)
    {
        var output = new byte[limit];
        using var stream = new ZLibStream(new MemoryStream(blob), CompressionMode.Decompress);
        var done = 0;
        while (done < limit)
        {
            var read = stream.Read(output, done, limit - done);
            if (read == 0)
            {
                break;
            }
            done += read;
        }
        return done == limit ? output : output[..done];
    }

    private ulong[]? L2Table(int l1Index)
    {
        if (_l2.TryGetValue(l1Index, out var cached))
        {
            return cached;
        }
        ulong[]? table = null;
        var offset = _l1[l1Index] & HostOffsetMask;
        if (offset != 0)
        {
            var raw = ReadRaw((long)offset, _clusterSize);
            table = new ulong[raw.Length / 8];
            for (var i = 0; i < table.Length; i++)
            {
                table[i] = BinaryPrimitives.ReadUInt64BigEndian(raw.AsSpan(i * 8, 8));
            }
        }
        _l2[l1Index] = table;
        return table;
    }

    private ulong Entry(long guestOffset)
    {
        var cluster = guestOffset >> _clusterBits;
        var l1Index = (int)(cluster >> _l2Bits);
        if (l1Index < 0 || l1Index >= _l1.Length)
        {
            return 0;
        }
        var l2Index = (int)(cluster & ((1L << _l2Bits) - 1));
        var table = L2Table(l1Index);
        if (table is null || l2Index >= table.Length)
        {
            return 0;
        }
        return table[l2Index];
    }
}
