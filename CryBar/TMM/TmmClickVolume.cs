namespace CryBar.TMM;

public sealed class TmmClickVolume
{
    public byte Type { get; init; }
    public bool AreVoxelsDefined { get; init; }
    public uint Version { get; init; }
    public float[]? BoundsMin { get; init; }
    public float[]? BoundsMax { get; init; }
    public int VoxelDimensions { get; init; }
    public float VoxelSizeLargestAxis { get; init; }
    public byte[]? VoxelData { get; init; }
}
