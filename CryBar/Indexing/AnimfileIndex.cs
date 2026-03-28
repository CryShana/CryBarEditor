namespace CryBar.Indexing;

/// <summary>
/// Reverse index mapping TMM model stems to their animfile XML entries.
/// Built by parsing animfile XMLs and extracting the TMModel path from component elements.
/// </summary>
public class AnimfileIndex
{
    readonly Dictionary<string, FileIndexEntry> _byTmmStem = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _byTmmStem.Count;

    /// <summary>
    /// Registers an animfile entry for the TMM model path found in its component element.
    /// </summary>
    public void Add(string tmmModelPath, FileIndexEntry animfileEntry)
    {
        // extract stem from path like "greek\units\infantry\hoplite\hoplite_iron"
        var stem = Path.GetFileNameWithoutExtension(tmmModelPath.Replace('/', '\\'));
        if (stem.Length > 0)
            _byTmmStem[stem] = animfileEntry;
    }

    /// <summary>
    /// Finds the animfile entry for a TMM stem. Tries exact match first,
    /// then progressively strips trailing _segments to find a base model match.
    /// e.g. "hoplite_iron" -> "hoplite", "armory_a_age2" -> "armory_a" -> "armory"
    /// </summary>
    public FileIndexEntry? Find(string tmmStem)
    {
        var candidate = tmmStem;
        while (candidate.Length > 0)
        {
            if (_byTmmStem.TryGetValue(candidate, out var entry))
                return entry;

            // strip last _segment
            int lastUnderscore = candidate.LastIndexOf('_');
            if (lastUnderscore <= 0) break;
            candidate = candidate[..lastUnderscore];
        }

        return null;
    }

    public void Clear() => _byTmmStem.Clear();
}
