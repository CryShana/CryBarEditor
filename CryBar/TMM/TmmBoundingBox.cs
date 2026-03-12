namespace CryBar.TMM;

public readonly struct TmmBoundingBox
{
    public float MinX { get; init; }
    public float MinY { get; init; }
    public float MinZ { get; init; }
    public float MaxX { get; init; }
    public float MaxY { get; init; }
    public float MaxZ { get; init; }

    public override string ToString() =>
        $"({MinX:F2}, {MinY:F2}, {MinZ:F2}) to ({MaxX:F2}, {MaxY:F2}, {MaxZ:F2})";
}
