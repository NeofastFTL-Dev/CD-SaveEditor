using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDSaveEditor.Saves;

namespace CDSaveEditor.Game;

public sealed record GameState(
    string Path,
    bool Found,
    long Offset,
    bool Patched,
    IReadOnlyDictionary<string, long> Values)
{
    public static GameState Missing(string? path) =>
        new(path ?? "", false, 0, false, new Dictionary<string, long>());
}

/// <summary>
/// The "NEW GAME CONFIG" block of the game's startup script. The game rebuilds a
/// profile it does not accept from these values, so they are what makes an edit
/// survive the next boot.
/// </summary>
public sealed class GameFiles
{
    public const int Window = 2048;

    public static readonly string[] Keys =
    {
        "weaponLockFlags",
        "vehicleLockFlags",
        "arenaRaceLockFlags",
        "arenaFlagLockFlags",
        "arenaSmackLockFlags",
    };

    private static readonly byte[] Marker = Encoding.ASCII.GetBytes("// NEW GAME CONFIG");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _saveDir;
    private readonly string _gamePatchFile;

    public GameFiles(string saveDir)
    {
        _saveDir = saveDir;
        _gamePatchFile = Path.Combine(saveDir, "game_patch.json");
    }

    /// <summary>Locate the startup config block inside the ISO.</summary>
    public static (long Offset, byte[] Block)? FindConfig(string iso)
    {
        using var stream = new FileStream(iso, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        const int chunkSize = 4 << 20;
        var tail = Array.Empty<byte>();
        var pos = 0L;
        while (true)
        {
            var buffer = new byte[chunkSize];
            var read = stream.Read(buffer, 0, chunkSize);
            if (read == 0)
            {
                return null;
            }
            var data = new byte[tail.Length + read];
            tail.CopyTo(data, 0);
            buffer.AsSpan(0, read).CopyTo(data.AsSpan(tail.Length));
            var hit = data.AsSpan().IndexOf(Marker);
            if (hit >= 0)
            {
                var offset = pos - tail.Length + hit;
                stream.Position = offset;
                var block = new byte[Window];
                var got = 0;
                while (got < Window)
                {
                    var n = stream.Read(block, got, Window - got);
                    if (n == 0)
                    {
                        break;
                    }
                    got += n;
                }
                return (offset, block[..got]);
            }
            var keep = Math.Min(data.Length, Marker.Length);
            tail = data[^keep..];
            pos += read;
        }
    }

    /// <summary>Pull the lock-flag values out of a startup config block.</summary>
    public static Dictionary<string, long> ParseConfig(ReadOnlySpan<byte> block)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in SplitLines(block))
        {
            foreach (var key in Keys)
            {
                var head = Encoding.ASCII.GetBytes(key + " ");
                if (!line.AsSpan().StartsWith(head))
                {
                    continue;
                }
                var rest = Encoding.Latin1.GetString(line, head.Length, line.Length - head.Length).Trim();
                var token = rest.Split(' ')[0];
                long value;
                if (token.StartsWith("b.", StringComparison.Ordinal))
                {
                    value = 0;
                    foreach (var bit in token[2..].Split('.'))
                    {
                        if (!int.TryParse(bit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                        {
                            throw new ToolException($"cannot read the bit list \"{token}\" in the game config");
                        }
                        value |= 1L << index;
                    }
                }
                else if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    throw new ToolException($"cannot read the value \"{token}\" for {key} in the game config");
                }
                values[key] = value;
            }
        }
        return values;
    }

    /// <summary>
    /// Return the config with <paramref name="values"/> written, byte length
    /// unchanged: the lock-flag lines are laid out again as a group so a value can
    /// grow back from "0" to a full lock mask without moving the rest of the block.
    /// </summary>
    public static byte[] PatchBlock(byte[] block, IReadOnlyDictionary<string, long> values)
    {
        var lines = SplitLines(block);
        var found = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (Keys.Any(key => lines[i].AsSpan().StartsWith(Encoding.ASCII.GetBytes(key + " "))))
            {
                found.Add(i);
            }
        }
        if (found.Count == 0)
        {
            throw new ToolException("the startup config has no lock-flag lines");
        }
        var first = found[0];
        var last = found[^1];

        var order = new List<string>();
        var comments = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var index in found)
        {
            foreach (var key in Keys)
            {
                var head = Encoding.ASCII.GetBytes(key + " ");
                if (!lines[index].AsSpan().StartsWith(head))
                {
                    continue;
                }
                var rest = Encoding.Latin1.GetString(lines[index], head.Length, lines[index].Length - head.Length).Trim();
                var token = rest.Split(' ')[0];
                var comment = rest[token.Length..].Trim();
                order.Add(key);
                if (comment.Length > 0)
                {
                    comments[key] = Encoding.Latin1.GetBytes(comment);
                }
                break;
            }
        }
        foreach (var key in Keys.Where(key => !order.Contains(key)))
        {
            order.Add(key);
        }

        var oldLength = 0;
        for (var i = first; i <= last; i++)
        {
            oldLength += lines[i].Length;
        }
        oldLength += 2 * (last - first);

        var newLines = new List<byte[]>();
        foreach (var key in order)
        {
            var value = values.TryGetValue(key, out var given) ? given : 0;
            var text = new List<byte>(Encoding.ASCII.GetBytes($"{key} {value}"));
            if (comments.TryGetValue(key, out var comment))
            {
                text.Add(0x20);
                text.Add(0x20);
                text.AddRange(comment);
            }
            newLines.Add(text.ToArray());
        }
        var newLength = newLines.Sum(line => line.Length) + 2 * (newLines.Count - 1);
        if (newLength > oldLength)
        {
            throw new ToolException("no room in the game startup config for these values");
        }
        var padding = new byte[oldLength - newLength];
        Array.Fill(padding, (byte)0x20);
        newLines[^1] = newLines[^1].Concat(padding).ToArray();

        using var output = new MemoryStream(block.Length);
        void WriteLine(byte[] line)
        {
            output.Write(line);
        }
        for (var i = 0; i < first; i++)
        {
            WriteLine(lines[i]);
            output.WriteByte(0x0D);
            output.WriteByte(0x0A);
        }
        for (var i = 0; i < newLines.Count; i++)
        {
            if (i > 0)
            {
                output.WriteByte(0x0D);
                output.WriteByte(0x0A);
            }
            WriteLine(newLines[i]);
        }
        for (var i = last + 1; i < lines.Count; i++)
        {
            output.WriteByte(0x0D);
            output.WriteByte(0x0A);
            WriteLine(lines[i]);
        }
        var patched = output.ToArray();
        if (patched.Length != block.Length)
        {
            throw new InvalidOperationException("internal error: config block changed size");
        }
        return patched;
    }

    public GameState State(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso))
        {
            return GameState.Missing(iso);
        }
        var found = FindConfig(iso);
        if (found is null)
        {
            return new GameState(iso, false, 0, false, new Dictionary<string, long>());
        }
        var values = ParseConfig(found.Value.Block);
        return new GameState(iso, true, found.Value.Offset,
            values.Count > 0 && values.Values.All(value => value == 0), values);
    }

    /// <summary>The game's own lock masks, used when a flag is switched off.</summary>
    public Dictionary<string, long> DefaultLockMasks(string? iso, IReadOnlyList<Baseline> baselines)
    {
        var masks = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var baseline in baselines)
        {
            foreach (var field in baseline.Fields)
            {
                if (Keys.Contains(field.Key) && !masks.ContainsKey(field.Key))
                {
                    masks[field.Key] = field.Value;
                }
            }
        }
        if (masks.Count < Keys.Length)
        {
            byte[]? source = null;
            var manifest = LoadGamePatch();
            if (manifest is not null && iso is not null && manifest.Iso == iso)
            {
                source = Convert.FromHexString(manifest.Original);
            }
            else if (!string.IsNullOrWhiteSpace(iso))
            {
                var pristine = iso + ".orig";
                source = FindConfig(File.Exists(pristine) ? pristine : iso)?.Block;
            }
            if (source is not null)
            {
                foreach (var (key, value) in ParseConfig(source))
                {
                    masks[key] = value;
                }
            }
        }
        foreach (var key in Keys)
        {
            masks.TryAdd(key, -1);
        }
        return masks;
    }

    /// <summary>Make the game build every new profile from <paramref name="values"/>.</summary>
    public bool PatchGame(string iso, IReadOnlyDictionary<string, long>? values = null, bool dryRun = false)
    {
        var target = values ?? Keys.ToDictionary(key => key, _ => 0L);
        var found = FindConfig(iso)
                    ?? throw new ToolException($"no Cel Damage startup config found inside {iso}");
        var newBlock = PatchBlock(found.Block, target);
        if (newBlock.AsSpan().SequenceEqual(found.Block))
        {
            return false;
        }
        if (dryRun)
        {
            return true;
        }
        var manifest = LoadGamePatch();
        var known = manifest is not null && manifest.Iso == iso && manifest.Offset == found.Offset;
        if (!known)
        {
            Directory.CreateDirectory(_saveDir);
            var backup = iso + ".orig";
            if (!File.Exists(backup))
            {
                File.Copy(iso, backup);
            }
            WriteGamePatch(new GamePatch
            {
                Iso = iso,
                Offset = found.Offset,
                Length = found.Block.Length,
                Original = Convert.ToHexString(found.Block).ToLowerInvariant(),
            });
        }
        using var stream = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        stream.Position = found.Offset;
        stream.Write(newBlock);
        return true;
    }

    /// <summary>Put the game's original startup config back.</summary>
    public bool RevertGame(string iso, bool dryRun = false)
    {
        var manifest = LoadGamePatch();
        if (manifest is null || manifest.Iso != iso)
        {
            return false;
        }
        if (dryRun)
        {
            return true;
        }
        using (var stream = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            stream.Position = manifest.Offset;
            stream.Write(Convert.FromHexString(manifest.Original));
        }
        try
        {
            File.Delete(_gamePatchFile);
        }
        catch (IOException)
        {
            // the manifest is only a convenience; the ISO bytes are already back
        }
        return true;
    }

    private GamePatch? LoadGamePatch()
    {
        try
        {
            return JsonSerializer.Deserialize<GamePatch>(File.ReadAllText(_gamePatchFile));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void WriteGamePatch(GamePatch patch)
    {
        var temp = _gamePatchFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(patch, JsonOptions));
        File.Move(temp, _gamePatchFile, overwrite: true);
    }

    private static List<byte[]> SplitLines(ReadOnlySpan<byte> block)
    {
        var lines = new List<byte[]>();
        var start = 0;
        for (var i = 0; i < block.Length - 1; i++)
        {
            if (block[i] == 0x0D && block[i + 1] == 0x0A)
            {
                lines.Add(block[start..i].ToArray());
                start = i + 2;
                i++;
            }
        }
        lines.Add(block[start..].ToArray());
        return lines;
    }

    private sealed class GamePatch
    {
        [JsonPropertyName("iso")]
        public string Iso { get; set; } = "";

        [JsonPropertyName("offset")]
        public long Offset { get; set; }

        [JsonPropertyName("length")]
        public int Length { get; set; }

        [JsonPropertyName("original")]
        public string Original { get; set; } = "";
    }
}
