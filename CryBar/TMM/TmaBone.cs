namespace CryBar.TMM;

/// <summary>A single bone in a TMA animation skeleton.</summary>
public sealed class TmaBone
{
    public required string Name { get; init; }

    /// <summary>Parent bone index. -1 means root bone.</summary>
    public int ParentId { get; init; }

    /// <summary>Local-space transform matrix (4×4, 16 floats, row-major).</summary>
    public required float[] LocalTransform { get; init; }

    /// <summary>Bind-pose matrix (4×4, 16 floats, row-major).</summary>
    public required float[] BindPose { get; init; }

    /// <summary>Inverse bind-pose matrix (4×4, 16 floats, row-major).</summary>
    public required float[] InverseBindPose { get; init; }
}
