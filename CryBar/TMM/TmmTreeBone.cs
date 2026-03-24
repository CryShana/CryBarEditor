namespace CryBar.TMM;

public sealed class TmmTreeBone
{
    public required string Name { get; init; }
    public required float[] BindPose { get; init; }
    public required float[] InverseBindPose { get; init; }
}
