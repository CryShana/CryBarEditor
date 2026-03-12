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

    public bool Parsed { get; private set; }
    public uint Version { get; private set; }

    // Import metadata
    public string[]? ImportNames { get; private set; }

    // Bounding boxes
    public TmmBoundingBox BoundingBox { get; private set; }
    public TmmBoundingBox ExtendedBoundingBox { get; private set; }
    public float BoundsRadius { get; private set; }

    // Section counts
    public uint NumMeshGroups { get; private set; }
    public uint NumMaterials { get; private set; }
    public uint NumShaderTechniques { get; private set; }
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

    // Main matrix (4x3 stored as 12 floats, expanded to 4x4)
    public float[]? MainMatrix { get; private set; }

    // Parsed sections
    public TmmAttachment[]? Attachments { get; private set; }
    public TmmMeshGroup[]? MeshGroups { get; private set; }
    public string[]? Materials { get; private set; }
    public string[]? ShaderTechniques { get; private set; }
    public TmmBone[]? Bones { get; private set; }

    readonly ReadOnlyMemory<byte> _data;

    public TmmFile(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    [MemberNotNullWhen(true, nameof(ImportNames), nameof(MainMatrix),
        nameof(Attachments), nameof(MeshGroups), nameof(Materials),
        nameof(ShaderTechniques), nameof(Bones))]
    public bool Parse()
    {
        var data = _data.Span;
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

        if (!TryReadUInt32(data, ref offset, out var numShaderTechniques)) return false;
        NumShaderTechniques = numShaderTechniques;

        if (!TryReadUInt32(data, ref offset, out var numBones)) return false;
        if (numBones > MaxBones) return false;
        NumBones = numBones;

        if (!TryReadUInt32(data, ref offset, out var reserved)) return false;
        // reserved should be 0, but don't fail on unexpected values

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

        // 2 reserved blocks (offset + byte length each)
        offset += 16; // skip 4 uint32s
        if (offset > data.Length) return false;

        if (!TryReadUInt32(data, ref offset, out var hStart)) return false;
        if (!TryReadUInt32(data, ref offset, out var hBl)) return false;
        HeightsStart = hStart; HeightsByteLength = hBl;

        // 1 reserved block
        offset += 8;
        if (offset > data.Length) return false;

        // 2 unknown bytes
        offset += 2;
        if (offset > data.Length) return false;

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

            // Terminator quad (4 int32s)
            offset += 16;
            if (offset > data.Length) return false;

            attachments[i] = new TmmAttachment
            {
                TypeFlag = typeFlag,
                ParentBoneId = parentBoneId,
                Name = attachName,
                TransformMatrix1 = mat1,
                TransformMatrix2 = mat2,
                UnknownFlag1 = flag1,
                UnknownFlag2 = flag2,
                SecondName = secondName
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
                ShaderIndex = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset + 20, 4))
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
        var shaderTechniques = new string[numShaderTechniques];
        for (int i = 0; i < numShaderTechniques; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var techName)) return false;
            shaderTechniques[i] = techName;
        }
        ShaderTechniques = shaderTechniques;

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

        Parsed = true;
        return true;
    }

    /// <summary>
    /// Generates a human-readable summary of the parsed TMM file.
    /// </summary>
    public string GetSummary()
    {
        if (!Parsed) return "(TMM not parsed)";
        // All arrays are guaranteed non-null after successful Parse() via [MemberNotNullWhen]
        var importNames = ImportNames!;
        var meshGroups = MeshGroups!;
        var materials = Materials!;
        var shaderTechniques = ShaderTechniques!;
        var bones = Bones!;
        var attachments = Attachments!;

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
            var shaderName = mg.ShaderIndex < shaderTechniques.Length ? shaderTechniques[mg.ShaderIndex] : $"#{mg.ShaderIndex}";
            sb.AppendLine($"  [{i}] {mg.VertexCount} verts, {mg.TriangleCount} tris, material: \"{matName}\", shader: \"{shaderName}\"");
        }
        sb.AppendLine();

        // Materials
        sb.AppendLine($"Materials: {string.Join(", ", materials)}");
        sb.AppendLine($"Shader Techniques: {string.Join(", ", shaderTechniques)}");
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

        sb.AppendLine();
        sb.AppendLine($"Total: {NumVertices} vertices, {NumTriangleVerts / 3} triangles");

        return sb.ToString();
    }

    #region Read Helpers

    static bool TryReadInt32(ReadOnlySpan<byte> data, ref int offset, out int value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    static bool TryReadFloat(ReadOnlySpan<byte> data, ref int offset, out float value)
    {
        value = 0;
        if (offset + 4 > data.Length) return false;
        value = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
        offset += 4;
        return true;
    }

    static bool TryReadUTF16String(ReadOnlySpan<byte> data, ref int offset, out string value)
    {
        value = "";
        if (offset + 4 > data.Length) return false;
        var charCount = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
        offset += 4;
        if (charCount < 0 || charCount > MaxNameLength) return false;
        var byteLength = charCount * 2;
        if (offset + byteLength > data.Length) return false;
        value = Encoding.Unicode.GetString(data.Slice(offset, byteLength));
        offset += byteLength;
        return true;
    }

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

    static float[] ReadFloats(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        var result = new float[count];
        for (int i = 0; i < count; i++)
        {
            result[i] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(offset, 4));
            offset += 4;
        }
        return result;
    }

    #endregion
}
