namespace CryBar.TMM;

public readonly struct TmmSpeedTreeVertex
{
    public ushort AnchorX { get; init; }
    public ushort AnchorY { get; init; }
    public ushort AnchorZ { get; init; }
    public ushort GeometryType { get; init; }
    public ushort WindDataX { get; init; }
    public ushort WindDataY { get; init; }

    public const int SizeInBytes = 12;
}
