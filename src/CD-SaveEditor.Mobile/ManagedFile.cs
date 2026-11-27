using System.Globalization;
using Avalonia.Platform.Storage;

namespace CDSaveEditor.Mobile;

/// <summary>
/// A file the user picked through the system picker. The editable copy lives in the
/// app's own storage, because Android hands out content URIs rather than paths; the
/// original is kept so the result can be written back over it.
/// </summary>
public sealed class ManagedFile
{
    private ManagedFile(string name, string localPath, IStorageFile? source)
    {
        Name = name;
        LocalPath = localPath;
        Source = source;
    }

    public string Name { get; }

    public string LocalPath { get; }

    /// <summary>The picked file, where "write back" sends the result.</summary>
    public IStorageFile? Source { get; }

    public bool Dirty { get; set; }

    public long Length => new FileInfo(LocalPath).Length;

    /// <summary>A file already on disk (used by the desktop preview of this view).</summary>
    public static ManagedFile FromLocalPath(string path) =>
        new(Path.GetFileName(path), path, null);

    public static async Task<ManagedFile> CopyInAsync(IStorageFile file, string role)
    {
        var directory = Path.Combine(AppPaths.DataDir, "files");
        Directory.CreateDirectory(directory);
        var extension = Path.GetExtension(file.Name);
        var local = Path.Combine(directory, role + (extension.Length > 0 ? extension : ".img"));
        await using (var input = await file.OpenReadAsync())
        await using (var output = new FileStream(local, FileMode.Create, FileAccess.Write,
                       FileShare.None, 1 << 20, useAsync: true))
        {
            await input.CopyToAsync(output, 1 << 20);
        }
        return new ManagedFile(file.Name, local, file);
    }

    public async Task WriteToAsync(IStorageFile target)
    {
        await using var input = new FileStream(LocalPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 1 << 20, useAsync: true);
        await using var output = await target.OpenWriteAsync();
        await input.CopyToAsync(output, 1 << 20);
        await output.FlushAsync();
    }

    public static string Describe(long bytes)
    {
        string[] units = { "B", "KiB", "MiB", "GiB", "TiB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{value:0.#} {units[unit]}");
    }
}
