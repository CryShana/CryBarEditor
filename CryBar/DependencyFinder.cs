using CryBar.Classes;

using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace CryBar;

/// <summary>
/// Scans file content for dependency references (file paths, STR_ keys, soundset names)
/// and optionally resolves them against a FileIndex.
/// </summary>
public static partial class DependencyFinder
{
    /// <summary>
    /// Matches file-path-like strings: 2+ segments separated by \ or /, last segment may have extension.
    /// </summary>
    [GeneratedRegex(@"[\w][\w .+-]*(?:[\\\/][\w][\w .+-]*)+(?:\.[\w]+)*")]
    private static partial Regex PathPattern();

    /// <summary>
    /// Matches STR_ string table keys (e.g. STR_UNIT_HOPLITE_NAME).
    /// </summary>
    [GeneratedRegex(@"\bSTR_[A-Z0-9_]{3,}\b")]
    private static partial Regex StrKeyPattern();

    /// <summary>
    /// Matches single-segment filenames with an alphabetic extension (e.g. "handattack.tactics").
    /// Lookbehind/lookahead exclude matches that are part of a larger path already caught by PathPattern.
    /// </summary>
    [GeneratedRegex(@"(?<![\\\/\w<])[A-Za-z][\w]*\.[A-Za-z][\w]+(?![\\\/\w>])")]
    private static partial Regex SingleSegmentFilePattern();

    /// <summary>
    /// Soundset name from attribute: &lt;soundset name="GreekMilitarySelect"&gt;
    /// </summary>
    [GeneratedRegex(@"<soundset[^>]*\bname=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex SoundsetNameAttrPattern();

    /// <summary>
    /// Soundset name from element content: &lt;soundset ...&gt;LightningStrike&lt;/soundset&gt;
    /// Content must start with a letter (to avoid matching paths).
    /// </summary>
    [GeneratedRegex(@"<soundset[^>]*>([A-Za-z][\w]*)</soundset>", RegexOptions.IgnoreCase)]
    private static partial Regex SoundsetContentPattern();

    static readonly XmlReaderSettings SafeXmlSettings = new()
    {
        DtdProcessing = DtdProcessing.Ignore,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    /// <summary>
    /// Scans <paramref name="content"/> for all dependency references (paths, STR_ keys, soundset names).
    /// Groups results by entity when the content has XML entity structure (repeated direct children with name attributes).
    /// Optionally resolves parsed paths against <paramref name="index"/>.
    /// </summary>
    /// <param name="filterEntityName">When set, only return the group matching this entity name (case-insensitive).</param>
    public static DependencyResult FindDependencies(string content, string entryPath, FileIndex? index = null, SoundsetIndex? soundsetIndex = null, string? stringTableLanguage = null, string? filterEntityName = null)
    {
        // Preprocess: unescape JSON double-backslashes
        var processed = content;
        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            processed = processed.Replace("\\\\", "\\");

        // Build groups (with entity detection for XML content)
        var groups = BuildGroups(processed);

        // Filter to a single entity if requested
        if (filterEntityName != null)
        {
            groups = groups
                .Where(g => string.Equals(g.EntityName, filterEntityName, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // Resolve against index
        if (index != null)
        {
            var lang = string.IsNullOrWhiteSpace(stringTableLanguage) ? "English" : stringTableLanguage;
            foreach (var group in groups)
                foreach (var r in group.References)
                    ResolveReference(r, entryPath, index, soundsetIndex, lang);
        }

        return new DependencyResult
        {
            EntryPath = entryPath,
            Groups = groups,
        };
    }

    // Group building

    /// <summary>
    /// Builds dependency groups. For XML with entity structure, uses a single XmlReader pass
    /// to collect all direct children, determine entity tags, and extract references in parallel.
    /// Non-XML files go into an ungrouped group.
    /// </summary>
    static List<DependencyGroup> UngroupedFallback(string content)
    {
        var refs = ExtractReferences(content);
        return refs.Count > 0
            ? [new DependencyGroup { References = refs }]
            : [];
    }

    static List<DependencyGroup> BuildGroups(string content)
    {
        // Strip BOM if present
        if (content.Length > 0 && content[0] == '\uFEFF')
            content = content[1..];

        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '<')
            return UngroupedFallback(content);

        // Single XmlReader pass: collect all direct children with their tag, identity, and OuterXml
        var children = new List<(string tag, string? name, string xml)>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), SafeXmlSettings);

            // Advance to root element
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element) break;
            if (reader.NodeType != XmlNodeType.Element)
                return UngroupedFallback(content);

            // Track tags where ExtractChildIdentityElement returned null — skip after 2 nulls since last success
            var noIdentityCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int rootDepth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1)
                    continue;

                var tag = reader.Name;
                var name = reader.GetAttribute("name")
                        ?? reader.GetAttribute("filename")
                        ?? reader.GetAttribute("id");

                if (reader.IsEmptyElement)
                {
                    children.Add((tag, name, ""));
                }
                else
                {
                    var xml = reader.ReadOuterXml();

                    // If no identity attribute, check for a direct <name> or <ID> child element
                    if (name == null)
                    {
                        noIdentityCount.TryGetValue(tag, out var nullCount);
                        if (nullCount < 2)
                        {
                            name = ExtractChildIdentityElement(xml);
                            if (name == null)
                                noIdentityCount[tag] = nullCount + 1;
                            else
                                noIdentityCount.Remove(tag); // reset — keep checking this tag
                        }
                    }

                    children.Add((tag, name, xml));
                }
            }
        }
        catch (XmlException)
        {
            // XML parse error mid-way: process what we collected
        }

        if (children.Count == 0)
            return UngroupedFallback(content);

        // Entity detection — three rules, evaluated in order:
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hasName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, name, _) in children)
        {
            counts.TryGetValue(tag, out var c);
            counts[tag] = c + 1;
            if (name != null)
                hasName.Add(tag);
        }

        var entityTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Rule 1: Repeated same-tag children (≥2) with at least one named — the strictest match.
        // Preferred over Rule 2 so that singleton infrastructure tags (e.g. <settings>) don't
        // become entities when real repeated entities (e.g. <unit>) exist alongside them.
        foreach (var (tag, count) in counts)
        {
            if (count >= 2 && hasName.Contains(tag))
                entityTags.Add(tag);
        }

        // Rule 2: Any named children, even singletons (e.g. single <Chatset name="Shared">)
        if (entityTags.Count == 0)
        {
            foreach (var (tag, _) in counts)
            {
                if (hasName.Contains(tag))
                    entityTags.Add(tag);
            }
        }

        // Rule 3: All children have unique tags with content — tag name IS the entity name.
        // Mutation below sets name = tag, which the partitioning loop relies on.
        if (entityTags.Count == 0 && children.Count >= 2
            && counts.Values.All(c => c == 1)
            && children.All(c => c.xml.Length > 0))
        {
            for (int i = 0; i < children.Count; i++)
            {
                var (tag, name, xml) = children[i];
                children[i] = (tag, name ?? tag, xml);
            }
            foreach (var (tag, _) in counts)
                entityTags.Add(tag);
        }

        if (entityTags.Count == 0)
            return UngroupedFallback(content);

        // Partition children into entities vs ungrouped
        var entities = new List<(string name, string tag, string xml)>();
        var ungroupedXmlChunks = new List<string>();

        foreach (var (tag, name, xml) in children)
        {
            if (entityTags.Contains(tag) && name != null && xml.Length > 0)
                entities.Add((name, tag, xml));
            else if (xml.Length > 0)
                ungroupedXmlChunks.Add(xml);
        }

        // Parallel regex extraction across entities
        var entityResults = new (string name, string tag, List<DependencyReference> refs)[entities.Count];
        Parallel.For(0, entities.Count, i =>
        {
            var (name, tag, xml) = entities[i];
            entityResults[i] = (name, tag, ExtractReferences(xml));
        });

        // Assemble groups and deduplicate ungrouped refs
        var groups = new List<DependencyGroup>();
        var entitySeenKeys = new HashSet<(DependencyRefType, string)>(RefKeyComparer.Instance);

        foreach (var (name, tag, entityRefs) in entityResults)
        {
            if (entityRefs.Count > 0)
            {
                groups.Add(new DependencyGroup
                {
                    EntityName = name,
                    EntityType = tag.ToLowerInvariant(),
                    References = entityRefs,
                });

                foreach (var r in entityRefs)
                    entitySeenKeys.Add((r.Type, r.RawValue));
            }
        }

        if (ungroupedXmlChunks.Count > 0)
        {
            var ungroupedRefs = new List<DependencyReference>();
            foreach (var chunk in ungroupedXmlChunks)
                ungroupedRefs.AddRange(ExtractReferences(chunk));

            var deduped = new List<DependencyReference>();
            foreach (var r in ungroupedRefs)
            {
                if (!entitySeenKeys.Contains((r.Type, r.RawValue)))
                    deduped.Add(r);
            }

            if (deduped.Count > 0)
            {
                groups.Insert(0, new DependencyGroup
                {
                    EntityName = null,
                    EntityType = null,
                    References = deduped,
                });
            }
        }

        return groups;
    }

    /// <summary>
    /// Extracts text content of a direct &lt;name&gt; or &lt;ID&gt; child element from an XML fragment.
    /// Checks &lt;name&gt; first, then falls back to &lt;ID&gt;.
    /// Returns null if neither element exists.
    /// </summary>
    static string? ExtractChildIdentityElement(string outerXml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(outerXml), SafeXmlSettings);
            string? idValue = null;

            // Advance to the wrapper element
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element) break;
            if (reader.NodeType != XmlNodeType.Element) return null;

            int parentDepth = reader.Depth;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != parentDepth + 1)
                    continue;

                var tagName = reader.Name;

                if (tagName.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    var value = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value; // <name> wins immediately
                }
                else if (idValue == null && tagName.Equals("ID", StringComparison.OrdinalIgnoreCase))
                {
                    var value = reader.ReadElementContentAsString();
                    if (!string.IsNullOrWhiteSpace(value))
                        idValue = value; // remember <ID> as fallback
                }
            }

            return idValue;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    // --- Reference extraction (regex-based, works on any content chunk) ---

    /// <summary>
    /// Extracts all dependency references from a content string using regex.
    /// Deduplicates within the content chunk (case-insensitive).
    /// </summary>
    static List<DependencyReference> ExtractReferences(string content)
    {
        var refs = new List<DependencyReference>();
        var seen = new HashSet<(DependencyRefType, string)>(RefKeyComparer.Instance);

        bool hasSlash = content.Contains('\\') || content.Contains('/');

        // File paths (require path separators)
        if (hasSlash)
        {
            foreach (Match m in PathPattern().Matches(content))
            {
                var raw = m.Value;
                if (m.Index >= 3 && content[m.Index - 3] == ':' && content[m.Index - 2] == '/' && content[m.Index - 1] == '/') continue; // skip paths after ://
                if (!IsLikelyPath(raw)) continue; // filter base64 and garbage

                var normalized = raw.Replace('/', '\\');
                var tag = DetectSourceTag(content, m.Index);

                if (seen.Add((DependencyRefType.FilePath, normalized)))
                {
                    refs.Add(new DependencyReference
                    {
                        RawValue = normalized,
                        Type = DependencyRefType.FilePath,
                        SourceTag = tag,
                    });
                }
            }
        }

        // Single-segment file references (e.g. "handattack.tactics") — require a dot
        if (content.Contains('.'))
        {
            foreach (Match m in SingleSegmentFilePattern().Matches(content))
            {
                var raw = m.Value;
                var tag = DetectSourceTag(content, m.Index);
                if (seen.Add((DependencyRefType.FilePath, raw)))
                {
                    refs.Add(new DependencyReference
                    {
                        RawValue = raw,
                        Type = DependencyRefType.FilePath,
                        SourceTag = tag,
                    });
                }
            }
        }

        // STR_ keys
        if (content.Contains("STR_", StringComparison.Ordinal))
        {
            foreach (Match m in StrKeyPattern().Matches(content))
            {
                if (seen.Add((DependencyRefType.StringKey, m.Value)))
                {
                    refs.Add(new DependencyReference
                    {
                        RawValue = m.Value,
                        Type = DependencyRefType.StringKey,
                    });
                }
            }
        }

        // Soundset names (require <soundset tag)
        if (content.Contains("<soundset", StringComparison.OrdinalIgnoreCase))
        {
            ExtractSoundsetNames(SoundsetNameAttrPattern(), content, refs, seen, skipPaths: false);
            ExtractSoundsetNames(SoundsetContentPattern(), content, refs, seen, skipPaths: true);
        }

        return refs;
    }

    static void ExtractSoundsetNames(Regex pattern, string content,
        List<DependencyReference> refs, HashSet<(DependencyRefType, string)> seen, bool skipPaths)
    {
        foreach (Match m in pattern.Matches(content))
        {
            var name = m.Groups[1].Value;
            if (skipPaths && (name.Contains('\\') || name.Contains('/'))) continue;
            if (seen.Add((DependencyRefType.SoundsetName, name)))
            {
                refs.Add(new DependencyReference
                {
                    RawValue = name,
                    Type = DependencyRefType.SoundsetName,
                    SourceTag = "soundset",
                });
            }
        }
    }

    sealed class RefKeyComparer : IEqualityComparer<(DependencyRefType, string)>
    {
        public static readonly RefKeyComparer Instance = new();
        public bool Equals((DependencyRefType, string) x, (DependencyRefType, string) y)
            => x.Item1 == y.Item1 && string.Equals(x.Item2, y.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((DependencyRefType, string) obj)
            => HashCode.Combine(obj.Item1, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Item2));
    }

    /// <summary>
    /// Looks backwards from a regex match position to find the enclosing XML tag or attribute name.
    /// </summary>
    static string? DetectSourceTag(string content, int pos)
    {
        int i = pos - 1;

        // Skip whitespace
        while (i >= 0 && content[i] is ' ' or '\t' or '\r' or '\n')
            i--;

        // If preceded by '>', this is element content — find the tag name
        if (i >= 0 && content[i] == '>')
        {
            int tagEnd = i;
            i--;
            while (i >= 0 && content[i] != '<')
                i--;
            if (i >= 0)
            {
                int tagStart = i + 1;
                int nameEnd = tagStart;
                while (nameEnd < tagEnd && content[nameEnd] != ' ' && content[nameEnd] != '>' && content[nameEnd] != '/')
                    nameEnd++;
                if (nameEnd > tagStart)
                    return content[tagStart..nameEnd].ToLowerInvariant();
            }
        }

        // If preceded by '="' pattern, this is an attribute value — find attribute name
        if (i >= 0 && content[i] == '"')
        {
            i--;
            if (i >= 0 && content[i] == '=')
            {
                i--;
                while (i >= 0 && content[i] is ' ' or '\t')
                    i--;
                int attrEnd = i + 1;
                while (i >= 0 && content[i] != ' ' && content[i] != '<' && content[i] != '>')
                    i--;
                if (i + 1 < attrEnd)
                    return content[(i + 1)..attrEnd].ToLowerInvariant();
            }
        }

        return null;
    }

    // Resolution

    static void ResolveReference(DependencyReference reference, string entryPath, FileIndex index, SoundsetIndex? soundsetIndex, string stringTableLanguage)
    {
        switch (reference.Type)
        {
            case DependencyRefType.FilePath:
                reference.Resolved.AddRange(index.FindByPartialPath(reference.RawValue, excludePath: entryPath));
                break;

            case DependencyRefType.SoundsetName:
                if (soundsetIndex != null)
                    ResolveSoundsetName(reference, soundsetIndex);
                break;

            case DependencyRefType.StringKey:
                var stringTables = index.Find("string_table.txt");
                var preferred = stringTables.FirstOrDefault(e =>
                    e.FullRelativePath.Contains(stringTableLanguage, StringComparison.OrdinalIgnoreCase));
                if (preferred != null)
                    reference.Resolved.Add(preferred);
                else if (stringTables.Count > 0)
                    reference.Resolved.Add(stringTables[0]); // fallback to first available
                break;
        }
    }

    /// <summary>
    /// Rejects matches that look like base64-encoded data or other garbage rather than real file paths.
    /// </summary>
    static bool IsLikelyPath(string match)
    {
        // Too short to be a real path (e.g. "yi\M", "1\9")
        if (match.Length < 5)
            return false;

        // Trailing dot without extension (sentence fragments like "attack\explore plans to analyze.")
        if (match[^1] == '.')
            return false;

        // Paths with spaces are almost certainly sentence fragments, not real game paths
        if (match.Contains(' '))
            return false;

        // Base64-specific characters never appear in game file paths
        if (match.Contains('+') || match.Contains('='))
            return false;

        // Exceeds MAX_PATH — no real game path is this long
        if (match.Length > 260)
            return false;

        // Real game paths have meaningful segment names.
        // Require at least one segment >= 3 chars to reject binary garbage.
        int separatorCount = 0;
        int segmentLength = 0;
        bool hasLongSegment = false;
        foreach (var c in match)
        {
            if (c is '/' or '\\')
            {
                if (segmentLength >= 3) hasLongSegment = true;
                segmentLength = 0;
                if (++separatorCount > 15)
                    return false;
            }
            else
            {
                segmentLength++;
            }
        }
        // Check the last segment too
        if (segmentLength >= 3) hasLongSegment = true;

        return hasLongSegment;
    }

    /// <summary>
    /// Builds dependencies for a TMM model file: companion .tmm.data and .material files.
    /// </summary>
    public static DependencyResult FindDependenciesForTmm(string entryPath, FileIndex? index = null)
    {
        var refs = new List<DependencyReference>();
        var tmmFileName = Path.GetFileName(entryPath);

        // Companion geometry data file: {name}.tmm.data
        var dataFileName = tmmFileName + ".data";
        var dataRef = new DependencyReference
        {
            RawValue = dataFileName,
            Type = DependencyRefType.FilePath,
            SourceTag = "geometry",
        };
        if (index != null)
            dataRef.Resolved.AddRange(index.Find(dataFileName));
        refs.Add(dataRef);

        // Companion material file: {stem}.material.XMB or {stem}.material
        // Note: the .tmm extension is stripped — "armory_a_age2.tmm" → "armory_a_age2.material.XMB"
        var tmmStem = Path.GetFileNameWithoutExtension(tmmFileName);
        var matFileName = tmmStem + ".material";
        var matRef = new DependencyReference
        {
            RawValue = matFileName,
            Type = DependencyRefType.FilePath,
            SourceTag = "material",
        };
        if (index != null)
        {
            var matEntries = index.Find(matFileName + ".XMB");
            if (matEntries.Count == 0)
                matEntries = index.Find(matFileName);
            matRef.Resolved.AddRange(matEntries);
        }
        refs.Add(matRef);

        return new DependencyResult
        {
            EntryPath = entryPath,
            Groups = [new DependencyGroup { References = refs }],
        };
    }

    /// <summary>
    /// Unified entry point: determines file type, reads/decompresses data, and returns dependencies.
    /// Handles .tmm (companion files), .bank (redirects to soundset), and text-based files (XML/JSON/etc).
    /// </summary>
    /// <param name="entryPath">Full relative path of the file being analyzed.</param>
    /// <param name="fileData">Raw (possibly compressed) file data.</param>
    /// <param name="index">File index for resolving references.</param>
    /// <param name="soundsetIndex">Soundset index for resolving soundset names.</param>
    /// <param name="stringTableLanguage">Preferred language for string table resolution.</param>
    /// <param name="readFileAsync">Delegate to read a file from a FileIndexEntry (for bank→soundset redirect). Caller must dispose the returned buffer.</param>
    /// <param name="filterEntityName">When set, only return the group matching this entity name.</param>
    public static async Task<DependencyResult> FindDependenciesForFileAsync(
        string entryPath,
        PooledBuffer fileData,
        FileIndex? index,
        SoundsetIndex? soundsetIndex = null,
        string? stringTableLanguage = null,
        Func<FileIndexEntry, ValueTask<PooledBuffer?>>? readFileAsync = null,
        string? filterEntityName = null)
    {
        var ext = Path.GetExtension(entryPath);

        // TMM: companion files only
        if (ext.Equals(".tmm", StringComparison.OrdinalIgnoreCase))
            return FindDependenciesForTmm(entryPath, index);

        // Bank: redirect to associated soundset file
        if (ext.Equals(".bank", StringComparison.OrdinalIgnoreCase))
            return await FindDependenciesForBankAsync(entryPath, index, soundsetIndex, stringTableLanguage, readFileAsync);

        // Text-based: decompress and parse
        using var decompressed = BarCompression.EnsureDecompressedPooled(fileData, out _);
        var content = ConversionHelper.GetTextContent(decompressed.Span, entryPath);
        return FindDependencies(content, entryPath, index, soundsetIndex, stringTableLanguage, filterEntityName);
    }

    /// <summary>
    /// Handles .bank files by finding and reading the associated soundset file.
    /// E.g. "greek.bank" → reads "soundsets_greek.soundset.XMB" and parses its dependencies.
    /// </summary>
    static async Task<DependencyResult> FindDependenciesForBankAsync(
        string entryPath,
        FileIndex? index,
        SoundsetIndex? soundsetIndex,
        string? stringTableLanguage,
        Func<FileIndexEntry, ValueTask<PooledBuffer?>>? readFileAsync)
    {
        if (index == null || readFileAsync == null)
            return new DependencyResult { EntryPath = entryPath, Groups = [] };

        var bankName = Path.GetFileNameWithoutExtension(entryPath);
        var soundsetFileName = $"soundsets_{bankName}.soundset.XMB";

        var soundsetEntries = index.Find(soundsetFileName);
        if (soundsetEntries.Count == 0)
            return new DependencyResult { EntryPath = entryPath, Groups = [] };

        using var data = await readFileAsync(soundsetEntries[0]);
        if (data == null)
            return new DependencyResult { EntryPath = entryPath, Groups = [] };

        using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
        var content = ConversionHelper.GetTextContent(decompressed.Span, soundsetFileName);
        return FindDependencies(content, soundsetFileName, index, soundsetIndex, stringTableLanguage);
    }

    static void ResolveSoundsetName(DependencyReference reference, SoundsetIndex soundsetIndex)
    {
        var entry = soundsetIndex.Find(reference.RawValue);
        if (entry == null) return;

        reference.Resolved.Add(entry.SoundsetFile);
        if (entry.BankFile != null)
            reference.Resolved.Add(entry.BankFile);
    }
}
