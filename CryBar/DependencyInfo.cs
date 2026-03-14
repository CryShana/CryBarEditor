namespace CryBar;

public enum DependencyRefType
{
    FilePath,
    StringKey,
    SoundsetName
}

/// <summary>
/// A single reference found in file content (a path, STR_ key, or soundset name).
/// </summary>
public class DependencyReference
{
    /// <summary>Raw value as parsed from content (e.g. "greek\units\infantry\hoplite\hoplite.xml").</summary>
    public required string RawValue { get; init; }

    /// <summary>Type of reference.</summary>
    public required DependencyRefType Type { get; init; }

    /// <summary>XML tag or attribute name where this reference was found (e.g. "animfile", "icon", "soundset").</summary>
    public string? SourceTag { get; init; }

    /// <summary>Matched files from the FileIndex. Empty if unresolved or index not provided.</summary>
    public List<FileIndexEntry> Resolved { get; init; } = [];
}

/// <summary>
/// A group of references belonging to a single entity (e.g. a proto unit, a tech, a god power).
/// When the file has no entity structure, a single group with EntityName = null holds all references.
/// </summary>
public class DependencyGroup
{
    /// <summary>Entity name (e.g. "Hoplite"). Null for file-level (ungrouped) references.</summary>
    public string? EntityName { get; init; }

    /// <summary>Entity type (e.g. "unit", "tech", "power"). Null when ungrouped.</summary>
    public string? EntityType { get; init; }

    /// <summary>All references found within this entity's scope.</summary>
    public required List<DependencyReference> References { get; init; }
}

/// <summary>
/// Result of scanning file content for dependencies.
/// </summary>
public class DependencyResult
{
    /// <summary>Path of the file that was scanned.</summary>
    public required string EntryPath { get; init; }

    /// <summary>Entity-grouped references. Files without entities have a single group with EntityName = null.</summary>
    public required List<DependencyGroup> Groups { get; init; }

    /// <summary>All references flattened across groups.</summary>
    public IEnumerable<DependencyReference> AllReferences => Groups.SelectMany(g => g.References);
}
