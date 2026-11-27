using System.Globalization;
using CDSaveEditor.Game;
using CDSaveEditor.Gui;
using CDSaveEditor.LiveSplit;
using CDSaveEditor.Saves;
using CDSaveEditor.Storage;

namespace CDSaveEditor;

/// <summary>Command line front end.</summary>
public static class Cli
{
    private sealed class Options
    {
        public string? Image;
        public string? Iso;
        public string? Out;
        public string? File;
        public int Slot;
        public int Port = 8765;
        public int? ServePort;
        public string? Connect;
        public string? Lss;
        public double Interval = 1.0;
        public string EventDigit = Arenas.SmackEvent;
        public bool DryRun;
        public bool NoBackup;
        public bool Demo;
        public string? UiScreenshot;
        public string? MobileScreenshot;
    }

    public static int Run(string[] args)
    {
        try
        {
            return Dispatch(args);
        }
        catch (ToolException exception)
        {
            Console.Error.WriteLine("error: " + exception.Message);
            return 2;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or InvalidDataException or FormatException)
        {
            Console.Error.WriteLine("error: " + exception.Message);
            return 1;
        }
    }

    private static int Dispatch(string[] args)
    {
        var options = new Options();
        string? command = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp();
                    return 0;
                case "--version":
                    Console.WriteLine("CD-SaveEditor " + (typeof(Cli).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"));
                    return 0;
                case "--image":
                    options.Image = Next(args, ref i, arg);
                    break;
                case "--iso":
                    options.Iso = Next(args, ref i, arg);
                    break;
                case "--out":
                    options.Out = Next(args, ref i, arg);
                    break;
                case "--slot":
                    options.Slot = int.Parse(Next(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--port":
                    options.Port = int.Parse(Next(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--serve":
                    options.ServePort = int.Parse(Next(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--connect":
                    options.Connect = Next(args, ref i, arg);
                    break;
                case "--lss":
                    options.Lss = Next(args, ref i, arg);
                    break;
                case "--interval":
                    options.Interval = double.Parse(Next(args, ref i, arg), CultureInfo.InvariantCulture);
                    break;
                case "--event":
                    options.EventDigit = Next(args, ref i, arg);
                    break;
                case "--demo":
                    options.Demo = true;
                    break;
                case "--no-backup":
                    options.NoBackup = true;
                    break;
                case "--ui-screenshot":
                    options.UiScreenshot = Next(args, ref i, arg);
                    break;
                case "--mobile-screenshot":
                    options.MobileScreenshot = Next(args, ref i, arg);
                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        throw new ToolException($"unknown option {arg} (try --help)");
                    }
                    if (command is null)
                    {
                        command = arg;
                    }
                    else if (options.File is null)
                    {
                        options.File = arg;
                    }
                    else
                    {
                        throw new ToolException($"unexpected argument {arg}");
                    }
                    break;
            }
        }

        return command switch
        {
            "status" => Status(options),
            "export" => Export(options),
            "import" => Import(options),
            "unlock" => Unlock(options),
            "restore" => Restore(options),
            "split" or "livesplit" => Split(options),
            "gui" or null => GuiHost.Run(options.Image, options.Iso, options.UiScreenshot,
                options.MobileScreenshot),
            "serve" => throw new ToolException(
                "the web UI was removed; start CD-SaveEditor with no command for the desktop app"),
            _ => throw new ToolException($"unknown command {command} (try --help)"),
        };
    }

    private static int Split(Options options) => Splitter.Run(new SplitterOptions
    {
        Image = options.Image,
        ServePort = options.ServePort,
        Connect = options.Connect,
        DryRun = options.DryRun,
        Demo = options.Demo,
        Interval = options.Interval,
        EventDigit = options.EventDigit,
        Lss = options.Lss,
    });

    private static string Next(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ToolException($"{option} needs a value");
        }
        return args[++index];
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            CD-SaveEditor - Cel Damage save editor for xemu Xbox HDD images

            Usage:
              CD-SaveEditor                     start the local web UI
              CD-SaveEditor status              show the saves and the game file state
              CD-SaveEditor export [--out DIR]  write the profiles out as .psc files
              CD-SaveEditor import FILE [--slot N]
                                                write a .psc file into the xemu HDD image
              CD-SaveEditor unlock [--dry-run]  unlock everything, in place
              CD-SaveEditor restore [--dry-run] put the original values back
              CD-SaveEditor split [--dry-run | --serve PORT | --connect HOST:PORT]
                                                watch the profile and split LiveSplit
              CD-SaveEditor gui                  start the desktop window (the default)

            Options:
              --image FILE   HDD image (qcow2 or raw); defaults to xemu's hdd_path
              --iso FILE     game ISO; defaults to xemu's dvd_path
              --out DIR      where export writes the .psc files (default: ./saves)
              --slot N       profile slot for import (default: 1)
              --no-backup    skip the timestamped copy of the HDD image
              --dry-run      show what would change without writing

            Split options:
              --serve PORT   host a WebSocket server for LiveSplit One
              --connect HOST:PORT
                             connect to LiveSplit's TCP server
              --event N      event digit that counts as a win: 0 smack, 1 race,
                             2 flag (default 0)
              --interval N   seconds between progress checks (default 1.0)
              --demo         send start, two splits and a reset, then exit
              --lss PATH     write the All Smacks splits file and exit
            """);
    }

    private static Action<double> ProgressPrinter(string label)
    {
        var last = -1;
        return fraction =>
        {
            var percent = (int)(fraction * 100);
            if (percent != last && percent % 5 == 0)
            {
                last = percent;
                Console.Error.Write($"\r{label} {percent,3}%");
            }
        };
    }

    private static void Describe(IEnumerable<SaveField> fields)
    {
        foreach (var field in fields)
        {
            Console.WriteLine($"  {field.Key,-24} {field.Value}");
        }
    }

    private static void DescribeGame(string? iso)
    {
        var game = new GameFiles(AppPaths.SaveDir);
        var path = Xemu.FindIso(iso);
        if (path is null)
        {
            Console.WriteLine("game: no ISO found (pass --iso)");
            return;
        }
        var state = game.State(path);
        Console.WriteLine($"game: {path}");
        if (!state.Found)
        {
            Console.WriteLine("  startup config: not found");
            return;
        }
        Console.WriteLine($"  startup config: {(state.Patched ? "patched" : "original")}");
        foreach (var (key, value) in state.Values)
        {
            Console.WriteLine($"    {key,-22} {value}");
        }
    }

    private static int Status(Options options)
    {
        var path = Xemu.ResolveImage(options.Image);
        using var image = DiskImageFactory.Open(path);
        Console.WriteLine($"image: {path} ({image.Size / (double)(1 << 30):F1} GiB)");
        var saves = PscFormat.FindSaves(image, ProgressPrinter("scanning"));
        Console.Error.WriteLine();
        Console.WriteLine($"found {saves.Count} Cel Damage save(s)");
        for (var i = 0; i < saves.Count; i++)
        {
            var save = saves[i];
            Console.WriteLine();
            Console.WriteLine($"{SaveStore.SlotName(i, saves.Count)}  @ 0x{save.TextOffset:x}  ({save.Length} bytes of text)");
            Describe(save.Fields);
            Console.WriteLine($"  unlocked: {(ProfileEditor.IsUnlocked(save.Fields) ? "yes" : "no")}");
        }
        Console.WriteLine();
        DescribeGame(options.Iso);
        return 0;
    }

    private static int Export(Options options)
    {
        var path = Xemu.ResolveImage(options.Image);
        using var image = DiskImageFactory.Open(path);
        var saves = PscFormat.FindSaves(image, ProgressPrinter("scanning"));
        Console.Error.WriteLine();
        if (saves.Count == 0)
        {
            Console.WriteLine($"no Cel Damage save found in {path}");
            return 1;
        }
        var directory = options.Out ?? AppPaths.SaveDir;
        Directory.CreateDirectory(directory);
        for (var i = 0; i < saves.Count; i++)
        {
            var name = SaveStore.SlotName(i, saves.Count);
            var destination = Path.Combine(directory, name);
            SaveStore.Dump(saves[i], destination);
            Console.WriteLine($"exported {name} -> {destination}");
        }
        return 0;
    }

    private static int Import(Options options)
    {
        if (string.IsNullOrWhiteSpace(options.File))
        {
            throw new ToolException("import needs a profile file: CD-SaveEditor import <file.psc> [--slot N]");
        }
        var path = Xemu.ResolveImage(options.Image);
        var result = SaveStore.Import(path, options.File, options.Slot, backup: !options.NoBackup);
        Console.WriteLine($"wrote {options.File} into {path} at slot {result.Slot + 1} (0x{result.Offset:x}, {result.Length} bytes)");
        if (result.BackupPath is not null)
        {
            Console.WriteLine($"backup: {result.BackupPath}");
        }
        Console.WriteLine("start the game to load it");
        return 0;
    }

    private static int Unlock(Options options)
    {
        var path = Xemu.ResolveImage(options.Image);
        using var image = DiskImageFactory.Open(path);
        var saves = PscFormat.FindSaves(image, ProgressPrinter("scanning"));
        Console.Error.WriteLine();
        if (saves.Count == 0)
        {
            Console.WriteLine($"no Cel Damage save found in {path}");
            return 1;
        }

        var result = ProfileEditor.Unlock(image, saves, AppPaths.BaselineDir,
            dryRun: options.DryRun, doBackup: !options.NoBackup,
            progress: ProgressPrinter("writing"));
        Console.Error.WriteLine();
        Console.WriteLine(options.DryRun
            ? "dry run - nothing written"
            : $"patched {result.Results.Count} save(s)");

        var game = new GameFiles(AppPaths.SaveDir);
        var iso = Xemu.FindIso(options.Iso);
        if (iso is null)
        {
            Console.WriteLine("warning: no game ISO found, so the game will rebuild its profile " +
                              "on the next boot; pass --iso to patch the game files");
        }
        else
        {
            var changed = game.PatchGame(iso, dryRun: options.DryRun);
            Console.WriteLine("game files: " + (options.DryRun && changed
                ? "will be patched"
                : changed
                    ? "patched - new profiles are built unlocked"
                    : "already patched"));
            Console.WriteLine($"backup of the untampered ISO: {iso}.orig");
        }
        if (!options.DryRun)
        {
            Console.WriteLine("start the game once: it rewrites the profile with these values " +
                              "and a checksum it accepts");
        }
        if (result.Baselines.Count > 0)
        {
            Console.WriteLine($"original saved to: {AppPaths.BaselineDir}");
        }
        if (result.BackupPath is not null)
        {
            Console.WriteLine($"backup: {result.BackupPath}");
        }
        foreach (var edit in result.Results)
        {
            Console.WriteLine();
            Console.WriteLine($"@ 0x{edit.Offset:x}");
            Describe(edit.After);
        }
        return 0;
    }

    private static int Restore(Options options)
    {
        var path = Xemu.ResolveImage(options.Image);
        var baselines = SaveStore.LoadBaselines(AppPaths.BaselineDir);
        if (baselines.Count == 0)
        {
            Console.WriteLine($"no saved original in {AppPaths.BaselineDir} - run unlock once first");
            return 1;
        }

        IReadOnlyList<EditResult> results;
        string? backupPath;
        using (var image = DiskImageFactory.Open(path))
        {
            var saves = PscFormat.FindSaves(image, ProgressPrinter("scanning"));
            Console.Error.WriteLine();
            if (saves.Count == 0)
            {
                Console.WriteLine($"no Cel Damage save found in {path}");
                return 1;
            }
            (results, backupPath) = ProfileEditor.Restore(image, saves, baselines,
                dryRun: options.DryRun, doBackup: !options.NoBackup,
                progress: ProgressPrinter("restoring"));
        }
        Console.Error.WriteLine();
        Console.WriteLine(options.DryRun
            ? "dry run - nothing written"
            : $"restored {results.Count} save(s)");

        var game = new GameFiles(AppPaths.SaveDir);
        var iso = Xemu.FindIso(options.Iso);
        if (iso is not null && game.RevertGame(iso, dryRun: options.DryRun))
        {
            Console.WriteLine("game files: original startup config restored");
        }
        if (!options.DryRun)
        {
            Console.WriteLine("start the game once so it rebuilds the profile with the original values");
        }
        if (backupPath is not null)
        {
            Console.WriteLine($"backup: {backupPath}");
        }
        foreach (var edit in results)
        {
            Console.WriteLine();
            Console.WriteLine($"@ 0x{edit.Offset:x}");
            if (edit.Mode == "rebuild")
            {
                Console.WriteLine("  profile was rewritten by the game: it will rebuild the original " +
                                  "values on the next boot");
            }
            Describe(edit.After);
        }
        return 0;
    }
}
