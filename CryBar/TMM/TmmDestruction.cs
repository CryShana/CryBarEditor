namespace CryBar.TMM;

public sealed class TmmDestruction
{
    public uint ErrorFlags { get; init; }
    public required TmmDestructionBone[] ChunkBones { get; init; }
    public bool HasBase { get; init; }
    public uint BaseChunkIndex { get; init; }
    public bool EnableProxyGroupShapes { get; init; }
    public int JitterCountOnDeath { get; init; }
    public float JitterIntensityOnDeath { get; init; }
    public float MotionStopHideDelayOnDeath { get; init; }
    public float MotionStopHideTimeOnDeath { get; init; }
    public float MotionStopHideDelayRandomExtensionOnDeath { get; init; }
    public float ForceMultiplierOnDeath { get; init; }
    public int PhysicsTypeOnDeath { get; init; }
    public bool AllowDecalsVFXOnDeath { get; init; }
    public bool AllowPopcornVFXOnDeath { get; init; }
    public float PercentToLeave { get; init; }
    public string? CustomDebrisPath { get; init; }
    public uint DebrisType { get; init; }
    public uint DebrisCivType { get; init; }
    public uint DefaultFullDestroyMode { get; init; }
    public required TmmProxyGroup[] ProxyGroups { get; init; }
    public required TmmDestructionInterval[] Intervals { get; init; }
}

public sealed class TmmDestructionBone
{
    public required string Name { get; init; }
    public required float[] BindPose { get; init; }
    public required float[] InverseBindPose { get; init; }
}

public sealed class TmmProxyGroup
{
    public required float[] ProxyCenter { get; init; }
    public required float[] ImpactPoint { get; init; }
    public uint FirstChunkBoneIndex { get; init; }
    public uint ChunkCount { get; init; }
    public int JitterCount { get; init; }
    public float JitterIntensity { get; init; }
    public float MotionStopHideDelay { get; init; }
    public float MotionStopHideTime { get; init; }
    public float MotionStopHideDelayRandomExtension { get; init; }
    public float ForceMultiplier { get; init; }
    public required float[] ForceDirectionDefault { get; init; }
    public required float[] ForceDirectionReal { get; init; }
    public required float[] BoundsMin { get; init; }
    public required float[] BoundsMax { get; init; }
    public required float[] BoundsCenter { get; init; }
    public required float[] BoundsSize { get; init; }
    public int PhysicsType { get; init; }
    public bool AllowDecalsVFX { get; init; }
    public bool AllowPopcornVFX { get; init; }
    public required int[] ProxyGroupOrder { get; init; }
    public int Flags { get; init; }
}

public sealed class TmmDestructionInterval
{
    public float EventThreshold { get; init; }
    public required int[] ProxyGroupIndices { get; init; }
}
