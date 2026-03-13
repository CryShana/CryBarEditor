namespace CryBar;

/// <summary>
/// Index of files across root directories and BAR archives.
/// Supports flexible lookups by full path, filename, and with extension guessing.
/// </summary>
public class FileIndex
{
    static readonly string[] KnownExtensions = [".xmb", ".ddt", ".tmm"];

    readonly Dictionary<string, List<FileIndexEntry>> _byPath = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<FileIndexEntry>> _byFileName = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _lock = new();
    int _count;

    static string Normalize(string path) => path.Replace('\\', '/');

    public int Count => _count;

    public void Add(FileIndexEntry entry)
    {
        var normPath = Normalize(entry.FullRelativePath);
        var normName = Normalize(entry.FileName);
        lock (_lock)
        {
            if (!_byPath.TryGetValue(normPath, out var pathList))
                _byPath[normPath] = pathList = [];
            pathList.Add(entry);
            _count++;

            if (!_byFileName.TryGetValue(normName, out var nameList))
                _byFileName[normName] = nameList = [];
            nameList.Add(entry);
        }
    }

    public void Remove(string fullRelativePath)
    {
        var normPath = Normalize(fullRelativePath);
        lock (_lock)
        {
            if (!_byPath.TryGetValue(normPath, out var pathList)) return;
            foreach (var entry in pathList)
            {
                var normName = Normalize(entry.FileName);
                if (_byFileName.TryGetValue(normName, out var nameList))
                {
                    nameList.RemoveAll(e => Normalize(e.FullRelativePath).Equals(normPath, StringComparison.OrdinalIgnoreCase));
                    if (nameList.Count == 0) _byFileName.Remove(normName);
                }
            }
            _count -= pathList.Count;
            _byPath.Remove(normPath);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _byPath.Clear();
            _byFileName.Clear();
            _count = 0;
        }
    }

    /// <summary>
    /// Flexible file lookup. Tries:
    /// 1. Exact path match
    /// 2. Strip last extension, retry (handles proto.xml -> proto.xml.xmb)
    /// 3. Append known extensions (.xmb, .ddt, .tmm), retry
    /// 4. Filename index lookup, filter by path suffix match
    /// 5. Filename + known extensions
    /// </summary>
    public List<FileIndexEntry> Find(string queryPath)
    {
        var norm = Normalize(queryPath);

        // 1. Exact path match
        if (_byPath.TryGetValue(norm, out var exact))
            return [.. exact];

        // 2. Strip last extension, retry
        var lastDot = norm.LastIndexOf('.');
        if (lastDot > 0)
        {
            var stripped = norm[..lastDot];
            if (_byPath.TryGetValue(stripped, out var strippedMatch))
                return [.. strippedMatch];
        }

        // 3. Append known extensions
        foreach (var ext in KnownExtensions)
        {
            if (_byPath.TryGetValue(norm + ext, out var extMatch))
                return [.. extMatch];
        }

        // 4. Filename lookup with path suffix match
        var fileName = norm.Contains('/') ? norm[(norm.LastIndexOf('/') + 1)..] : norm;
        if (_byFileName.TryGetValue(fileName, out var nameMatches))
        {
            // Try suffix match if query has path components
            if (norm.Contains('/'))
            {
                var suffixMatches = nameMatches
                    .Where(e => Normalize(e.FullRelativePath).EndsWith(norm, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (suffixMatches.Count > 0) return suffixMatches;
            }
            return [.. nameMatches];
        }

        // 5. Filename + known extensions
        foreach (var ext in KnownExtensions)
        {
            if (_byFileName.TryGetValue(fileName + ext, out var extNameMatch))
                return [.. extNameMatch];
        }

        return new List<FileIndexEntry>();
    }
}
