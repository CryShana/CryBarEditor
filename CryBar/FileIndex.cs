namespace CryBar;

/// <summary>
/// Index of files across root directories and BAR archives.
/// Uses a single stem-based index (filename without extensions) for all lookups.
/// Supports flexible matching: exact filename, extension guessing, partial path suffix matching.
/// </summary>
public class FileIndex
{
    static readonly string[] KnownExtensions = [".xmb", ".ddt", ".tmm"];

    readonly Dictionary<string, List<FileIndexEntry>> _byStem = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _lock = new();
    int _count;

    static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>
    /// Strips all extensions from a filename: "proto.xml.XMB" → "proto", "hoplite_iron.tmm.data" → "hoplite_iron"
    /// </summary>
    static string GetStem(string fileName)
    {
        var name = fileName;
        int dot;
        while ((dot = name.LastIndexOf('.')) > 0)
            name = name[..dot];
        return name;
    }

    /// <summary>
    /// Extracts the filename portion (last segment) from a normalized path.
    /// </summary>
    static string GetFileName(string normalizedPath)
    {
        var lastSlash = normalizedPath.LastIndexOf('/');
        return lastSlash >= 0 ? normalizedPath[(lastSlash + 1)..] : normalizedPath;
    }

    public int Count => _count;

    public void Add(FileIndexEntry entry)
    {
        var normName = Normalize(entry.FileName);
        var stem = GetStem(normName);
        lock (_lock)
        {
            if (!_byStem.TryGetValue(stem, out var stemList))
                _byStem[stem] = stemList = [];
            stemList.Add(entry);
            _count++;
        }
    }

    public void Remove(string fullRelativePath)
    {
        var normPath = Normalize(fullRelativePath);
        lock (_lock)
        {
            // Find the stem for this path to locate its bucket
            var fileName = GetFileName(normPath);
            var stem = GetStem(fileName);

            if (!_byStem.TryGetValue(stem, out var stemList)) return;

            int removed = stemList.RemoveAll(e => Normalize(e.FullRelativePath).Equals(normPath, StringComparison.OrdinalIgnoreCase));
            _count -= removed;

            if (stemList.Count == 0) _byStem.Remove(stem);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _byStem.Clear();
            _count = 0;
        }
    }

    /// <summary>
    /// Flexible file lookup. Single pass over stem candidates with priority:
    /// 1. Exact full path match
    /// 2. Exact filename match (with path suffix filter when query has directories)
    /// 3. Extension-flexible match (strip/append extensions)
    /// 4. All stem matches as fallback (extensionless queries)
    /// </summary>
    public List<FileIndexEntry> Find(string queryPath)
    {
        var norm = Normalize(queryPath);
        var fileName = GetFileName(norm);
        var stem = GetStem(fileName);
        if (stem.Length == 0) return [];

        List<FileIndexEntry>? candidates;
        lock (_lock)
        {
            if (!_byStem.TryGetValue(stem, out candidates))
                return [];
            candidates = [.. candidates]; // snapshot
        }

        bool hasPath = norm.Contains('/');

        // Precompute: if query ends with a known extension, compute the stripped version
        string? queryWithoutKnownExt = null;
        foreach (var ext in KnownExtensions)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                queryWithoutKnownExt = fileName[..^ext.Length];
                break;
            }
        }

        // Single pass: classify each candidate (normalize once per candidate)
        List<FileIndexEntry>? exactPath = null;
        List<FileIndexEntry>? exactName = null;
        List<FileIndexEntry>? extMatch = null;

        foreach (var entry in candidates)
        {
            var entryPath = Normalize(entry.FullRelativePath);
            var entryName = Normalize(entry.FileName);

            if (entryPath.Equals(norm, StringComparison.OrdinalIgnoreCase))
            {
                (exactPath ??= []).Add(entry);
                continue;
            }

            if (entryName.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            {
                (exactName ??= []).Add(entry);
                continue;
            }

            // Extension-flexible: entry filename matches query + known extension
            foreach (var ext in KnownExtensions)
            {
                if (entryName.Equals(fileName + ext, StringComparison.OrdinalIgnoreCase))
                {
                    (extMatch ??= []).Add(entry);
                    break;
                }
            }

            // Extension-flexible: query has known extension, entry matches without it
            if (queryWithoutKnownExt != null && entryName.Equals(queryWithoutKnownExt, StringComparison.OrdinalIgnoreCase))
            {
                (extMatch ??= []).Add(entry);
            }
        }

        if (exactPath is { Count: > 0 }) return exactPath;

        // Pick best name-level match
        var nameResult = exactName ?? extMatch;
        if (nameResult == null)
        {
            // Stem matched but no filename-level match - only return candidates whose filename
            // starts with the query filename (handles extensionless queries like "proto" → "proto.xml.XMB")
            var prefixMatches = new List<FileIndexEntry>();
            foreach (var entry in candidates)
            {
                var entryName = Normalize(entry.FileName);
                if (entryName.StartsWith(fileName + ".", StringComparison.OrdinalIgnoreCase))
                    prefixMatches.Add(entry);
            }
            nameResult = prefixMatches.Count > 0 ? prefixMatches : [];
        }

        // Path suffix filter when query has directory components
        if (hasPath && nameResult.Count > 1)
        {
            var suffixMatches = new List<FileIndexEntry>();
            foreach (var entry in nameResult)
            {
                var entryPath = Normalize(entry.FullRelativePath);
                if (entryPath.EndsWith(norm, StringComparison.OrdinalIgnoreCase))
                    suffixMatches.Add(entry);
            }
            if (suffixMatches.Count > 0) return suffixMatches;
        }

        return nameResult;
    }

    /// <summary>
    /// Resolves a partial path (e.g. "greek\units\infantry\hoplite\hoplite_iron") to all matching index entries.
    /// Uses stem-based lookup then verifies all folder segments from the parsed path appear in the candidate's full path.
    /// </summary>
    public List<FileIndexEntry> FindByPartialPath(string parsedPath, string? excludePath = null)
    {
        var norm = Normalize(parsedPath);
        var normExclude = excludePath != null ? Normalize(excludePath) : null;

        var lastSlash = norm.LastIndexOf('/');
        var fileName = lastSlash >= 0 ? norm[(lastSlash + 1)..] : norm;
        var stem = GetStem(fileName);
        if (stem.Length == 0) return [];

        var dirPart = lastSlash >= 0 ? norm[..lastSlash] : null;
        var dirSegments = dirPart?.Split('/', StringSplitOptions.RemoveEmptyEntries);

        List<FileIndexEntry>? candidates;
        lock (_lock)
        {
            if (!_byStem.TryGetValue(stem, out candidates))
                return [];
            candidates = [.. candidates]; // snapshot
        }

        var results = new List<FileIndexEntry>();
        foreach (var entry in candidates)
        {
            var entryNorm = Normalize(entry.FullRelativePath);

            if (normExclude != null && entryNorm.Equals(normExclude, StringComparison.OrdinalIgnoreCase))
                continue;

            if (dirSegments != null && dirSegments.Length > 0)
            {
                var entryLastSlash = entryNorm.LastIndexOf('/');
                var entryDir = entryLastSlash >= 0 ? entryNorm[..entryLastSlash] : "";
                var entryDirSegments = entryDir.Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (!EndsWithSegments(entryDirSegments, dirSegments))
                    continue;
            }

            results.Add(entry);
        }

        return results;
    }

    static bool EndsWithSegments(string[] haystack, string[] needle)
    {
        if (needle.Length > haystack.Length) return false;
        int offset = haystack.Length - needle.Length;
        for (int i = 0; i < needle.Length; i++)
        {
            if (!haystack[offset + i].Equals(needle[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}
