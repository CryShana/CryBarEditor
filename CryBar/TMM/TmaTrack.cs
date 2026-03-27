namespace CryBar.TMM;

/// <summary>Keyframe encoding method for one component of a TMA animation track.</summary>
public enum TmaEncoding : byte
{
    /// <summary>Single constant value for all frames (16 bytes).</summary>
    Constant = 0,

    /// <summary>One raw value per keyframe (translation/scale: N×12 bytes; rotation: N×16 bytes).</summary>
    Raw = 1,

    /// <summary>Compressed quaternion, 4 bytes per keyframe.</summary>
    Quat32 = 2,

    /// <summary>Compressed quaternion, 8 bytes per keyframe.</summary>
    Quat64 = 3,
}

/// <summary>One animation track (bone channel) in a TMA file.</summary>
public sealed class TmaTrack
{
    public required string Name { get; init; }
    public byte TrackVersion { get; init; }
    public TmaEncoding TranslationEncoding { get; init; }
    public TmaEncoding RotationEncoding { get; init; }
    public TmaEncoding ScaleEncoding { get; init; }
    public int KeyframeCount { get; init; }

    /// <summary>Raw bytes of the translation data block.</summary>
    public byte[] TranslationData { get; init; } = [];

    /// <summary>Raw bytes of the rotation data block.</summary>
    public byte[] RotationData { get; init; } = [];

    /// <summary>Raw bytes of the scale data block.</summary>
    public byte[] ScaleData { get; init; } = [];
}
