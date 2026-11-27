namespace CDSaveEditor;

/// <summary>An expected failure that should be shown to the user, not a stack trace.</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message)
    {
    }
}

/// <summary>Where the tool keeps its own data (baselines, game patch manifest).</summary>
public static class AppPaths
{
    private static string? _dataDir;

    public static string DataDir => _dataDir ??= ResolveDataDir();

    public static string SaveDir => Path.Combine(DataDir, "saves");

    public static string BaselineDir => Path.Combine(SaveDir, "original");

    public static string GamePatchFile => Path.Combine(SaveDir, "game_patch.json");

    /// <summary>Overrides the data folder (used by tests).</summary>
    public static void UseDataDir(string path) => _dataDir = Path.GetFullPath(path);

    /// <summary>
    /// The folder next to the executable, except while running from a build tree:
    /// there the repository root (the folder with the project file or saves/) is used,
    /// so the CLI and a published build share the same baselines.
    /// </summary>
    private static string ResolveDataDir()
    {
        var configured = Environment.GetEnvironmentVariable("CD_SAVEEDITOR_DATA");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "saves"))
                || File.Exists(Path.Combine(dir.FullName, "CD-SaveEditor.csproj")))
            {
                return dir.FullName;
            }
        }
        return AppContext.BaseDirectory;
    }
}
