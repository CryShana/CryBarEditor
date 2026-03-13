namespace CryBar;

/// <summary>
/// Represents a single sound file reference within a soundset.
/// </summary>
public class SoundsetSound
{
    /// <summary>Relative path from sound root, e.g. "shared\vo\death\death_female1.wav"</summary>
    public required string Filename { get; init; }

    /// <summary>Volume override (0.0–1.0). Null means default/full volume.</summary>
    public float? Volume { get; init; }

    /// <summary>Weight for random selection. Null means equal weighting.</summary>
    public float? Weight { get; init; }

    // From soundmanifest (optional enrichment)
    /// <summary>Duration in seconds from soundmanifest.</summary>
    public double? Length { get; set; }

    /// <summary>Number of samples from soundmanifest.</summary>
    public int? NumSamples { get; set; }
}

/// <summary>
/// A named soundset containing one or more sound files.
/// </summary>
public class SoundsetDefinition
{
    public required string Name { get; init; }
    public float? Volume { get; init; }
    public int? MaxNum { get; init; }
    public required List<SoundsetSound> Sounds { get; init; }
}

/// <summary>
/// Result of resolving an FMOD event to its underlying sound files.
/// </summary>
public class SoundsetResolution
{
    /// <summary>The soundset name that was matched.</summary>
    public required string SoundsetName { get; init; }

    /// <summary>Which file the soundset was found in.</summary>
    public required string SourceFile { get; init; }

    /// <summary>The resolved soundset definition with all sounds.</summary>
    public required SoundsetDefinition Soundset { get; init; }

    /// <summary>Whether soundmanifest data was enriched into the sounds.</summary>
    public bool HasManifestData { get; set; }
}
