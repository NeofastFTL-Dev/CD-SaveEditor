using CDSaveEditor.Saves;
using CDSaveEditor.Storage;

namespace CDSaveEditor.LiveSplit;

/// <summary>Reads the Smack Attack win flags out of the live save files on the HDD image.</summary>
/// <remarks>
/// Each save slot is tracked separately: the game only maintains the slot in use and
/// leaves the other one stale, so a slot is never compared with its neighbour.
/// The image is opened read-only here and without buffering, so a handle that outlives
/// a poll always returns what is on disk now.
/// </remarks>
public sealed class Profiles : IDisposable
{
    private readonly string _eventDigit;
    private readonly HashSet<string> _fields;
    private IDiskImage _image;
    private List<long> _offsets;
    private int _misses;

    public Profiles(string? imagePath = null, string eventDigit = Arenas.SmackEvent)
    {
        _eventDigit = eventDigit;
        _fields = Arenas.WonFields(eventDigit);
        Path = Xemu.ResolveImage(imagePath);
        _image = DiskImageFactory.Open(Path);
        _offsets = FindOffsets();
    }

    public string Path { get; }

    public IReadOnlyList<long> Offsets => _offsets;

    /// <summary>{save offset: set of won arena flags}, or null if unreadable.</summary>
    public Dictionary<long, HashSet<string>>? Read()
    {
        var states = new Dictionary<long, HashSet<string>>();
        foreach (var offset in _offsets)
        {
            var save = PscFormat.TrySaveAt(_image, offset);
            if (save is null)
            {
                continue;
            }
            states[offset] = save.Fields
                .Where(field => field.Value != 0 && _fields.Contains(field.Key))
                .Select(field => field.Key)
                .ToHashSet(StringComparer.Ordinal);
        }
        if (states.Count > 0)
        {
            _misses = 0;
            return states;
        }
        _misses++;
        if (_misses >= 3)
        {
            Rescan();
        }
        return null;
    }

    /// <summary>Look again, on a new handle: the save may have been relocated.</summary>
    public void Rescan()
    {
        _misses = 0;
        _image.Dispose();
        _image = DiskImageFactory.Open(Path);
        var offsets = FindOffsets();
        if (offsets.Count > 0 && !offsets.SequenceEqual(_offsets))
        {
            Console.WriteLine("profile moved; watching "
                              + string.Join(", ", offsets.Select(FormatOffset)));
            _offsets = offsets;
        }
    }

    public void Dispose() => _image.Dispose();

    public static string FormatOffset(long offset) => "0x" + offset.ToString("x");

    private List<long> FindOffsets()
    {
        var saves = PscFormat.FindSaves(_image, Progress("scanning"));
        var offsets = saves.Select(save => save.TextOffset).ToList();
        if (offsets.Count > 0)
        {
            Console.WriteLine($"\r{offsets.Count} save(s) found");
            return offsets;
        }
        return SaveStore.LoadBaselines(AppPaths.BaselineDir)
            .Where(baseline => baseline.Offset is not null)
            .Select(baseline => baseline.Offset!.Value)
            .ToList();
    }

    private static Action<double> Progress(string label)
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
}
