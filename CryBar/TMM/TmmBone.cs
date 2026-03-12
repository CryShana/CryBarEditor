namespace CryBar.TMM;

public sealed class TmmBone
{
    public required string Name { get; init; }
    public int ParentId { get; init; }
    public float CollisionOffsetX { get; init; }
    public float CollisionOffsetY { get; init; }
    public float CollisionOffsetZ { get; init; }
    public float Radius { get; init; }

    /// <summary>Parent-space transform matrix (4x4, column-major as stored in file).</summary>
    public required float[] ParentSpaceMatrix { get; init; }

    /// <summary>World-space transform matrix (4x4, column-major as stored in file).</summary>
    public required float[] WorldSpaceMatrix { get; init; }

    /// <summary>Inverse bind-pose matrix (4x4, column-major as stored in file).</summary>
    public required float[] InverseBindMatrix { get; init; }
}
