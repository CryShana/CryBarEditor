using System.IO;

namespace CryBar.Indexing;

public enum FileIndexSource : byte { RootFile, BarEntry }

public readonly struct FileIndexEntry
{
    public required string FullRelativePath { get; init; }
    public required FileIndexSource Source { get; init; }
    public string? BarFilePath { get; init; }
    public string? EntryRelativePath { get; init; }
    public bool IsExternal { get; init; }

    /// <summary>
    /// Derives filename from FullRelativePath. Zero-allocation slice.
    /// </summary>
    public ReadOnlySpan<char> FileName => Path.GetFileName(FullRelativePath.AsSpan());
}
