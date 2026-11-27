using System.Text;

namespace CDSaveEditor.LiveSplit;

/// <summary>The splits file: one segment per stage that can record a Smack Attack win.</summary>
public static class LssFile
{
    public static string Xml()
    {
        var segments = new StringBuilder();
        foreach (var stage in Arenas.SmackStages)
        {
            segments.Append("    <Segment>\n")
                .Append("      <Name>").Append(Escape(stage.Name)).Append("</Name>\n")
                .Append("      <Icon />\n")
                .Append("      <SplitTimes>\n")
                .Append("        <SplitTime name=\"Personal Best\" />\n")
                .Append("      </SplitTimes>\n")
                .Append("      <BestSegmentTime />\n")
                .Append("      <SegmentHistory />\n")
                .Append("    </Segment>\n");
        }
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n"
               + "<Run version=\"1.8.0\">\n"
               + "  <GameIcon />\n"
               + "  <GameName>Cel Damage</GameName>\n"
               + "  <CategoryName>All Smacks</CategoryName>\n"
               + "  <Metadata>\n"
               + "    <Run id=\"\" />\n"
               + "    <Platform usesEmulator=\"True\">Xbox</Platform>\n"
               + "    <Region />\n"
               + "    <Variables />\n"
               + "  </Metadata>\n"
               + "  <Offset>00:00:00</Offset>\n"
               + "  <AttemptCount>0</AttemptCount>\n"
               + "  <AttemptHistory />\n"
               + "  <Segments>\n"
               + segments
               + "  </Segments>\n"
               + "</Run>\n";
    }

    public static void Write(string path) =>
        File.WriteAllText(path, Xml(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
