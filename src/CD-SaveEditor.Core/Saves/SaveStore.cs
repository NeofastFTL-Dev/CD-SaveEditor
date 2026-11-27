using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CDSaveEditor.Storage;

namespace CDSaveEditor.Saves;

/// <summary>The untouched profile saved before the first edit.</summary>
public sealed class Baseline
{
    public long? Offset { get; init; }

    public string File { get; init; } = "";

    public byte[] Header { get; init; } = new byte[PscFormat.HeaderLength];

    public string Text { get; init; } = "";

    public IReadOnlyList<SaveField> Fields { get; init; } = Array.Empty<SaveField>();
}

public sealed record ImportResult(long Offset, int Length, int Slot)
{
    public string? BackupPath { get; init; }
}

/// <summary>Reading profiles out of the image and written files back into it.</summary>
public static class SaveStore
{
    public static string SlotName(int index, int _total) => $"pitaPsc_slot{index + 1}.psc";

    public static string Backup(string path)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var destination = $"{path}.bak-{stamp}";
        File.Copy(path, destination, overwrite: true);
        return destination;
    }

    public static void Dump(PscSave save, string path) =>
        File.WriteAllBytes(path, PscFormat.Serialize(save.Header, save.Text));

    /// <summary>
    /// Keep a copy of the untouched saves in saves/original. Entries are never
    /// overwritten: once a save has a baseline it stays the original one.
    /// </summary>
    public static IReadOnlyList<string> WriteBaselines(IReadOnlyList<PscSave> saves, string baselineDir)
    {
        Directory.CreateDirectory(baselineDir);
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = LoadManifest(manifestPath);
        var known = manifest.Select(entry => entry.Offset).ToHashSet();
        var written = new List<string>();

        for (var i = 0; i < saves.Count; i++)
        {
            var save = saves[i];
            if (known.Contains(save.TextOffset))
            {
                continue;
            }
            var name = SlotName(i, saves.Count);
            Dump(save, Path.Combine(baselineDir, name));
            manifest.Add(new ManifestEntry
            {
                Offset = save.TextOffset,
                Length = save.Length,
                File = name,
            });
            known.Add(save.TextOffset);
            written.Add(name);
        }

        if (written.Count > 0 || !File.Exists(manifestPath))
        {
            var temp = manifestPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions));
            File.Move(temp, manifestPath, overwrite: true);
        }
        return written;
    }

    public static IReadOnlyList<Baseline> LoadBaselines(string baselineDir)
    {
        var manifestPath = Path.Combine(baselineDir, "manifest.json");
        var manifest = LoadManifest(manifestPath);
        var baselines = new List<Baseline>();
        foreach (var entry in manifest)
        {
            byte[] data;
            try
            {
                data = File.ReadAllBytes(Path.Combine(baselineDir, entry.File));
            }
            catch (IOException)
            {
                continue;
            }
            if (data.Length < PscFormat.HeaderLength)
            {
                continue;
            }
            var text = Encoding.Latin1.GetString(data, PscFormat.HeaderLength, data.Length - PscFormat.HeaderLength);
            baselines.Add(new Baseline
            {
                Offset = entry.Offset,
                File = Path.Combine(baselineDir, entry.File),
                Header = data[..PscFormat.HeaderLength],
                Text = text,
                Fields = PscFormat.Parse(text),
            });
        }
        return baselines;
    }

    /// <summary>Writes an exported profile file into the xemu HDD image.</summary>
    /// <remarks>
    /// The game validates the 24-byte header, so the file is written byte for byte
    /// into a slot whose text is exactly as long; anything else would break the
    /// profile. Profiles exported from this tool always satisfy that.
    /// </remarks>
    public static ImportResult Import(string imagePath, string pscPath, int slot, bool backup = true)
    {
        var data = File.ReadAllBytes(pscPath);
        if (data.Length < PscFormat.HeaderLength + 64)
        {
            throw new ToolException($"{pscPath} is too small to be a Cel Damage profile.");
        }
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(20, 4));
        var text = Encoding.Latin1.GetString(data, PscFormat.HeaderLength, data.Length - PscFormat.HeaderLength);
        if (declared != text.Length || !text.EndsWith("\r\n", StringComparison.Ordinal))
        {
            throw new ToolException(
                $"{pscPath} is not a profile this tool wrote: its header length does not match the text.");
        }
        var keys = PscFormat.Parse(text).Select(field => field.Key).ToHashSet(StringComparer.Ordinal);
        if (!keys.Contains("weaponLockFlags") || !keys.Contains("vehicleLockFlags"))
        {
            throw new ToolException($"{pscPath} does not look like a Cel Damage profile.");
        }

        using var image = DiskImageFactory.Open(imagePath);
        var saves = PscFormat.FindSaves(image);
        if (saves.Count == 0)
        {
            throw new ToolException($"no Cel Damage save found in {imagePath}");
        }
        if (slot < 0 || slot >= saves.Count)
        {
            throw new ToolException(
                $"slot {slot + 1} does not exist; {imagePath} holds {saves.Count} save(s).");
        }
        var target = saves[slot];
        if (text.Length != target.Length)
        {
            throw new ToolException(
                $"the profile in {pscPath} holds {text.Length} bytes of text but slot {slot + 1} " +
                $"holds {target.Length}. Import a profile exported from the same slot, or use " +
                "unlock/apply to rewrite a save in place.");
        }

        var backupPath = backup ? SaveStore.Backup(imagePath) : null;
        image.WriteAt(target.TextOffset - PscFormat.HeaderLength, data);
        return new ImportResult(target.TextOffset, text.Length, slot) { BackupPath = backupPath };
    }

    private sealed class ManifestEntry
    {
        [JsonPropertyName("offset")]
        public long Offset { get; set; }

        [JsonPropertyName("length")]
        public int Length { get; set; }

        [JsonPropertyName("file")]
        public string File { get; set; } = "";
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static List<ManifestEntry> LoadManifest(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<ManifestEntry>>(json) ?? new List<ManifestEntry>();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return new List<ManifestEntry>();
        }
    }
}
