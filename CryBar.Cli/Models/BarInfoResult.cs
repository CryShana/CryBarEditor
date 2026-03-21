namespace CryBar.Cli.Models;

public class BarInfoResult
{
    public string Archive { get; set; } = "";
    public int Version { get; set; }
    public int EntryCount { get; set; }
    public long TotalSize { get; set; }
    public long CompressedSize { get; set; }
    public Dictionary<string, int> CompressionBreakdown { get; set; } = new();
    public Dictionary<string, int> ExtensionBreakdown { get; set; } = new();
}
