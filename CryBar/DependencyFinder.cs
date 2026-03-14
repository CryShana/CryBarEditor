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
    [GeneratedRegex(@"(?<![\\\/\w])[A-Za-z][\w]*\.[A-Za-z][\w]+(?![\\\/\w])")]
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
    public static DependencyResult FindDependencies(string content, string entryPath, FileIndex? index = null, SoundsetIndex? soundsetIndex = null)
    {
        // Preprocess: unescape JSON double-backslashes
        var processed = content;
        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.StartsWith("{") || trimmed.StartsWith("["))
            processed = processed.Replace("\\\\", "\\");

        // Build groups (with entity detection for XML content)
        var groups = BuildGroups(processed);

        // Resolve against index
        if (index != null)
        {
            foreach (var group in groups)
                foreach (var r in group.References)
                    ResolveReference(r, entryPath, index, soundsetIndex);
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
    static List<DependencyGroup> BuildGroups(string content)
    {
        var trimmed = content.AsSpan().TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '<')
        {
            // Non-XML content: single ungrouped group
            var refs = ExtractReferences(content);
            return refs.Count > 0
                ? [new DependencyGroup { References = refs }]
                : [];
        }

        // Single XmlReader pass: collect all direct children with their tag, name attr, and OuterXml
        var children = new List<(string tag, string? name, string xml)>();

        try
        {
            using var reader = XmlReader.Create(new StringReader(content), SafeXmlSettings);

            // Advance to root element
            while (reader.Read())
                if (reader.NodeType == XmlNodeType.Element) break;
            if (reader.NodeType != XmlNodeType.Element)
            {
                var refs = ExtractReferences(content);
                return refs.Count > 0
                    ? [new DependencyGroup { References = refs }]
                    : [];
            }

            int rootDepth = reader.Depth;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Depth != rootDepth + 1)
                    continue;

                var tag = reader.Name;
                var name = reader.GetAttribute("name");

                if (reader.IsEmptyElement)
                {
                    children.Add((tag, name, ""));
                }
                else
                {
                    children.Add((tag, name, reader.ReadOuterXml()));
                }
            }
        }
        catch (XmlException)
        {
            // XML parse error mid-way: process what we collected
        }

        if (children.Count == 0)
        {
            var refs = ExtractReferences(content);
            return refs.Count > 0
                ? [new DependencyGroup { References = refs }]
                : [];
        }

        // Determine entity tags: direct children appearing ≥2 times with at least one name attribute
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hasNameAttr = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, name, _) in children)
        {
            counts.TryGetValue(tag, out var c);
            counts[tag] = c + 1;
            if (name != null)
                hasNameAttr.Add(tag);
        }

        var entityTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, count) in counts)
        {
            if (count >= 2 && hasNameAttr.Contains(tag))
                entityTags.Add(tag);
        }

        if (entityTags.Count == 0)
        {
            // No entity structure — single ungrouped group
            var refs = ExtractReferences(content);
            return refs.Count > 0
                ? [new DependencyGroup { References = refs }]
                : [];
        }

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
                if (raw.Contains("://")) continue; // filter URIs

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

    static void ResolveReference(DependencyReference reference, string entryPath, FileIndex index, SoundsetIndex? soundsetIndex)
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
                var english = stringTables.FirstOrDefault(e =>
                    e.FullRelativePath.Contains("English", StringComparison.OrdinalIgnoreCase));
                if (english != null)
                    reference.Resolved.Add(english);
                break;
        }
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
