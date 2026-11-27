using System.Text.RegularExpressions;

namespace CDSaveEditor.Storage;

/// <summary>Finds the paths xemu itself uses, so the user does not have to pass them.</summary>
public static class Xemu
{
    private static IEnumerable<string> ConfigCandidates()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(home, ".var", "app", "app.xemu.xemu", "data", "xemu", "xemu", "xemu.toml");
            yield return Path.Combine(home, ".config", "xemu", "xemu.toml");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "xemu", "xemu", "xemu.toml");
        }
        foreach (var appData in new[]
                 {
                     Environment.GetEnvironmentVariable("APPDATA"),
                     Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(appData))
            {
                yield return Path.Combine(appData, "xemu", "xemu", "xemu.toml");
                yield return Path.Combine(appData, "xemu", "xemu.toml");
            }
        }
    }

    private static IEnumerable<string> DefaultImages()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsLinux())
        {
            yield return Path.Combine(home, "Music", "Xbox-Emulator-Files", "hdd", "xbox_hdd.qcow2");
            yield return Path.Combine(home, ".var", "app", "app.xemu.xemu", "data", "xemu", "xemu", "hdd", "xbox_hdd.qcow2");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(home, "Library", "Application Support", "xemu", "xemu", "hdd", "xbox_hdd.qcow2");
        }
        else
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(appData))
            {
                yield return Path.Combine(appData, "xemu", "xemu", "hdd", "xbox_hdd.qcow2");
            }
        }
    }

    private static string? TomlValue(string key)
    {
        var pattern = new Regex(@"^\s*" + Regex.Escape(key) + @"\s*=\s*['""]([^'""]+)['""]",
            RegexOptions.CultureInvariant);
        foreach (var config in ConfigCandidates())
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(config);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var line in lines)
            {
                var match = pattern.Match(line);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        return null;
    }

    /// <summary>The HDD image from xemu's own configuration, if it is readable.</summary>
    public static string? ImageFromConfig() => TomlValue("hdd_path");

    public static string? IsoFromConfig() => TomlValue("dvd_path");

    public static string? FindImage(string? explicitPath)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }
        candidates.Add(Environment.GetEnvironmentVariable("CELCD_IMAGE"));
        candidates.Add(ImageFromConfig());
        candidates.AddRange(DefaultImages());
        return FirstExisting(candidates);
    }

    public static string? FindIso(string? explicitPath)
    {
        var candidates = new List<string?>();
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            candidates.Add(explicitPath);
        }
        candidates.Add(Environment.GetEnvironmentVariable("CELCD_ISO"));
        candidates.Add(IsoFromConfig());
        return FirstExisting(candidates);
    }

    public static string ResolveImage(string? explicitPath) =>
        FindImage(explicitPath)
        ?? throw new ToolException(
            "No Xbox HDD image found. Pass --image /path/to/xbox_hdd.qcow2 or set CELCD_IMAGE.");

    public static string ResolveIso(string? explicitPath) =>
        FindIso(explicitPath)
        ?? throw new ToolException(
            "No Cel Damage ISO found. Pass --iso /path/to/game.iso or set CELCD_ISO.");

    private static string? FirstExisting(IEnumerable<string?> candidates)
    {
        foreach (var path in candidates)
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                return path;
            }
        }
        return null;
    }
}
