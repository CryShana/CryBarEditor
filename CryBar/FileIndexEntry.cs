namespace CryBar;

public enum FileIndexSource { RootFile, BarEntry }

public class FileIndexEntry
{
    public required string FullRelativePath { get; init; }  // e.g. "game\data\gameplay\proto.xml.XMB"
    public required string FileName { get; init; }          // e.g. "proto.xml.XMB"
    public required FileIndexSource Source { get; init; }
    public string? BarFilePath { get; init; }               // disk path to .bar (null for root files)
    public string? EntryRelativePath { get; init; }         // path within BAR archive
}
