using CryBar.Indexing;

namespace CryBar.Sound;

/// <summary>
/// Entry in the soundset index: links a soundset name to its source file and FMOD bank.
/// </summary>
public class SoundsetIndexEntry
{
    /// <summary>Culture extracted from soundset filename (e.g. "greek" from "soundsets_greek.soundset.XMB").</summary>
    public required string Culture { get; init; }

    /// <summary>The soundset definition file this name was found in.</summary>
    public required FileIndexEntry SoundsetFile { get; init; }

    /// <summary>Associated FMOD bank file (e.g. greek.bank). Null if not found.</summary>
    public FileIndexEntry? BankFile { get; init; }
}

/// <summary>
/// Index mapping soundset names to their source files and FMOD banks.
/// Built by parsing soundset definition files (soundsets_*.soundset.XMB).
/// Used by DependencyFinder for soundset resolution and by the FMOD linker.
/// </summary>
public class SoundsetIndex
{
    readonly Dictionary<string, SoundsetIndexEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byName.Count;

    /// <summary>
    /// Registers all soundset names from a parsed soundset file.
    /// Call this for each soundsets_*.soundset file after parsing with SoundsetParser.
    /// </summary>
    public void AddFromParsedFile(
        List<SoundsetDefinition> definitions,
        string culture,
        FileIndexEntry soundsetFile,
        FileIndexEntry? bankFile)
    {
        foreach (var def in definitions)
        {
            _byName[def.Name] = new SoundsetIndexEntry
            {
                Culture = culture,
                SoundsetFile = soundsetFile,
                BankFile = bankFile,
            };
        }
    }

    /// <summary>
    /// Looks up a soundset name and returns its index entry, or null if not found.
    /// </summary>
    public SoundsetIndexEntry? Find(string soundsetName)
    {
        return _byName.GetValueOrDefault(soundsetName);
    }

    public void Clear() => _byName.Clear();

    /// <summary>
    /// Extracts culture from a soundset filename: "soundsets_greek.soundset.XMB" -> "greek".
    /// Returns null if the filename doesn't match the expected pattern.
    /// </summary>
    public static string? ExtractCulture(string fileName)
    {
        const string prefix = "soundsets_";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = fileName.AsSpan(prefix.Length);
        var dot = rest.IndexOf('.');
        return dot > 0 ? rest[..dot].ToString() : null;
    }
}
