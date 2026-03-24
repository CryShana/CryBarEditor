namespace CryBar.TMM;

public sealed class TmmAttachment
{
    public uint TypeFlag { get; init; }
    public int ParentBoneId { get; init; }
    public required string Name { get; init; }

    /// <summary>Adjustment transform mutated attach matrix (4x3 stored as 12 floats, row-major).</summary>
    public required float[] AdjustmentTransformMatrix { get; init; }

    /// <summary>Local transform matrix (4x3 stored as 12 floats, row-major).</summary>
    public required float[] LocalTransformMatrix { get; init; }

    /// <summary>Auto=0, DoNotDummyBone=1, AddToSim=2, ForceToDummyBoneType=3</summary>
    public uint DummyBoneMode { get; init; }

    /// <summary>Auto=0, BindPoseOnly=1, BindPoseAnimationFirstFrameOnly=2, BindPoseSpecificFrameOnly=3, AllFrames=4, LimitedFrames=5</summary>
    public uint DummyBoneTransformMode { get; init; }

    public string ForcedDummyBoneName { get; init; } = "";
    public int FrameLimit { get; init; }
    public float FramePosition { get; init; }

    /// <summary>Auto=0, AllAnimations=1, OnlySingleAnimation=2, OnlySpecificAnimations=3, AllExceptSpecificAnimations=4</summary>
    public uint DummyBoneAnimationFilter { get; init; }

    public string[] DummySpecificAnimations { get; init; } = [];
}
