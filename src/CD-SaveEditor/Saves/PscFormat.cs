using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using CDSaveEditor.Storage;

namespace CDSaveEditor.Saves;

/// <summary>One "key value" line of a PSC profile.</summary>
public sealed record SaveField(string Key, long Value);

/// <summary>A Cel Damage profile as it sits in the HDD image.</summary>
public sealed class PscSave
{
    public required long TextOffset { get; init; }

    public required byte[] Header { get; init; }

    public required int Length { get; init; }

    public required string Text { get; init; }

    public required IReadOnlyList<SaveField> Fields { get; init; }
}

/// <summary>
/// The profile format: a 24-byte header (16 byte save id, u32 checksum over the
/// text, u32 text length) followed by "key value" lines.
/// </summary>
public static class PscFormat
{
    public const int HeaderLength = 24;

    /// <summary>Fields that record an arena win: arenaWonFlags&lt;arena&gt;&lt;event&gt;.</summary>
    public const string WonPrefix = "arenaWonFlags";

    private static readonly Encoding Latin1 = Encoding.Latin1;
    private static readonly byte[] Needle = Latin1.GetBytes("weaponLockFlags");

    public static List<SaveField> Parse(string text)
    {
        var fields = new List<SaveField>();
        foreach (var rawLine in text.Split("\r\n"))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var space = line.IndexOf(' ');
            if (space < 0)
            {
                continue;
            }
            var key = line[..space];
            var raw = line[(space + 1)..].Trim();
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                fields.Add(new SaveField(key, value));
            }
        }
        return fields;
    }

    /// <summary>Reads a save whose text starts at <paramref name="textOffset"/>.</summary>
    public static PscSave? TrySaveAt(IDiskImage image, long textOffset)
    {
        if (textOffset < HeaderLength)
        {
            return null;
        }
        var header = image.ReadAt(textOffset - HeaderLength, HeaderLength);
        if (header.Length < HeaderLength)
        {
            return null;
        }
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20, 4));
        if (length < 64 || length > 1 << 16)
        {
            return null;
        }
        var raw = image.ReadAt(textOffset, (int)length);
        if (raw.Length != length || !raw.AsSpan().EndsWith("\r\n"u8))
        {
            return null;
        }
        var text = Latin1.GetString(raw);
        var fields = Parse(text);
        var keys = fields.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        if (!keys.Contains("weaponLockFlags") || !keys.Contains("vehicleLockFlags"))
        {
            return null;
        }
        return new PscSave
        {
            TextOffset = textOffset,
            Header = header,
            Length = (int)length,
            Text = text,
            Fields = fields,
        };
    }

    public static List<PscSave> FindSaves(IDiskImage image, Action<double>? progress = null)
    {
        var saves = new List<PscSave>();
        var seen = new HashSet<long>();
        const int chunkSize = 8 << 20;
        var pos = 0L;
        var tail = Array.Empty<byte>();
        while (pos < image.Size)
        {
            var count = (int)Math.Min(chunkSize, image.Size - pos);
            var chunk = image.ReadAt(pos, count);
            if (chunk.Length == 0)
            {
                break;
            }
            var data = new byte[tail.Length + chunk.Length];
            tail.CopyTo(data, 0);
            chunk.CopyTo(data, tail.Length);
            var baseOffset = pos - tail.Length;
            var start = 0;
            while (true)
            {
                var hit = data.AsSpan(start).IndexOf(Needle);
                if (hit < 0)
                {
                    break;
                }
                var index = start + hit;
                var offset = baseOffset + index;
                if (!seen.Contains(offset))
                {
                    var found = TrySaveAt(image, offset);
                    if (found is not null)
                    {
                        seen.Add(offset);
                        saves.Add(found);
                        start = index + Needle.Length;
                        continue;
                    }
                }
                start = index + 1;
            }
            var keep = Math.Min(data.Length, HeaderLength + Needle.Length);
            tail = data[^keep..];
            pos += chunk.Length;
            progress?.Invoke(Math.Min(1.0, (double)pos / image.Size));
        }
        saves.Sort((a, b) => a.TextOffset.CompareTo(b.TextOffset));
        return saves;
    }

    /// <summary>Re-renders the text, padding values to their original width.</summary>
    public static string Render(IReadOnlyList<SaveField> fields, string originalText)
    {
        var widths = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in originalText.Split("\r\n"))
        {
            var space = line.IndexOf(' ');
            if (space < 0)
            {
                continue;
            }
            var key = line[..space];
            widths.TryAdd(key, line.Length - space - 1);
        }

        var lines = new List<string>(fields.Count);
        foreach (var (key, value) in fields)
        {
            var text = value.ToString(CultureInfo.InvariantCulture);
            if (widths.TryGetValue(key, out var width))
            {
                if (text.Length < width)
                {
                    // Keep the byte length identical so the save header stays valid.
                    text = value >= 0
                        ? text.PadLeft(width, '0')
                        : new string('0', width); // only 0 is ever written for negatives
                }
                else if (text.Length > width)
                {
                    throw new FormatException(
                        $"value {value} for {key} does not fit the original {width}-byte field");
                }
            }
            lines.Add(key + " " + text);
        }
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static byte[] Serialize(byte[] header, string text)
    {
        var body = Latin1.GetBytes(text);
        var data = new byte[HeaderLength + body.Length];
        header.AsSpan(0, HeaderLength).CopyTo(data);
        body.CopyTo(data, HeaderLength);
        return data;
    }
}
