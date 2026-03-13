namespace CryBar.TMM;

/// <summary>Base class for TMA animation controllers (visibility, footprints, etc.).</summary>
public abstract class TmaController
{
    public int Type { get; init; }
}

/// <summary>Type 1 controller: toggles visibility of an attach point over a time range.</summary>
public sealed class TmaVisibilityController : TmaController
{
    public float Start { get; init; }
    public float End { get; init; }
    public float EaseIn { get; init; }
    public float EaseOut { get; init; }
    public bool InvertLogic { get; init; }
    public required string AttachPointName { get; init; }
}

/// <summary>Type 2 controller: spawns a footprint decal at an attach point.</summary>
public sealed class TmaFootprintController : TmaController
{
    public float SpawnTime { get; init; }
    public required string FootprintName { get; init; }
    public int FootprintId { get; init; }
    public bool InvertTextureY { get; init; }
    public required string AttachPointName { get; init; }
    public bool IsRightSide { get; init; }
}

/// <summary>Placeholder for controller types not yet documented.</summary>
public sealed class TmaUnknownController : TmaController { }
