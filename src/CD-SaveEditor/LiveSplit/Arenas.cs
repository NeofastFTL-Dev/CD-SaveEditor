using System.Globalization;
using CDSaveEditor.Saves;

namespace CDSaveEditor.LiveSplit;

/// <summary>The game's arena table and which of those arenas can record a win.</summary>
/// <remarks>
/// The game ships twelve arenas, each with the two numbers the level packs carry as
/// l0_ThemeNumber / l0_ArenaNumber: a theme (0 DS, 1 JG, 2 TR, 3 SP) and an arena
/// number 1-3 within that theme:
///
///   dw_DS2_Arena 0 1   dw_DS1_Arena 0 2   cf_DS4_Arena 0 3
///   cf_JG1_arena 1 1   cf_JG2_Arena 1 2   cf_JG3_Arena 1 3
///   dw_TR2_Arena 2 1   cf_TR3_Arena 2 2   dw_TR1_Arena 2 3
///   dw_SP3_Arena 3 1   dw_SP2_arena 3 2   dw_SP1_Arena 3 3
///
/// The arena names sit in the menu text file in that same order (twelve names, three
/// per theme), which is the order the game indexes arenas in: save digit =
/// theme * 3 + (arena number - 1).
///
/// The save only has room for one digit per arena: the engine registers exactly
/// thirty win fields, arenaWonFlags&lt;arena 0-9&gt;&lt;event 0-2&gt;. Ten arenas can
/// therefore record a Smack Attack win and two cannot - the last two of the table,
/// Space arenas 2 and 3. Any split list stops at those ten.
/// </remarks>
public static class Arenas
{
    /// <summary>Event ids as named by the game: 0 smack, 1 race, 2 flag.</summary>
    public const string SmackEvent = "0";

    public const int WonArenaSlots = 10;

    public static readonly string[][] Themes =
    {
        new[] { "Wild Wooly West", "Death Valley", "Mesa Madness" },        // DS 1-3
        new[] { "Swamp Stomp", "Temple of Boom", "Shmoe vs. the Volcano" }, // JG 1-3
        new[] { "Monster Mosh", "Count's Castle", "Bohemian Raspberry" },   // TR 1-3
        new[] { "Babbling 5", "Lunarcy", "Shooting Stars" },                // SP 1-3
    };

    public static readonly string[] All = Themes.SelectMany(theme => theme).ToArray();

    /// <summary>One entry per split: the save field that records the win, plus its name.</summary>
    public static readonly IReadOnlyList<(string Field, string Name)> SmackStages =
        Enumerable.Range(0, WonArenaSlots)
            .Select(arena => (
                PscFormat.WonPrefix + arena.ToString(CultureInfo.InvariantCulture) + SmackEvent,
                All[arena]))
            .ToArray();

    public static readonly string[] SmackArenas = SmackStages.Select(stage => stage.Name).ToArray();

    public static int Count => SmackStages.Count;

    /// <summary>The save fields a win can appear in, with the digit of <paramref name="eventDigit"/>.</summary>
    public static HashSet<string> WonFields(string eventDigit = SmackEvent) =>
        SmackStages.Select(stage => stage.Field[..^1] + eventDigit)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>True for a win flag of the given event type (arenaWonFlags&lt;arena&gt;&lt;event&gt;).</summary>
    public static bool IsWonFlag(string key, string eventDigit = SmackEvent) =>
        key.StartsWith(PscFormat.WonPrefix, StringComparison.Ordinal)
        && key.Length == PscFormat.WonPrefix.Length + 2
        && key[^1].ToString() == eventDigit;

    /// <summary>Arena that a won flag belongs to, e.g. arenaWonFlags30 -> "Swamp Stomp".</summary>
    public static string ArenaName(string key)
    {
        var digit = key.Length > PscFormat.WonPrefix.Length ? key[PscFormat.WonPrefix.Length] : '\0';
        if (char.IsAsciiDigit(digit) && digit - '0' < All.Length)
        {
            return All[digit - '0'];
        }
        return key;
    }
}
