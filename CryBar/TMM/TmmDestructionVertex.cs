namespace CryBar.TMM;

public readonly struct TmmDestructionVertex
{
    public byte BurntInteriorColor { get; init; }
    public ushort DestructionBoneIndex { get; init; }

    public const int SizeInBytes = 2;
}
