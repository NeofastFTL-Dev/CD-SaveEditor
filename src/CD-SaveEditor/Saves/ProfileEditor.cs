using System.Text;
using CDSaveEditor.Storage;

namespace CDSaveEditor.Saves;

public sealed record EditResult(
    long Offset,
    IReadOnlyList<SaveField> Before,
    IReadOnlyList<SaveField> After,
    bool Written,
    string Mode = "bytes");

public sealed record UnlockResult(
    IReadOnlyList<EditResult> Results,
    string? BackupPath,
    IReadOnlyList<string> Baselines);

/// <summary>Rewrites the lock and win flags of the profiles in an image.</summary>
/// <remarks>
/// Every rewrite keeps the text byte-for-byte the same length, so the header
/// length and the FATX directory entry stay valid. When a value cannot be
/// expressed in the original field width the header checksum is zeroed instead:
/// the game then rebuilds the profile from its (patched) startup config.
/// </remarks>
public static class ProfileEditor
{
    /// <summary>Fields the tool flips, in unlock order. A set bit means locked.</summary>
    public static readonly string[] UnlockFields =
    {
        "weaponLockFlags",
        "vehicleLockFlags",
        "arenaRaceLockFlags",
        "arenaFlagLockFlags",
        "arenaSmackLockFlags",
    };

    public static List<SaveField> UnlockedFields(IEnumerable<SaveField> fields)
    {
        var result = new List<SaveField>();
        foreach (var (key, value) in fields)
        {
            if (UnlockFields.Contains(key))
            {
                result.Add(new SaveField(key, 0));
            }
            else if (key.StartsWith(PscFormat.WonPrefix, StringComparison.Ordinal))
            {
                result.Add(new SaveField(key, 1));
            }
            else
            {
                result.Add(new SaveField(key, value));
            }
        }
        return result;
    }

    public static bool IsUnlocked(IEnumerable<SaveField> fields)
    {
        var locks = fields.Where(field => UnlockFields.Contains(field.Key)).Select(field => field.Value).ToList();
        return locks.Count > 0 && locks.All(value => value == 0);
    }

    public static UnlockResult Unlock(
        IDiskImage image,
        IReadOnlyList<PscSave> saves,
        string baselineDir,
        bool dryRun = false,
        bool doBackup = true,
        Action<double>? progress = null)
    {
        var plan = new List<(PscSave Save, string Text, List<SaveField> Fields)>();
        foreach (var save in saves)
        {
            var fields = UnlockedFields(save.Fields);
            var text = PscFormat.Render(fields, save.Text);
            if (text.Length != save.Text.Length)
            {
                throw new InvalidOperationException("internal error: text length changed");
            }
            plan.Add((save, text, fields));
        }

        if (dryRun)
        {
            return new UnlockResult(
                plan.Select(item => new EditResult(item.Save.TextOffset, item.Save.Fields, item.Fields, false)).ToList(),
                null,
                Array.Empty<string>());
        }

        var baselines = SaveStore.WriteBaselines(plan.Select(item => item.Save).ToList(), baselineDir);

        string? backupPath = null;
        if (doBackup && plan.Count > 0)
        {
            backupPath = SaveStore.Backup(image.Path);
            progress?.Invoke(0);
        }

        var results = new List<EditResult>(plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            var (save, text, fields) = plan[i];
            image.WriteAt(save.TextOffset, Encoding.Latin1.GetBytes(text));
            results.Add(new EditResult(save.TextOffset, save.Fields, fields, true));
            progress?.Invoke((double)(i + 1) / plan.Count);
        }
        return new UnlockResult(results, backupPath, baselines);
    }

    /// <summary>Writes a custom set of lock-flag values into the profile(s).</summary>
    public static IReadOnlyList<EditResult> ApplyCombination(
        IDiskImage image,
        IReadOnlyList<PscSave> saves,
        IReadOnlyDictionary<string, long> target,
        bool dryRun = false)
    {
        var results = new List<EditResult>();
        foreach (var save in saves)
        {
            var fields = save.Fields
                .Select(field => new SaveField(field.Key, target.TryGetValue(field.Key, out var value) ? value : field.Value))
                .ToList();
            string? text = null;
            try
            {
                text = PscFormat.Render(fields, save.Text);
            }
            catch (FormatException)
            {
                // the value needs more bytes than the game wrote; the rebuild path below handles it
            }
            var fits = text is not null && text.Length == save.Text.Length;
            var changed = fits && text != save.Text;
            if (!dryRun)
            {
                if (changed)
                {
                    image.WriteAt(save.TextOffset, Encoding.Latin1.GetBytes(text!));
                }
                else if (!fits)
                {
                    image.WriteAt(save.TextOffset - 8, new byte[4]);
                }
            }
            results.Add(new EditResult(save.TextOffset, save.Fields, fields, changed, fits ? "bytes" : "rebuild"));
        }
        return results;
    }

    /// <summary>Writes the saved original values back into every matching save.</summary>
    public static (IReadOnlyList<EditResult> Results, string? BackupPath) Restore(
        IDiskImage image,
        IReadOnlyList<PscSave> saves,
        IReadOnlyList<Baseline> baselines,
        bool dryRun = false,
        bool doBackup = true,
        Action<double>? progress = null)
    {
        var byOffset = baselines
            .Where(baseline => baseline.Offset is not null)
            .ToDictionary(baseline => baseline.Offset!.Value, baseline => baseline);
        var plan = new List<(PscSave Save, Baseline Baseline)>();
        for (var i = 0; i < saves.Count; i++)
        {
            if (byOffset.TryGetValue(saves[i].TextOffset, out var baseline) is false)
            {
                baseline = i < baselines.Count ? baselines[i] : null;
            }
            if (baseline is null)
            {
                continue;
            }
            plan.Add((saves[i], baseline));
        }
        if (plan.Count == 0)
        {
            throw new ToolException("no saved original profile found - run unlock once first");
        }

        bool Restorable(PscSave save, Baseline baseline) => baseline.Text.Length == save.Text.Length;

        if (dryRun)
        {
            return (plan.Select(item => new EditResult(
                item.Save.TextOffset,
                item.Save.Fields,
                Restorable(item.Save, item.Baseline) ? item.Baseline.Fields : Array.Empty<SaveField>(),
                false,
                Restorable(item.Save, item.Baseline) ? "bytes" : "rebuild")).ToList(), null);
        }

        var backupPath = doBackup ? SaveStore.Backup(image.Path) : null;
        progress?.Invoke(0);

        var results = new List<EditResult>(plan.Count);
        for (var i = 0; i < plan.Count; i++)
        {
            var (save, baseline) = plan[i];
            if (Restorable(save, baseline))
            {
                image.WriteAt(save.TextOffset, Encoding.Latin1.GetBytes(baseline.Text));
                results.Add(new EditResult(save.TextOffset, save.Fields, baseline.Fields, true));
            }
            else
            {
                // Break the header checksum: the game rebuilds the profile from its
                // own defaults (now the original ones) on the next boot.
                image.WriteAt(save.TextOffset - 8, new byte[4]);
                results.Add(new EditResult(save.TextOffset, save.Fields, Array.Empty<SaveField>(), true, "rebuild"));
            }
            progress?.Invoke((double)(i + 1) / plan.Count);
        }
        return (results, backupPath);
    }
}
