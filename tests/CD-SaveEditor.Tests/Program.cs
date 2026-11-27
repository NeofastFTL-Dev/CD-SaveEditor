using System.Buffers.Binary;
using System.Text;
using CDSaveEditor;
using CDSaveEditor.Game;
using CDSaveEditor.LiveSplit;
using CDSaveEditor.Saves;
using CDSaveEditor.Storage;

// Dependency free smoke suite: `dotnet run --project tests/CD-SaveEditor.Tests`.
// It exercises the same code the CLI and web UI use, against synthetic images, so
// it never touches the user's real HDD.

var failures = 0;

void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail is null ? "" : ": " + detail)}");
    if (!ok)
    {
        failures++;
    }
}

string? FindUp(string fileName)
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (var depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
    {
        var candidate = Path.Combine(dir.FullName, fileName);
        if (File.Exists(candidate))
        {
            return candidate;
        }
    }
    return null;
}

static string ReadHeaders(Stream stream)
{
    var builder = new StringBuilder();
    var buffer = new byte[1024];
    while (!builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
    {
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read == 0)
        {
            break;
        }
        builder.Append(Encoding.ASCII.GetString(buffer, 0, read));
    }
    return builder.ToString();
}

static void ReadExact(Stream stream, byte[] buffer)
{
    var done = 0;
    while (done < buffer.Length)
    {
        var read = stream.Read(buffer, done, buffer.Length - done);
        if (read == 0)
        {
            throw new IOException("connection closed");
        }
        done += read;
    }
}

var root = Path.Combine(Path.GetTempPath(), "cd-saveeditor-tests-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);

try
{
    // ---------------------------------------------------------------- PSC format
    var text = SaveFixture.Text;
    var fields = PscFormat.Parse(text);
    Check("parses 37 fields", fields.Count == 37, fields.Count.ToString());
    Check("lock mask value", fields.Single(f => f.Key == "weaponLockFlags").Value == -8388611);

    var unlocked = ProfileEditor.UnlockedFields(fields);
    var rendered = PscFormat.Render(unlocked, text);
    Check("unlock keeps the text length", rendered.Length == text.Length,
        $"{rendered.Length} vs {text.Length}");
    Check("unlock zeroes the lock flags",
        PscFormat.Parse(rendered).Where(f => ProfileEditor.UnlockFields.Contains(f.Key)).All(f => f.Value == 0));
    Check("unlock sets the win flags",
        PscFormat.Parse(rendered).Where(f => f.Key.StartsWith(PscFormat.WonPrefix, StringComparison.Ordinal)).All(f => f.Value == 1));
    // the game writes two characters for -1, so 0 is padded to "00" to keep the size
    Check("values keep their original width",
        PscFormat.Render(new[] { new SaveField("arenaRaceLockFlags", 0) }, "arenaRaceLockFlags -1\r\n")
            == "arenaRaceLockFlags 00\r\n");
    try
    {
        PscFormat.Render(new[] { new SaveField("weaponLockFlags", -123456789) }, text);
        Check("rejects a value that does not fit", false);
    }
    catch (FormatException)
    {
        Check("rejects a value that does not fit", true);
    }

    // ------------------------------------------------------------- raw HDD image
    var rawPath = Path.Combine(root, "hdd.img");
    var rawBytes = new byte[0x8000];
    SaveFixture.Bytes().CopyTo(rawBytes, 0x2000);
    SaveFixture.Bytes().CopyTo(rawBytes, 0x6000);
    File.WriteAllBytes(rawPath, rawBytes);

    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        Check("finds both saves in a raw image", saves.Count == 2, saves.Count.ToString());
        // the scan reports where the text starts, 24 bytes past the header
        Check("save text offset", saves[0].TextOffset == 0x2018, $"0x{saves[0].TextOffset:x}");
        Check("no false positives on the needle", saves.All(s => s.Length == SaveFixture.Text.Length));
    }

    var baselineDir = Path.Combine(root, "saves", "original");
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        var result = ProfileEditor.Unlock(image, saves, baselineDir, doBackup: false);
        Check("unlock reports every save", result.Results.Count == 2);
        Check("baseline written", result.Baselines.Count == 2 && File.Exists(Path.Combine(baselineDir, "manifest.json")));
    }
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        Check("unlocked on disk", saves.All(s => ProfileEditor.IsUnlocked(s.Fields)));
        Check("text length preserved on disk", saves.All(s => s.Text.Length == SaveFixture.Text.Length));
    }
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        var baselines = SaveStore.LoadBaselines(baselineDir);
        var (results, _) = ProfileEditor.Restore(image, saves, baselines, doBackup: false);
        Check("restore writes both saves", results.Count == 2 && results.All(r => r.Written));
    }
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        Check("restored to the original bytes", saves[0].Text == SaveFixture.Text);
    }

    // apply a combination
    var masks = new Dictionary<string, long>
    {
        ["weaponLockFlags"] = 0,
        ["vehicleLockFlags"] = -64,
        ["arenaRaceLockFlags"] = -1,
        ["arenaFlagLockFlags"] = -1,
        ["arenaSmackLockFlags"] = -1,
    };
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        var results = ProfileEditor.ApplyCombination(image, saves, masks);
        Check("combination applied in place", results[0].Mode == "bytes" && results[0].Written);
    }
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var save = PscFormat.FindSaves(image)[0];
        var saveValues = save.Fields.ToDictionary(f => f.Key, f => f.Value);
        Check("weaponLockFlags unlocked, vehicle still locked",
            saveValues["weaponLockFlags"] == 0 && saveValues["vehicleLockFlags"] == -64,
            $"{saveValues["weaponLockFlags"]}/{saveValues["vehicleLockFlags"]}");
        Check("combination kept the text length", save.Text.Length == SaveFixture.Text.Length);
    }

    // ------------------------------------------------------- qcow2 read and write
    var qcowPath = Path.Combine(root, "hdd.qcow2");
    QCow2Fixture.Write(qcowPath, SaveFixture.Bytes(), guestOffset: 0x1000);
    using (var image = DiskImageFactory.Open(qcowPath))
    {
        Check("detects qcow2", image is QCow2Image);
        Check("reports the guest size", image.Size == 0x20000, image.Size.ToString());
        var saves = PscFormat.FindSaves(image);
        Check("finds the save through the cluster table", saves.Count == 1 && saves[0].TextOffset == 0x1018,
            saves.Count == 1 ? $"0x{saves[0].TextOffset:x}" : $"{saves.Count} save(s)");
        var zeros = image.ReadAt(0x10000, 16);
        Check("unallocated clusters read as zeros", zeros.All(b => b == 0));
        try
        {
            image.WriteAt(0x10000, new byte[4]);
            Check("refuses to write an unallocated cluster", false);
        }
        catch (IOException)
        {
            Check("refuses to write an unallocated cluster", true);
        }
    }
    using (var image = DiskImageFactory.Open(qcowPath))
    {
        var saves = PscFormat.FindSaves(image);
        ProfileEditor.Unlock(image, saves, Path.Combine(root, "saves2"), doBackup: false);
    }
    using (var image = DiskImageFactory.Open(qcowPath))
    {
        var save = PscFormat.FindSaves(image).Single();
        Check("unlocked through the qcow2 writer", ProfileEditor.IsUnlocked(save.Fields));
        Check("qcow2 write kept the text length", save.Text.Length == SaveFixture.Text.Length);
    }

    // ------------------------------------------------------------------- export
    var exportDir = Path.Combine(root, "export");
    Directory.CreateDirectory(exportDir);
    var exportedFile = Path.Combine(exportDir, "pitaPsc_slot1.psc");
    using (var image = DiskImageFactory.Open(rawPath))
    {
        SaveStore.Dump(PscFormat.FindSaves(image)[0], exportedFile);
    }
    Check("export writes a whole profile",
        File.Exists(exportedFile) && new FileInfo(exportedFile).Length == SaveFixture.Bytes().Length);

    // ------------------------------------------------------------------- import
    var editedBytes = File.ReadAllBytes(exportedFile);
    var editedText = Encoding.Latin1.GetString(editedBytes, 24, editedBytes.Length - 24)
        .Replace("arenaWonFlags00 0", "arenaWonFlags00 1", StringComparison.Ordinal);
    Check("fixture edit kept the length", editedText.Length == SaveFixture.Text.Length);
    Encoding.Latin1.GetBytes(editedText).CopyTo(editedBytes, 24);
    var editedFile = Path.Combine(exportDir, "edited.psc");
    File.WriteAllBytes(editedFile, editedBytes);

    var importResult = SaveStore.Import(rawPath, editedFile, slot: 1, backup: false);
    Check("import reports the target slot", importResult.Slot == 1 && importResult.Length == SaveFixture.Text.Length);
    using (var image = DiskImageFactory.Open(rawPath))
    {
        var saves = PscFormat.FindSaves(image);
        Check("import wrote the edited value",
            saves[1].Fields.Single(f => f.Key == "arenaWonFlags00").Value == 1);
        Check("import left the other slot alone",
            saves[0].Fields.Single(f => f.Key == "arenaWonFlags00").Value == 0);
    }

    var shortFile = Path.Combine(exportDir, "short.psc");
    File.WriteAllBytes(shortFile, editedBytes[..^16]);
    try
    {
        SaveStore.Import(rawPath, shortFile, slot: 0, backup: false);
        Check("import rejects a malformed profile", false);
    }
    catch (ToolException)
    {
        Check("import rejects a malformed profile", true);
    }

    // ---------------------------------------------------------- splitter bridge
    Check("ten stages can split", Arenas.Count == 10, Arenas.Count.ToString());
    Check("stage names follow the arena table",
        Arenas.SmackStages.Select(stage => stage.Name).SequenceEqual(Arenas.All.Take(Arenas.WonArenaSlots)));
    Check("the two unrecordable arenas",
        Arenas.All.Skip(Arenas.WonArenaSlots).SequenceEqual(new[] { "Lunarcy", "Shooting Stars" }));
    Check("watched fields are the split fields",
        Arenas.WonFields().SetEquals(Arenas.SmackStages.Select(stage => stage.Field)));
    Check("race and flag wins are not smack wins",
        Arenas.IsWonFlag("arenaWonFlags00") && !Arenas.IsWonFlag("arenaWonFlags01")
        && !Arenas.IsWonFlag("arenaWonFlags02"));
    Check("arena name lookup", Arenas.ArenaName("arenaWonFlags30") == "Swamp Stomp",
        Arenas.ArenaName("arenaWonFlags30"));

    var lss = LssFile.Xml();
    var lssDocument = System.Xml.Linq.XDocument.Parse(lss);
    var segmentNames = lssDocument.Root!.Element("Segments")!.Elements("Segment")
        .Select(segment => segment.Element("Name")!.Value).ToList();
    Check("lss has one split per stage", segmentNames.SequenceEqual(Arenas.SmackArenas),
        string.Join("|", segmentNames));
    Check("lss is the All Smacks category",
        lssDocument.Root!.Element("CategoryName")!.Value == "All Smacks");
    var committedLss = FindUp("All Smacks.lss");
    if (committedLss is null)
    {
        Console.WriteLine("SKIP  All Smacks.lss not reachable from the test binary");
    }
    else
    {
        Check("the committed lss matches the code", File.ReadAllText(committedLss) == lss);
    }

    using (var profiles = new Profiles(rawPath))
    {
        var states = profiles.Read();
        Check("profiles reads both slots", states is not null && states.Count == 2,
            states is null ? "unreadable" : states.Count.ToString());
        Check("profiles only reports split fields",
            states!.Values.All(won => won.All(key => Arenas.WonFields().Contains(key))));
        Check("profiles saw the imported win",
            states.Values.Any(won => won.Contains("arenaWonFlags00")));
    }

    var emptyStates = new Dictionary<long, HashSet<string>> { [1] = new() };
    var wonStates = new Dictionary<long, HashSet<string>> { [1] = new() { "arenaWonFlags00" } };
    Check("the first read is a baseline", Splitter.Diff(null, emptyStates).Baseline);
    Check("an unchanged profile does not split",
        Splitter.Diff(emptyStates, emptyStates) is { Baseline: false, Fresh.Count: 0 });
    Check("a new smack win is one split",
        Splitter.Diff(emptyStates, wonStates).Fresh.SequenceEqual(new[] { "arenaWonFlags00" }));
    Check("a cleared win is reported",
        Splitter.Diff(wonStates, emptyStates).Cleared.SequenceEqual(new[] { "arenaWonFlags00" }));
    Check("HOST:PORT parsing", Splitter.ParseHostPort("127.0.0.1:16834") == ("127.0.0.1", 16834));

    var printed = new StringWriter();
    var console = Console.Out;
    Console.SetOut(printed);
    using (var printer = new PrinterTimer())
    {
        printer.Send("splitOrStart");
    }
    Console.SetOut(console);
    Check("dry run prints the command",
        printed.ToString().Contains("-> splitOrStart", StringComparison.Ordinal), printed.ToString().Trim());

    var tcpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    tcpListener.Start();
    var tcpPort = ((System.Net.IPEndPoint)tcpListener.LocalEndpoint).Port;
    string? tcpLine = null;
    var accepted = Task.Run(() =>
    {
        using var connection = tcpListener.AcceptTcpClient();
        using var reader = new StreamReader(connection.GetStream(), Encoding.ASCII);
        tcpLine = reader.ReadLine();
    });
    using (var timer = new TcpTimer("127.0.0.1", tcpPort))
    {
        timer.Send("splitOrStart");
    }
    accepted.Wait(TimeSpan.FromSeconds(5));
    tcpListener.Stop();
    Check("TCP timer sends LiveSplit's command", tcpLine == "startorsplit", tcpLine ?? "nothing received");

    using (var server = new WebSocketTimer("127.0.0.1", 0))
    using (var client = new System.Net.Sockets.TcpClient())
    {
        client.Connect("127.0.0.1", server.Port);
        var stream = client.GetStream();
        stream.Write(Encoding.ASCII.GetBytes(
            "GET / HTTP/1.1\r\nHost: 127.0.0.1\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n"));
        var handshake = ReadHeaders(stream);
        Check("websocket handshake accepted", handshake.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
            handshake.Split('\r').FirstOrDefault() ?? "no response");
        Check("websocket accept key", handshake.Contains("s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", StringComparison.Ordinal));
        Check("websocket client registered", server.WaitForClient(TimeSpan.FromSeconds(2)));
        server.Send("splitOrStart");
        var frame = new byte[2];
        ReadExact(stream, frame);
        var payload = new byte[frame[1] & 0x7F];
        if (payload.Length > 0)
        {
            ReadExact(stream, payload);
        }
        Check("websocket sends the JSON command",
            Encoding.UTF8.GetString(payload) == "{\"command\":\"splitOrStart\"}",
            Encoding.UTF8.GetString(payload));
    }

    // -------------------------------------------------------------- game config
    var saveDir = Path.Combine(root, "appdata");
    var game = new GameFiles(saveDir);
    var isoPath = Path.Combine(root, "game.iso");
    var iso = new byte[0x8000];
    Encoding.ASCII.GetBytes(
            "// NEW GAME CONFIG\r\n" +
            "weaponLockFlags -8388611\r\n" +
            "vehicleLockFlags -64\r\n" +
            "arenaRaceLockFlags -1\r\n" +
            "arenaFlagLockFlags -1\r\n" +
            "arenaSmackLockFlags 235802126\r\n" +
            "renderLockFlags 15\r\n")
        .CopyTo(iso, 0x100);
    File.WriteAllBytes(isoPath, iso);

    var config = GameFiles.FindConfig(isoPath);
    Check("finds the startup config", config is not null && config.Value.Offset == 0x100,
        config is null ? "not found" : $"0x{config.Value.Offset:x}");
    var values = GameFiles.ParseConfig(config!.Value.Block);
    Check("reads every lock flag", values.Count == 5, values.Count.ToString());
    Check("reads signed masks", values["weaponLockFlags"] == -8388611);

    var patchedBlock = GameFiles.PatchBlock(config.Value.Block, GameFiles.Keys.ToDictionary(k => k, _ => 0L));
    Check("patched block keeps its size", patchedBlock.Length == config.Value.Block.Length,
        $"{patchedBlock.Length} vs {config.Value.Block.Length}");
    Check("patched block is fully unlocked",
        GameFiles.ParseConfig(patchedBlock).Values.All(v => v == 0));
    Check("render/movie lines untouched",
        Encoding.ASCII.GetString(patchedBlock).Contains("renderLockFlags 15", StringComparison.Ordinal));

    Check("game patch reports a change", game.PatchGame(isoPath));
    Check("ISO backup written", File.Exists(isoPath + ".orig"));
    Check("manifest written", File.Exists(Path.Combine(saveDir, "game_patch.json")));
    Check("game state is patched", game.State(isoPath).Patched);
    Check("second patch is a no-op", game.PatchGame(isoPath) == false);
    Check("revert restores the original bytes", game.RevertGame(isoPath)
        && File.ReadAllBytes(isoPath).AsSpan().SequenceEqual(iso));

    // DefaultLockMasks uses the baselines, then the ISO, for the locked values
    var masksFromIso = game.DefaultLockMasks(isoPath, Array.Empty<Baseline>());
    Check("default masks come from the ISO",
        masksFromIso["weaponLockFlags"] == -8388611 && masksFromIso["vehicleLockFlags"] == -64,
        string.Join(",", masksFromIso.Select(kv => $"{kv.Key}={kv.Value}")));
}
finally
{
    try
    {
        Directory.Delete(root, recursive: true);
    }
    catch (IOException)
    {
        // a locked temp file must not fail the run
    }
}

Console.WriteLine(failures == 0 ? "RESULT ALL PASS" : $"RESULT {failures} FAILURE(S)");
return failures == 0 ? 0 : 1;

internal static class SaveFixture
{
    private static readonly string[] Lines = new[]
        {
            "weaponLockFlags -8388611",
            "vehicleLockFlags -64",
            "arenaRaceLockFlags -1",
            "arenaFlagLockFlags -1",
            "arenaSmackLockFlags 235802126",
            "renderLockFlags 15",
            "movieFlags 0",
        }
        .Concat(Enumerable.Range(0, 10).SelectMany(arena =>
            Enumerable.Range(0, 3).Select(e => $"arenaWonFlags{arena}{e} 0")))
        .ToArray();

    /// <summary>A profile with the same layout the game writes.</summary>
    public static string Text { get; } = string.Join("\r\n", Lines) + "\r\n";

    public static byte[] Bytes()
    {
        var header = new byte[PscFormat.HeaderLength];
        for (var i = 0; i < 16; i++)
        {
            header[i] = (byte)(0x40 + i);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16, 4), 0x11223344);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20, 4), (uint)Encoding.Latin1.GetByteCount(Text));
        return PscFormat.Serialize(header, Text);
    }
}

internal static class QCow2Fixture
{
    /// <summary>
    /// Writes the smallest useful qcow2: 64 KiB clusters, one L1 entry, one data
    /// cluster for guest offset 0 and one unallocated cluster after it.
    /// </summary>
    public static void Write(string path, byte[] guestData, long guestOffset)
    {
        const int clusterSize = 1 << 16;
        const long l1Offset = 0x10000;
        const long l2Offset = 0x20000;
        const long dataOffset = 0x30000;

        var image = new byte[0x40000];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0, 4), 0x514649FB);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(4, 4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(20, 4), 16);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(24, 8), 0x20000);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(36, 4), 1);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan(40, 8), (ulong)l1Offset);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan((int)l1Offset, 8), (ulong)l2Offset);
        BinaryPrimitives.WriteUInt64BigEndian(image.AsSpan((int)l2Offset, 8), (ulong)dataOffset);
        guestData.CopyTo(image, (int)(dataOffset + (guestOffset & (clusterSize - 1))));
        File.WriteAllBytes(path, image);
    }
}
