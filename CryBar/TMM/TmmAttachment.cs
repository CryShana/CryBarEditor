namespace CryBar.TMM;

public sealed class TmmAttachment
{
    public uint TypeFlag { get; init; }
    public int ParentBoneId { get; init; }
    public required string Name { get; init; }

    /// <summary>First transform matrix (4x3 stored as 12 floats, row-major).</summary>
    public required float[] TransformMatrix1 { get; init; }

    /// <summary>Second transform matrix (4x3 stored as 12 floats, row-major).</summary>
    public required float[] TransformMatrix2 { get; init; }

    public uint UnknownFlag1 { get; init; }
    public uint UnknownFlag2 { get; init; }
    public string SecondName { get; init; } = "";
}
