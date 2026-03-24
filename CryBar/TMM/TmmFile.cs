using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace CryBar.TMM;

/// <summary>
/// Parses .tmm model files (Age of Mythology: Retold format, versions 30+).
/// Replaces the older TmmModel class with full parsing of all sections.
/// </summary>
public class TmmFile
{
    const int MaxImportNames = 1000;
    const int MaxMeshGroups = 10000;
    const int MaxMaterials = 10000;
    const int MaxBones = 1000;
    const int MaxAttachments = 10000;
    const int MaxVertices = 10_000_000;
    const int MaxTriangleVerts = 30_000_000;
    const int MaxNameLength = 5000;

    [MemberNotNullWhen(true, nameof(ImportNames), nameof(MainMatrix),
        nameof(Attachments), nameof(MeshGroups), nameof(Materials),
        nameof(Submodels), nameof(Bones))]
    public bool Parsed { get; }
    public uint Version { get; private set; }
    public uint SharedAnimationBucketCount { get; private set; }
    public bool IsTerrainEmbellishment { get; private set; }
    public bool EnableRayTracingForModel { get; private set; }

    // Import metadata
    public string[]? ImportNames { get; private set; }

    // Bounding boxes
    public TmmBoundingBox BoundingBox { get; private set; }
    public TmmBoundingBox ExtendedBoundingBox { get; private set; }
    public float BoundsRadius { get; private set; }

    // Section counts
    public uint NumMeshGroups { get; private set; }
    public uint NumMaterials { get; private set; }
    public uint NumSubmodels { get; private set; }
    public uint NumBones { get; private set; }
    public uint NumAttachments { get; private set; }
    public uint NumVertices { get; private set; }
    public uint NumTriangleVerts { get; private set; }

    // Data block layout
    public uint VerticesStart { get; private set; }
    public uint VerticesByteLength { get; private set; }
    public uint TrianglesStart { get; private set; }
    public uint TrianglesByteLength { get; private set; }
    public uint WeightsStart { get; private set; }
    public uint WeightsByteLength { get; private set; }
    public uint HeightsStart { get; private set; }
    public uint HeightsByteLength { get; private set; }

    // Buffer layout — currently unnamed
    public uint DestructionBufferStart { get; private set; }
    public uint DestructionBufferByteLength { get; private set; }
    public uint ColorBufferStart { get; private set; }
    public uint ColorBufferByteLength { get; private set; }
    public uint SpeedTreeBufferStart { get; private set; }
    public uint SpeedTreeBufferByteLength { get; private set; }

    // Main matrix (4x3 stored as 12 floats, expanded to 4x4)
    public float[]? MainMatrix { get; private set; }

    // Parsed sections
    public TmmAttachment[]? Attachments { get; private set; }
    public TmmMeshGroup[]? MeshGroups { get; private set; }
    public string[]? Materials { get; private set; }
    public string[]? Submodels { get; private set; }
    public TmmBone[]? Bones { get; private set; }
    public bool FullyParsed { get; private set; }
    public TmmModifiedBone[]? ModifiedBones { get; private set; }
    public byte AutoBurnMode { get; private set; }
    public TmmDestruction? Destruction { get; private set; }
    public TmmPhysicsTemplate? PhysicsTemplate { get; private set; }
    public TmmTreeBone[]? TreeDestructionBones { get; private set; }
    public TmmClickVolume? ClickVolume { get; private set; }
    public TmmAutoAttachInfo? AutoAttachInfo { get; private set; }

    public TmmFile(ReadOnlyMemory<byte> data)
    {
        Parsed = Parse(data.Span);
    }

    bool Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return false;

        // Signature: "BTMM"
        if (data is not [0x42, 0x54, 0x4d, 0x4d, ..]) return false;

        var offset = 4;

        // Version
        var version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        if (version < 30 || version > 255) return false;
        Version = version;

        // "DP" marker
        if (offset + 2 > data.Length) return false;
        if (data[offset] != 0x44 || data[offset + 1] != 0x50) return false;
        offset += 2;

        // Import metadata block
        if (!TryReadInt32(data, ref offset, out var blockByteLength)) return false;
        if (!TryReadUInt32(data, ref offset, out var numImportNames)) return false;
        if (numImportNames > MaxImportNames) return false;

        var importNames = new string[numImportNames];
        for (int i = 0; i < numImportNames; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var name)) return false;
            importNames[i] = name;
            offset += 16; // 4 unknown int32 values
            if (offset > data.Length) return false;
        }
        ImportNames = importNames;

        // Bounding boxes (6 floats each)
        if (offset + 24 > data.Length) return false;
        BoundingBox = ReadBoundingBox(data, ref offset);

        if (offset + 24 > data.Length) return false;
        ExtendedBoundingBox = ReadBoundingBox(data, ref offset);

        // Bounds radius
        if (!TryReadFloat(data, ref offset, out var boundsRadius)) return false;
        BoundsRadius = boundsRadius;

        // Section counts
        if (!TryReadUInt32(data, ref offset, out var numMeshGroups)) return false;
        if (numMeshGroups > MaxMeshGroups) return false;
        NumMeshGroups = numMeshGroups;

        if (!TryReadUInt32(data, ref offset, out var numMaterials)) return false;
        if (numMaterials > MaxMaterials) return false;
        NumMaterials = numMaterials;

        if (!TryReadUInt32(data, ref offset, out var numSubmodels)) return false;
        NumSubmodels = numSubmodels;

        if (!TryReadUInt32(data, ref offset, out var numBones)) return false;
        if (numBones > MaxBones) return false;
        NumBones = numBones;

        if (!TryReadUInt32(data, ref offset, out var sharedAnimBucketCount)) return false;
        SharedAnimationBucketCount = sharedAnimBucketCount;

        if (!TryReadUInt32(data, ref offset, out var numAttachments)) return false;
        if (numAttachments > MaxAttachments) return false;
        NumAttachments = numAttachments;

        if (!TryReadUInt32(data, ref offset, out var numVertices)) return false;
        if (numVertices > MaxVertices) return false;
        NumVertices = numVertices;

        if (!TryReadUInt32(data, ref offset, out var numTriangleVerts)) return false;
        if (numTriangleVerts > MaxTriangleVerts) return false;
        NumTriangleVerts = numTriangleVerts;

        // Data block layout (offsets + byte lengths)
        if (!TryReadUInt32(data, ref offset, out var vertStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var vertBl)) return false;
        VerticesStart = vertStart; VerticesByteLength = vertBl;

        if (!TryReadUInt32(data, ref offset, out var triStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var triBl)) return false;
        TrianglesStart = triStart; TrianglesByteLength = triBl;

        if (!TryReadUInt32(data, ref offset, out var wStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var wBl)) return false;
        WeightsStart = wStart; WeightsByteLength = wBl;

        if (!TryReadUInt32(data, ref offset, out var destStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var destBl)) return false;
        DestructionBufferStart = destStart; DestructionBufferByteLength = destBl;

        if (!TryReadUInt32(data, ref offset, out var colStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var colBl)) return false;
        ColorBufferStart = colStart; ColorBufferByteLength = colBl;

        if (!TryReadUInt32(data, ref offset, out var hStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var hBl)) return false;
        HeightsStart = hStart; HeightsByteLength = hBl;

        if (!TryReadUInt32(data, ref offset, out var stStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var stBl)) return false;
        SpeedTreeBufferStart = stStart; SpeedTreeBufferByteLength = stBl;

        if (!TryReadBool(data, ref offset, out var isTerrainEmbellishment)) return false;
        IsTerrainEmbellishment = isTerrainEmbellishment;
        if (!TryReadBool(data, ref offset, out var enableRayTracing)) return false;
        EnableRayTracingForModel = enableRayTracing;

        // Main matrix (4x3 = 12 floats, expanded to 4x4)
        if (offset + 48 > data.Length) return false;
        var mainMatrix = new float[16];
        for (int i = 0; i < 12; i++)
        {
            mainMatrix[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)); offset += 4;
        }
        mainMatrix[12] = 0; mainMatrix[13] = 0; mainMatrix[14] = 0; mainMatrix[15] = 1;
        MainMatrix = mainMatrix;

        // Attachments
        var attachments = new TmmAttachment[numAttachments];
        for (int i = 0; i < numAttachments; i++)
        {
            if (!TryReadUInt32(data, ref offset, out var typeFlag)) return false;
            if (!TryReadInt32(data, ref offset, out var parentBoneId)) return false;
            if (!TryReadUTF16String(data, ref offset, out var attachName)) return false;

            // Two 4x3 transform matrices (12 floats each)
            if (offset + 96 > data.Length) return false;
            var mat1 = ReadFloats(data, ref offset, 12);
            var mat2 = ReadFloats(data, ref offset, 12);

            if (!TryReadUInt32(data, ref offset, out var flag1)) return false;
            if (!TryReadUInt32(data, ref offset, out var flag2)) return false;

            if (!TryReadUTF16String(data, ref offset, out var secondName)) return false;

            if (!TryReadInt32(data, ref offset, out var frameLimit)) return false;
            if (!TryReadFloat(data, ref offset, out var framePosition)) return false;
            if (!TryReadUInt32(data, ref offset, out var animFilter)) return false;
            if (!TryReadUInt32(data, ref offset, out var animCount)) return false;

            var anims = new string[animCount];
            for (int j = 0; j < animCount; j++)
            {
                if (!TryReadUTF16String(data, ref offset, out var animPath)) return false;
                anims[j] = animPath;
            }

            attachments[i] = new TmmAttachment
            {
                TypeFlag = typeFlag,
                ParentBoneId = parentBoneId,
                Name = attachName,
                AdjustmentTransformMatrix = mat1,
                LocalTransformMatrix = mat2,
                DummyBoneMode = flag1,
                DummyBoneTransformMode = flag2,
                ForcedDummyBoneName = secondName,
                FrameLimit = frameLimit,
                FramePosition = framePosition,
                DummyBoneAnimationFilter = animFilter,
                DummySpecificAnimations = anims
            };
        }
        Attachments = attachments;

        // Mesh groups
        var meshGroups = new TmmMeshGroup[numMeshGroups];
        for (int i = 0; i < numMeshGroups; i++)
        {
            if (offset + 24 > data.Length) return false;
            meshGroups[i] = new TmmMeshGroup
            {
                VertexStart = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)),
                IndexStart = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 4, 4)),
                VertexCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 8, 4)),
                IndexCount = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 12, 4)),
                MaterialIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 16, 4)),
                SubmodelMask = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 20, 4))
            };
            offset += 24;
        }
        MeshGroups = meshGroups;

        // Materials
        var materials = new string[numMaterials];
        for (int i = 0; i < numMaterials; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var matName)) return false;
            materials[i] = matName;
        }
        Materials = materials;

        // Shader techniques
        var submodels = new string[numSubmodels];
        for (int i = 0; i < numSubmodels; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var submodelName)) return false;
            submodels[i] = submodelName;
        }
        Submodels = submodels;

        // Bones
        var bones = new TmmBone[numBones];
        for (int i = 0; i < numBones; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var boneName)) return false;
            if (!TryReadInt32(data, ref offset, out var parentId)) return false;

            // Collision offset (3 floats) + radius (1 float)
            if (offset + 16 > data.Length) return false;
            float colX = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)); offset += 4;
            float colY = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)); offset += 4;
            float colZ = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)); offset += 4;
            float radius = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)); offset += 4;

            // Three 4x4 matrices (16 floats each)
            if (offset + 192 > data.Length) return false;
            var parentSpaceMatrix = ReadFloats(data, ref offset, 16);
            var worldSpaceMatrix = ReadFloats(data, ref offset, 16);
            var inverseBindMatrix = ReadFloats(data, ref offset, 16);

            bones[i] = new TmmBone
            {
                Name = boneName,
                ParentId = parentId,
                CollisionOffsetX = colX,
                CollisionOffsetY = colY,
                CollisionOffsetZ = colZ,
                Radius = radius,
                ParentSpaceMatrix = parentSpaceMatrix,
                WorldSpaceMatrix = worldSpaceMatrix,
                InverseBindMatrix = inverseBindMatrix
            };
        }
        Bones = bones;
        try
        {
            FullyParsed = ParseTrailingSections(data, ref offset);
        }
        catch
        {
            FullyParsed = false;
        }
        return true;
    }

    /// <summary>
    /// Generates a human-readable summary of the parsed TMM file.
    /// </summary>
    /// <param name="tmmFilePath">
    /// Optional path of the .tmm file (relative or absolute). When provided, the inferred
    /// .material file path is shown alongside each material name.
    /// </param>
    public string GetSummary(string? tmmFilePath = null)
    {
        if (!Parsed) return "(TMM not parsed)";
        // All arrays are guaranteed non-null after successful Parse() via [MemberNotNullWhen]
        var importNames = ImportNames!;
        var meshGroups = MeshGroups!;
        var materials = Materials!;
        var submodels = Submodels!;
        var bones = Bones!;
        var attachments = Attachments!;

        // Infer the .material file path from the TMM path (same path + ".material")
        string? materialFilePath = tmmFilePath != null ? tmmFilePath + ".material" : null;

        var sb = new StringBuilder();
        sb.AppendLine($"TMM Model (Version {Version})");

        if (importNames.Length > 0)
            sb.AppendLine($"Import metadata: {string.Join(", ", importNames)}");

        sb.AppendLine($"Bounding Box: {BoundingBox}");
        sb.AppendLine($"Extended Bounding Box: {ExtendedBoundingBox}");
        sb.AppendLine($"Radius: {BoundsRadius:F2}");
        sb.AppendLine();

        // Mesh groups
        sb.AppendLine($"Mesh Groups ({meshGroups.Length}):");
        for (int i = 0; i < meshGroups.Length; i++)
        {
            var mg = meshGroups[i];
            var matName = mg.MaterialIndex < materials.Length ? materials[mg.MaterialIndex] : $"#{mg.MaterialIndex}";
            var submodelName = mg.SubmodelMask < submodels.Length ? submodels[mg.SubmodelMask] : $"mask=0x{mg.SubmodelMask:X}";
            sb.AppendLine($"  [{i}] {mg.VertexCount} verts, {mg.TriangleCount} tris, material: \"{matName}\", submodel: \"{submodelName}\"");
        }
        sb.AppendLine();

        // Materials - relative paths without extension - to a .material XML file
        sb.AppendLine($"Materials ({materials.Length}):");
        foreach (var mat in materials)
            sb.AppendLine($"  {mat}.material");
        if (materialFilePath != null)
            sb.AppendLine($"  (Material file: {materialFilePath})");
        sb.AppendLine();
        sb.AppendLine($"Submodels: {string.Join(", ", submodels)}");
        sb.AppendLine();

        // Bones
        sb.AppendLine($"Bones ({bones.Length}):");
        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            var parentName = bone.ParentId >= 0 && bone.ParentId < bones.Length
                ? bones[bone.ParentId].Name : "none";
            sb.AppendLine($"  [{i}] {bone.Name} (parent: {parentName})");
        }

        // Attachments
        if (attachments.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Attachments ({attachments.Length}):");
            for (int i = 0; i < attachments.Length; i++)
            {
                var att = attachments[i];
                var boneName = att.ParentBoneId >= 0 && att.ParentBoneId < bones.Length
                    ? bones[att.ParentBoneId].Name : "none";
                sb.AppendLine($"  {att.Name} -> bone: {boneName}");
            }
        }

        // Modified Bones
        if (ModifiedBones is { Length: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine($"Modified Bones ({ModifiedBones.Length}):");
            foreach (var mb in ModifiedBones)
                sb.AppendLine($"  bone[{mb.BoneIndex}] radius *= {mb.RadiusMultiplier:F2}");
        }

        // Destruction
        if (Destruction != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Destruction: {Destruction.ChunkBones.Length} chunk bones, {Destruction.ProxyGroups.Length} proxy groups, {Destruction.Intervals.Length} intervals");
        }

        // Physics
        if (PhysicsTemplate != null)
            sb.AppendLine($"Physics: shape={PhysicsTemplate.ShapeType}, {PhysicsTemplate.HullPoints.Length} hull points");

        // Tree Skeleton
        if (TreeDestructionBones is { Length: > 0 })
            sb.AppendLine($"Tree Destruction: {TreeDestructionBones.Length} bones");

        // Click Volume
        if (ClickVolume != null)
            sb.AppendLine($"Click Volume: type={ClickVolume.Type}, voxels={ClickVolume.AreVoxelsDefined}");

        // Auto Attach
        if (AutoAttachInfo != null)
            sb.AppendLine($"Auto Attach: corpse={AutoAttachInfo.AutoAttachCorpseToBone}, impacts={AutoAttachInfo.ManualImpactPoints?.Length ?? 0}");

        // Flags
        sb.AppendLine();
        sb.AppendLine($"Flags: terrain_emb={IsTerrainEmbellishment}, raytracing={EnableRayTracingForModel}, burn_mode={AutoBurnMode}");

        sb.AppendLine();
        sb.AppendLine($"Total: {NumVertices} vertices, {NumTriangleVerts / 3} triangles");

        return sb.ToString();
    }

    bool ParseTrailingSections(ReadOnlySpan<byte> data, ref int offset)
    {
        // 5.5 Modified Bones (only if bones > 0)
        if (NumBones > 0)
        {
            if (!TryReadUInt32(data, ref offset, out var modBoneCount)) return false;
            if (modBoneCount > MaxBones) return false;
            var modBones = new TmmModifiedBone[modBoneCount];
            for (int i = 0; i < modBoneCount; i++)
            {
                if (!TryReadInt32(data, ref offset, out var boneIdx)) return false;
                if (!TryReadFloat(data, ref offset, out var origRadius)) return false;
                if (!TryReadFloat(data, ref offset, out var radiusMult)) return false;
                modBones[i] = new TmmModifiedBone
                {
                    BoneIndex = boneIdx,
                    OriginalRadius = origRadius,
                    RadiusMultiplier = radiusMult
                };
            }
            ModifiedBones = modBones;
        }

        // 5.6 Auto Burn Mode (1 byte, not a bool — read raw)
        if (offset >= data.Length) return false;
        AutoBurnMode = data[offset++];

        // 5.7 Destruction
        if (!TryReadBool(data, ref offset, out var hasDestruction)) return false;
        if (hasDestruction)
        {
            if (!ParseDestruction(data, ref offset)) return false;
        }

        return ParsePhysicsAndBeyond(data, ref offset);
    }

    bool ParseDestruction(ReadOnlySpan<byte> data, ref int offset)
    {
        if (!TryReadUInt32(data, ref offset, out var errorFlags)) return false;

        // Chunk bones
        if (!TryReadUInt32(data, ref offset, out var chunkBoneCount)) return false;
        if (chunkBoneCount > MaxBones) return false;
        var chunkBones = new TmmDestructionBone[chunkBoneCount];
        for (int i = 0; i < chunkBoneCount; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var boneName)) return false;
            if (!TryReadMatrix4x4(data, ref offset, out var bindPose)) return false;
            if (!TryReadMatrix4x4(data, ref offset, out var invBindPose)) return false;
            chunkBones[i] = new TmmDestructionBone { Name = boneName, BindPose = bindPose, InverseBindPose = invBindPose };
        }

        // Global destruction settings
        if (!TryReadBool(data, ref offset, out var hasBase)) return false;
        if (!TryReadUInt32(data, ref offset, out var baseChunkIndex)) return false;
        if (!TryReadBool(data, ref offset, out var enableProxyGroupShapes)) return false;
        if (!TryReadInt32(data, ref offset, out var jitterCountOnDeath)) return false;
        if (!TryReadFloat(data, ref offset, out var jitterIntensityOnDeath)) return false;
        if (!TryReadFloat(data, ref offset, out var motionStopHideDelayOnDeath)) return false;
        if (!TryReadFloat(data, ref offset, out var motionStopHideTimeOnDeath)) return false;
        if (!TryReadFloat(data, ref offset, out var motionStopHideDelayRandomExtOnDeath)) return false;
        if (!TryReadFloat(data, ref offset, out var forceMultiplierOnDeath)) return false;
        if (!TryReadInt32(data, ref offset, out var physicsTypeOnDeath)) return false;
        if (!TryReadBool(data, ref offset, out var allowDecalsOnDeath)) return false;
        if (!TryReadBool(data, ref offset, out var allowPopcornOnDeath)) return false;
        if (!TryReadFloat(data, ref offset, out var percentToLeave)) return false;

        // Custom debris
        if (!TryReadBool(data, ref offset, out var hasCustomDebris)) return false;
        string? customDebrisPath = null;
        if (hasCustomDebris)
        {
            if (!TryReadUTF16String(data, ref offset, out var debrisStr)) return false;
            customDebrisPath = debrisStr;
        }
        if (!TryReadUInt32(data, ref offset, out var debrisType)) return false;
        if (!TryReadUInt32(data, ref offset, out var debrisCivType)) return false;
        if (!TryReadUInt32(data, ref offset, out var defaultFullDestroyMode)) return false;

        // Proxy groups
        if (!TryReadUInt32(data, ref offset, out var proxyGroupCount)) return false;
        if (proxyGroupCount > MaxBones) return false;
        var proxyGroups = new TmmProxyGroup[proxyGroupCount];
        for (int i = 0; i < proxyGroupCount; i++)
        {
            if (!TryReadVector4(data, ref offset, out var center)) return false;
            if (!TryReadVector4(data, ref offset, out var impactPoint)) return false;
            if (!TryReadUInt32(data, ref offset, out var firstChunkBone)) return false;
            if (!TryReadUInt32(data, ref offset, out var chunkCount)) return false;
            if (!TryReadInt32(data, ref offset, out var jitterCount)) return false;
            if (!TryReadFloat(data, ref offset, out var jitterIntensity)) return false;
            if (!TryReadFloat(data, ref offset, out var hideDelay)) return false;
            if (!TryReadFloat(data, ref offset, out var hideTime)) return false;
            if (!TryReadFloat(data, ref offset, out var hideDelayRandomExt)) return false;
            if (!TryReadFloat(data, ref offset, out var forceMult)) return false;
            if (!TryReadVector4(data, ref offset, out var forceDirDefault)) return false;
            if (!TryReadVector4(data, ref offset, out var forceDirReal)) return false;
            if (!TryReadVector4(data, ref offset, out var boundsMin)) return false;
            if (!TryReadVector4(data, ref offset, out var boundsMax)) return false;
            if (!TryReadVector4(data, ref offset, out var boundsCenter)) return false;
            if (!TryReadVector4(data, ref offset, out var boundsSize)) return false;
            if (!TryReadInt32(data, ref offset, out var physType)) return false;
            if (!TryReadBool(data, ref offset, out var allowDecals)) return false;
            if (!TryReadBool(data, ref offset, out var allowPopcorn)) return false;
            if (!TryReadUInt32(data, ref offset, out var orderCount)) return false;
            if (orderCount > MaxBones) return false;
            var order = new int[orderCount];
            for (int j = 0; j < orderCount; j++)
            {
                if (!TryReadInt32(data, ref offset, out order[j])) return false;
            }
            if (!TryReadInt32(data, ref offset, out var flags)) return false;

            proxyGroups[i] = new TmmProxyGroup
            {
                ProxyCenter = center, ImpactPoint = impactPoint,
                FirstChunkBoneIndex = firstChunkBone, ChunkCount = chunkCount,
                JitterCount = jitterCount, JitterIntensity = jitterIntensity,
                MotionStopHideDelay = hideDelay, MotionStopHideTime = hideTime,
                MotionStopHideDelayRandomExtension = hideDelayRandomExt,
                ForceMultiplier = forceMult,
                ForceDirectionDefault = forceDirDefault, ForceDirectionReal = forceDirReal,
                BoundsMin = boundsMin, BoundsMax = boundsMax,
                BoundsCenter = boundsCenter, BoundsSize = boundsSize,
                PhysicsType = physType, AllowDecalsVFX = allowDecals, AllowPopcornVFX = allowPopcorn,
                ProxyGroupOrder = order, Flags = flags
            };
        }

        // Intervals
        if (!TryReadUInt32(data, ref offset, out var intervalCount)) return false;
        if (intervalCount > MaxBones) return false;
        var intervals = new TmmDestructionInterval[intervalCount];
        for (int i = 0; i < intervalCount; i++)
        {
            if (!TryReadFloat(data, ref offset, out var threshold)) return false;
            if (!TryReadUInt32(data, ref offset, out var groupCount)) return false;
            if (groupCount > MaxBones) return false;
            var groups = new int[groupCount];
            for (int j = 0; j < groupCount; j++)
            {
                if (!TryReadInt32(data, ref offset, out groups[j])) return false;
            }
            intervals[i] = new TmmDestructionInterval { EventThreshold = threshold, ProxyGroupIndices = groups };
        }

        Destruction = new TmmDestruction
        {
            ErrorFlags = errorFlags, ChunkBones = chunkBones,
            HasBase = hasBase, BaseChunkIndex = baseChunkIndex,
            EnableProxyGroupShapes = enableProxyGroupShapes,
            JitterCountOnDeath = jitterCountOnDeath, JitterIntensityOnDeath = jitterIntensityOnDeath,
            MotionStopHideDelayOnDeath = motionStopHideDelayOnDeath,
            MotionStopHideTimeOnDeath = motionStopHideTimeOnDeath,
            MotionStopHideDelayRandomExtensionOnDeath = motionStopHideDelayRandomExtOnDeath,
            ForceMultiplierOnDeath = forceMultiplierOnDeath,
            PhysicsTypeOnDeath = physicsTypeOnDeath,
            AllowDecalsVFXOnDeath = allowDecalsOnDeath, AllowPopcornVFXOnDeath = allowPopcornOnDeath,
            PercentToLeave = percentToLeave, CustomDebrisPath = customDebrisPath,
            DebrisType = debrisType, DebrisCivType = debrisCivType,
            DefaultFullDestroyMode = defaultFullDestroyMode,
            ProxyGroups = proxyGroups, Intervals = intervals
        };
        return true;
    }

    bool ParsePhysicsAndBeyond(ReadOnlySpan<byte> data, ref int offset)
    {
        // 5.8 Physics Template
        if (!TryReadBool(data, ref offset, out var hasPhysics)) return false;
        if (hasPhysics)
        {
            if (!TryReadInt32(data, ref offset, out var shapeType)) return false;
            if (!TryReadInt32(data, ref offset, out var maxVerts)) return false;
            if (!TryReadInt32(data, ref offset, out var effectType)) return false;
            if (!TryReadBool(data, ref offset, out var isFixedMotion)) return false;
            if (!TryReadBool(data, ref offset, out var isPhysicsControlled)) return false;
            if (!TryReadFloat(data, ref offset, out var restitution)) return false;
            if (!TryReadFloat(data, ref offset, out var density)) return false;
            if (!TryReadFloat(data, ref offset, out var penetrationDepth)) return false;
            if (!TryReadUInt32(data, ref offset, out var hullPointCount)) return false;
            var hullPoints = new float[hullPointCount][];
            for (int i = 0; i < hullPointCount; i++)
            {
                if (!TryReadVector4(data, ref offset, out hullPoints[i])) return false;
            }
            PhysicsTemplate = new TmmPhysicsTemplate
            {
                ShapeType = shapeType, MaxVertices = maxVerts, EffectType = effectType,
                IsFixedMotion = isFixedMotion, IsPhysicsControlled = isPhysicsControlled,
                Restitution = restitution, Density = density, PenetrationDepth = penetrationDepth,
                HullPoints = hullPoints
            };
        }

        // 5.9 Tree Destruction Skeleton
        if (!TryReadBool(data, ref offset, out var hasTreeSkeleton)) return false;
        if (hasTreeSkeleton)
        {
            if (!TryReadUInt32(data, ref offset, out var treeBoneCount)) return false;
            var treeBones = new TmmTreeBone[treeBoneCount];
            for (int i = 0; i < treeBoneCount; i++)
            {
                if (!TryReadUTF16String(data, ref offset, out var name)) return false;
                if (!TryReadMatrix4x4(data, ref offset, out var bind)) return false;
                if (!TryReadMatrix4x4(data, ref offset, out var invBind)) return false;
                treeBones[i] = new TmmTreeBone { Name = name, BindPose = bind, InverseBindPose = invBind };
            }
            TreeDestructionBones = treeBones;
        }

        // 5.10 Click Volume
        if (offset >= data.Length) return false;
        var clickVolumeType = data[offset++];

        // VX marker (0x56, 0x58)
        if (offset + 2 > data.Length) return false;
        offset += 2; // skip VX marker

        if (!TryReadBool(data, ref offset, out var areVoxelsDefined)) return false;
        if (areVoxelsDefined)
        {
            // VS marker (0x56, 0x53)
            if (offset + 2 > data.Length) return false;
            offset += 2; // skip VS marker

            if (!TryReadInt32(data, ref offset, out _)) return false;
            if (!TryReadUInt32(data, ref offset, out var voxelVersion)) return false;
            if (!TryReadVector4(data, ref offset, out var voxBoundsMin)) return false;
            if (!TryReadVector4(data, ref offset, out var voxBoundsMax)) return false;
            if (!TryReadInt32(data, ref offset, out var voxDimensions)) return false;
            if (!TryReadFloat(data, ref offset, out var voxSizeLargestAxis)) return false;
            if (!TryReadUInt32(data, ref offset, out var voxByteCount)) return false;
            if (offset + voxByteCount > data.Length) return false;
            var voxelData = data.Slice(offset, (int)voxByteCount).ToArray();
            offset += (int)voxByteCount;

            ClickVolume = new TmmClickVolume
            {
                Type = clickVolumeType, AreVoxelsDefined = true,
                Version = voxelVersion, BoundsMin = voxBoundsMin, BoundsMax = voxBoundsMax,
                VoxelDimensions = voxDimensions, VoxelSizeLargestAxis = voxSizeLargestAxis,
                VoxelData = voxelData
            };
        }
        else
        {
            ClickVolume = new TmmClickVolume { Type = clickVolumeType, AreVoxelsDefined = false };
        }

        // 5.11 Auto Attach Properties (version-gated)
        if (Version >= 36)
        {
            if (!TryReadBool(data, ref offset, out var autoAttachCorpse)) return false;
            if (!TryReadUTF16String(data, ref offset, out var corpseBoneName)) return false;
            if (!TryReadUTF16String(data, ref offset, out var deathAnim)) return false;
            if (!TryReadBool(data, ref offset, out var usesAutoImpact)) return false;
            if (!TryReadUTF16String(data, ref offset, out var idleAnimPath)) return false;

            float[][]? manualImpactPoints = null;
            if (Version >= 37)
            {
                if (!TryReadUInt32(data, ref offset, out var impactCount)) return false;
                manualImpactPoints = new float[impactCount][];
                for (int i = 0; i < impactCount; i++)
                {
                    if (!TryReadVector4(data, ref offset, out manualImpactPoints[i])) return false;
                }
            }

            AutoAttachInfo = new TmmAutoAttachInfo
            {
                AutoAttachCorpseToBone = autoAttachCorpse,
                CorpseAttachBoneName = corpseBoneName,
                DefaultDeathAnimation = deathAnim,
                UsesAutoGeneratedImpactPoints = usesAutoImpact,
                DefaultIdleAnimationPath = idleAnimPath,
                ManualImpactPoints = manualImpactPoints
            };
        }

        return true;
    }

    #region Read Helpers

    static bool TryReadInt32(ReadOnlySpan<byte> data, ref int offset, out int value)
        => TmmReadHelpers.TryReadInt32(data, ref offset, out value);

    static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
        => TmmReadHelpers.TryReadUInt32(data, ref offset, out value);

    static bool TryReadFloat(ReadOnlySpan<byte> data, ref int offset, out float value)
        => TmmReadHelpers.TryReadFloat(data, ref offset, out value);

    static bool TryReadBool(ReadOnlySpan<byte> data, ref int offset, out bool value)
        => TmmReadHelpers.TryReadBool(data, ref offset, out value);

    static bool TryReadUTF16String(ReadOnlySpan<byte> data, ref int offset, out string value)
        => TmmReadHelpers.TryReadUTF16String(data, ref offset, out value, MaxNameLength);

    static float[] ReadFloats(ReadOnlySpan<byte> data, ref int offset, int count)
        => TmmReadHelpers.ReadFloats(data, ref offset, count);

    static bool TryReadVector4(ReadOnlySpan<byte> data, ref int offset, out float[] value)
        => TmmReadHelpers.TryReadVector4(data, ref offset, out value);

    static bool TryReadMatrix4x4(ReadOnlySpan<byte> data, ref int offset, out float[] value)
        => TmmReadHelpers.TryReadMatrix4x4(data, ref offset, out value);

    static TmmBoundingBox ReadBoundingBox(ReadOnlySpan<byte> data, ref int offset)
    {
        var bb = new TmmBoundingBox
        {
            MinX = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4)),
            MinY = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 4, 4)),
            MinZ = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 8, 4)),
            MaxX = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 12, 4)),
            MaxY = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 16, 4)),
            MaxZ = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset + 20, 4))
        };
        offset += 24;
        return bb;
    }

    #endregion
}
