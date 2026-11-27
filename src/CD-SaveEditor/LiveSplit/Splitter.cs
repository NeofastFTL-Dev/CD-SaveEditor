using System.Globalization;

namespace CDSaveEditor.LiveSplit;

public sealed class SplitterOptions
{
    public string? Image;
    public int? ServePort;
    public string? Connect;
    public bool DryRun;
    public bool Demo;
    public double Interval = 1.0;
    public string EventDigit = Arenas.SmackEvent;
    public string? Lss;
}

/// <summary>
/// LiveSplit bridge: watches the Smack Attack win flags in the game's own profile and
/// drives a timer through LiveSplit's TCP server or LiveSplit One's WebSocket.
/// </summary>
public static class Splitter
{
    public static string Stamp() =>
        DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static int Run(SplitterOptions options)
    {
        if (options.Lss is not null)
        {
            LssFile.Write(options.Lss);
            Console.WriteLine($"wrote {options.Lss} with {Arenas.Count} splits: "
                              + string.Join(", ", Arenas.SmackArenas));
            return 0;
        }

        using var timer = CreateTimer(options);
        if (options.Demo)
        {
            return Demo(timer);
        }

        using var profiles = new Profiles(options.Image, options.EventDigit);
        Console.WriteLine(
            $"watching {profiles.Path} at {string.Join(", ", profiles.Offsets.Select(Profiles.FormatOffset))} "
            + $"for smack wins (arenaWonFlags<arena>{options.EventDigit})");

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Cancel();
        };

        Dictionary<long, HashSet<string>>? known = null;
        while (!stop.IsCancellationRequested)
        {
            var states = profiles.Read();
            if (states is not null)
            {
                var (baseline, fresh, cleared) = Diff(known, states);
                if (baseline)
                {
                    Console.WriteLine($"baseline wins: {Describe(states)}");
                }
                else
                {
                    if (fresh.Count > 0)
                    {
                        Console.WriteLine($"{Stamp()}  split: "
                                          + string.Join(", ", fresh.Select(key => $"{key} ({Arenas.ArenaName(key)})")));
                        timer.Send("splitOrStart");
                    }
                    if (cleared.Count > 0)
                    {
                        Console.WriteLine($"{Stamp()}  progress cleared: {string.Join(", ", cleared)}");
                    }
                }
                known = states;
            }
            stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(options.Interval));
        }
        Console.WriteLine("stopped");
        return 0;
    }

    /// <summary>
    /// The first read is a baseline; a save slot appearing or disappearing is one too.
    /// Anything else reports the flags that were set and cleared since the last poll.
    /// </summary>
    public static (bool Baseline, IReadOnlyList<string> Fresh, IReadOnlyList<string> Cleared) Diff(
        IReadOnlyDictionary<long, HashSet<string>>? known,
        IReadOnlyDictionary<long, HashSet<string>> states)
    {
        if (known is null || !known.Keys.ToHashSet().SetEquals(states.Keys))
        {
            return (true, Array.Empty<string>(), Array.Empty<string>());
        }
        var fresh = new List<string>();
        var cleared = new List<string>();
        foreach (var (offset, won) in states)
        {
            fresh.AddRange(won.Except(known[offset]).OrderBy(key => key, StringComparer.Ordinal));
            cleared.AddRange(known[offset].Except(won).OrderBy(key => key, StringComparer.Ordinal));
        }
        return (false, fresh, cleared);
    }

    public static string Describe(IReadOnlyDictionary<long, HashSet<string>> states) =>
        string.Join(", ", states.Select(entry =>
            $"{Profiles.FormatOffset(entry.Key)}: {entry.Value.Count}/{Arenas.Count}"));

    public static (string Host, int Port) ParseHostPort(string value)
    {
        var index = value.LastIndexOf(':');
        if (index <= 0 || !int.TryParse(value[(index + 1)..], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var port))
        {
            throw new ToolException($"\"{value}\" is not HOST:PORT");
        }
        var host = value[..index];
        return (host.Length == 0 ? "127.0.0.1" : host, port);
    }

    private static ITimer CreateTimer(SplitterOptions options)
    {
        if (options.DryRun)
        {
            return new PrinterTimer();
        }
        if (options.ServePort is int port)
        {
            return new WebSocketTimer("127.0.0.1", port);
        }
        if (options.Connect is { } target)
        {
            var (host, number) = ParseHostPort(target);
            return new TcpTimer(host, number);
        }
        throw new ToolException("choose --dry-run, --serve PORT, or --connect HOST:PORT");
    }

    private static int Demo(ITimer timer)
    {
        // Give a human time to connect LiveSplit One before the demo runs.
        if (timer is WebSocketTimer server && !server.WaitForClient(TimeSpan.FromSeconds(5)))
        {
            Console.WriteLine("demo: nobody connected");
        }
        foreach (var command in new[] { "splitOrStart", "splitOrStart", "splitOrStart", "reset" })
        {
            timer.Send(command);
            Thread.Sleep(400);
        }
        return 0;
    }
}
