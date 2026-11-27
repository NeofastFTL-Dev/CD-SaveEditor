using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Serialization;
using CDSaveEditor.Game;
using CDSaveEditor.Saves;
using CDSaveEditor.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;

namespace CDSaveEditor;

/// <summary>The local web UI (ASP.NET Core on loopback).</summary>
public static class WebUi
{
    public static int Run(string? image, string? iso, int port, bool openBrowser)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        var page = LoadPage();
        var game = new GameFiles(AppPaths.SaveDir);

        IResult State(string message = "", string error = "") =>
            Results.Json(BuildState(image, iso, game, message, error));

        IResult Failed(Exception exception) =>
            Results.Json(new Dictionary<string, object?>
            {
                ["error"] = exception.Message,
                ["saves"] = Array.Empty<object>(),
            }, statusCode: 500);

        app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));
        app.MapGet("/index.html", () => Results.Content(page, "text/html; charset=utf-8"));

        app.MapGet("/api/status", () =>
        {
            try
            {
                return State();
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        });

        app.MapPost("/api/export", () =>
        {
            try
            {
                var path = Xemu.ResolveImage(image);
                using var disk = DiskImageFactory.Open(path);
                var saves = PscFormat.FindSaves(disk);
                if (saves.Count == 0)
                {
                    return State(error: "no save found");
                }
                Directory.CreateDirectory(AppPaths.SaveDir);
                var names = new List<string>();
                for (var i = 0; i < saves.Count; i++)
                {
                    var destination = Path.Combine(AppPaths.SaveDir, SaveStore.SlotName(i, saves.Count));
                    SaveStore.Dump(saves[i], destination);
                    names.Add(destination);
                }
                return State("Exported to " + string.Join(", ", names));
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        });

        app.MapPost("/api/import", async (HttpRequest request) =>
        {
            try
            {
                var body = await request.ReadFromJsonAsync<ImportRequest>() ?? new ImportRequest();
                if (string.IsNullOrWhiteSpace(body.File))
                {
                    return State(error: "no profile file given");
                }
                var path = Xemu.ResolveImage(image);
                var result = SaveStore.Import(path, body.File, body.Slot);
                return State($"Wrote {Path.GetFileName(body.File)} into xemu slot {result.Slot + 1}. " +
                             "Start the game to load it.");
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        });

        app.MapPost("/api/unlock", () =>
        {
            try
            {
                var path = Xemu.ResolveImage(image);
                using var disk = DiskImageFactory.Open(path);
                var saves = PscFormat.FindSaves(disk);
                if (saves.Count == 0)
                {
                    return State(error: "no save found");
                }
                var result = ProfileEditor.Unlock(disk, saves, AppPaths.BaselineDir);
                disk.Dispose();

                var message = $"Patched {result.Results.Count} save(s).";
                if (result.Baselines.Count > 0)
                {
                    message += " Original saved to saves/original.";
                }
                if (result.BackupPath is not null)
                {
                    message += " Backup: " + Path.GetFileName(result.BackupPath) + ".";
                }
                var isoPath = Xemu.FindIso(iso);
                if (isoPath is not null)
                {
                    var patched = game.PatchGame(isoPath);
                    message += patched
                        ? " Game files patched."
                        : " Game files were already patched.";
                    message += " Start the game once so it writes the profile.";
                }
                else
                {
                    message += " No game ISO found (pass --iso), so the game will rebuild its " +
                               "profile on the next boot.";
                }
                return State(message);
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        });

        app.MapPost("/api/apply", async (HttpRequest request) =>
        {
            try
            {
                var wanted = await request.ReadFromJsonAsync<Dictionary<string, bool>>()
                             ?? new Dictionary<string, bool>();
                var isoPath = Xemu.FindIso(iso);
                var baselines = SaveStore.LoadBaselines(AppPaths.BaselineDir);
                var masks = isoPath is null
                    ? GameFiles.Keys.ToDictionary(key => key, _ => -1L)
                    : game.DefaultLockMasks(isoPath, baselines);
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
                    return State(error: "no lock flags in the request");
                }
                if (isoPath is null)
                {
                    return State(error: "no game ISO found - start the tool with --iso");
                }

                var path = Xemu.ResolveImage(image);
                using var disk = DiskImageFactory.Open(path);
                var saves = PscFormat.FindSaves(disk);
                if (saves.Count == 0)
                {
                    return State(error: "no save found");
                }
                game.PatchGame(isoPath, target);
                var results = ProfileEditor.ApplyCombination(disk, saves, target);
                disk.Dispose();

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
                return State(message);
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        });

        app.MapPost("/api/restore", () =>
        {
            try
            {
                var baselines = SaveStore.LoadBaselines(AppPaths.BaselineDir);
                if (baselines.Count == 0)
                {
                    return State(error: "no original saved yet - unlock once first");
                }
                var path = Xemu.ResolveImage(image);
                string message;
                using (var disk = DiskImageFactory.Open(path))
                {
                    var saves = PscFormat.FindSaves(disk);
                    if (saves.Count == 0)
                    {
                        return State(error: "no save found");
                    }
                    var (results, backupPath) = ProfileEditor.Restore(disk, saves, baselines);
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
                var isoPath = Xemu.FindIso(iso);
                if (isoPath is not null && game.RevertGame(isoPath))
                {
                    message += " Original game startup config restored.";
                }
                return State(message);
            }
            catch (Exception exception)
            {
                return Failed(exception);
            }
        });

        Console.WriteLine($"image: {Xemu.ResolveImage(image)}");
        if (Xemu.FindIso(iso) is { } gameIso)
        {
            Console.WriteLine($"game: {gameIso}");
        }
        var url = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"open {url}");
        if (openBrowser)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.WriteLine("could not open a browser: " + exception.Message);
            }
        }
        app.Run();
        return 0;
    }

    private static Dictionary<string, object?> BuildState(
        string? image, string? iso, GameFiles game, string message = "", string error = "")
    {
        var path = Xemu.ResolveImage(image);
        List<PscSave> saves;
        using (var disk = DiskImageFactory.Open(path))
        {
            saves = PscFormat.FindSaves(disk);
        }
        var isoPath = Xemu.FindIso(iso);
        var state = game.State(isoPath);

        return new Dictionary<string, object?>
        {
            ["image"] = path,
            ["saves"] = saves.Select(save => new Dictionary<string, object?>
            {
                ["offset"] = save.TextOffset,
                ["fields"] = save.Fields.Select(field => new object[] { field.Key, field.Value }).ToArray(),
                ["unlocked"] = ProfileEditor.IsUnlocked(save.Fields),
            }).ToList(),
            ["game"] = new Dictionary<string, object?>
            {
                ["path"] = state.Path,
                ["found"] = state.Found,
                ["offset"] = state.Offset,
                ["patched"] = state.Patched,
                ["values"] = state.Values,
            },
            ["message"] = message,
            ["error"] = error,
        };
    }

    private static string LoadPage()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(resource => resource.EndsWith("index.html", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("the web UI is missing from this build");
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record ImportRequest
    {
        [JsonPropertyName("file")]
        public string? File { get; init; }

        [JsonPropertyName("slot")]
        public int Slot { get; init; }
    }
}
