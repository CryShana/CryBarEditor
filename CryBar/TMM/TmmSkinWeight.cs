namespace CryBar.TMM;

public readonly struct TmmSkinWeight
{
    public byte Weight0 { get; init; }
    public byte Weight1 { get; init; }
    public byte Weight2 { get; init; }
    public byte Weight3 { get; init; }

    public byte BoneIndex0 { get; init; }
    public byte BoneIndex1 { get; init; }
    public byte BoneIndex2 { get; init; }
    public byte BoneIndex3 { get; init; }

    /// <summary>Size of a single skin weight entry in the .tmm.data file (bytes).</summary>
    public const int SizeInBytes = 8; // 4 weights + 4 bone indices
}
