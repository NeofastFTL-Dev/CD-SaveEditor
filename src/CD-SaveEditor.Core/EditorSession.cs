using CDSaveEditor.Game;
using CDSaveEditor.Saves;
using CDSaveEditor.Storage;

namespace CDSaveEditor;

public sealed record SlotInfo(long Offset, IReadOnlyList<SaveField> Fields, bool Unlocked);

public sealed record UiState(
    string? Image,
    IReadOnlyList<SlotInfo> Slots,
    GameState Game,
    string Message = "",
    string? Error = null);

/// <summary>
/// The operations behind the desktop window: read the profiles, unlock, restore,
/// export, import and apply a set of lock flags.
/// </summary>
public sealed class EditorSession
{
    private readonly string? _image;
    private readonly string? _iso;
    private readonly bool _backup;
    private readonly GameFiles _game = new(AppPaths.SaveDir);

    /// <param name="backup">
    /// Keep a timestamped copy of the image before writing. The phone app turns this
    /// off: it edits its own copy, and copying a multi-gigabyte image on a phone is
    /// not worth it when the original file is only replaced on an explicit write-back.
    /// </param>
    public EditorSession(string? image = null, string? iso = null, bool backup = true)
    {
        _image = image;
        _iso = iso;
        _backup = backup;
    }

    public string ImagePath => Xemu.ResolveImage(_image);

    public string? IsoPath => Xemu.FindIso(_iso);

    public UiState Load(string message = "", string? error = null)
    {
        var path = Xemu.ResolveImage(_image);
        List<PscSave> saves;
        using (var disk = DiskImageFactory.Open(path))
        {
            saves = PscFormat.FindSaves(disk);
        }
        var slots = saves
            .Select(save => new SlotInfo(save.TextOffset, save.Fields, ProfileEditor.IsUnlocked(save.Fields)))
            .ToList();
        return new UiState(path, slots, _game.State(IsoPath), message, error);
    }

    public UiState Unlock()
    {
        var path = Xemu.ResolveImage(_image);
        var message = string.Empty;
        string? error = null;
        using (var disk = DiskImageFactory.Open(path))
        {
            var saves = PscFormat.FindSaves(disk);
            if (saves.Count == 0)
            {
                error = "no save found";
            }
            else
            {
                var result = ProfileEditor.Unlock(disk, saves, AppPaths.BaselineDir, doBackup: _backup);
                message = $"Patched {result.Results.Count} save(s).";
                if (result.Baselines.Count > 0)
                {
                    message += " Original saved to saves/original.";
                }
                if (result.BackupPath is not null)
                {
                    message += " Backup: " + Path.GetFileName(result.BackupPath) + ".";
                }
                if (IsoPath is { } iso)
                {
                    message += _game.PatchGame(iso)
                        ? " Game files patched."
                        : " Game files were already patched.";
                    message += " Start the game once so it writes the profile.";
                }
                else
                {
                    message += " No game ISO found (pass --iso), so the game will rebuild its " +
                               "profile on the next boot.";
                }
            }
        }
        return Load(message, error);
    }

    public UiState Restore()
    {
        var path = Xemu.ResolveImage(_image);
        var baselines = SaveStore.LoadBaselines(AppPaths.BaselineDir);
        if (baselines.Count == 0)
        {
            return Load(error: "no original saved yet - unlock once first");
        }
        var message = string.Empty;
        string? error = null;
        using (var disk = DiskImageFactory.Open(path))
        {
            var saves = PscFormat.FindSaves(disk);
            if (saves.Count == 0)
            {
                error = "no save found";
            }
            else
            {
                var (results, backupPath) = ProfileEditor.Restore(disk, saves, baselines, doBackup: _backup);
                message = $"Restored the original values in {results.Count} save(s).";
                if (results.Any(result => result.Mode == "rebuild"))
                {
                    message += " The game rebuilds the profile on the next boot.";
                }
                if (backupPath is not null)
                {
                    message += " Backup: " + Path.GetFileName(backupPath) + ".";
                }
            }
        }
        if (error is null && IsoPath is { } iso && _game.RevertGame(iso))
        {
            message += " Original game startup config restored.";
        }
        return Load(message, error);
    }

    public UiState Export()
    {
        var path = Xemu.ResolveImage(_image);
        var names = new List<string>();
        using (var disk = DiskImageFactory.Open(path))
        {
            var saves = PscFormat.FindSaves(disk);
            if (saves.Count == 0)
            {
                return Load(error: "no save found");
            }
            Directory.CreateDirectory(AppPaths.SaveDir);
            for (var i = 0; i < saves.Count; i++)
            {
                var destination = Path.Combine(AppPaths.SaveDir, SaveStore.SlotName(i, saves.Count));
                SaveStore.Dump(saves[i], destination);
                names.Add(destination);
            }
        }
        return Load("Exported to " + string.Join(", ", names));
    }

    public UiState Import(string file, int slot)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return Load(error: "no profile file given");
        }
        var path = Xemu.ResolveImage(_image);
        var result = SaveStore.Import(path, file, slot, backup: _backup);
        return Load($"Wrote {Path.GetFileName(file)} into xemu slot {result.Slot + 1}. " +
                    "Start the game to load it.");
    }

    public UiState Apply(IReadOnlyDictionary<string, bool> wanted, string slotLabel = "")
    {
        var iso = IsoPath;
        if (iso is null)
        {
            return Load(error: "no game ISO found - start the tool with --iso");
        }
        var masks = _game.DefaultLockMasks(iso, SaveStore.LoadBaselines(AppPaths.BaselineDir));
        var target = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in GameFiles.Keys)
        {
            if (wanted.TryGetValue(key, out var open))
            {
                target[key] = open ? 0 : masks[key];
            }
        }
        if (target.Count == 0)
        {
            return Load(error: "no lock flags selected");
        }

        var path = Xemu.ResolveImage(_image);
        List<EditResult> results;
        using (var disk = DiskImageFactory.Open(path))
        {
            var saves = PscFormat.FindSaves(disk);
            if (saves.Count == 0)
            {
                return Load(error: "no save found");
            }
            _game.PatchGame(iso, target);
            results = ProfileEditor.ApplyCombination(disk, saves, target).ToList();
        }

        var opened = GameFiles.Keys
            .Where(key => target.TryGetValue(key, out var value) && value == 0)
            .Select(key => key.Replace("LockFlags", "", StringComparison.Ordinal))
            .ToList();
        var message = opened.Count > 0
            ? "Unlocked: " + string.Join(", ", opened) + "."
            : "Everything switched back to the game's defaults.";
        if (results.Any(result => result.Mode == "rebuild"))
        {
            message += " The game rebuilds the profile on the next boot.";
        }
        message += " Start the game once so it writes it.";
        return Load(message);
    }
}
