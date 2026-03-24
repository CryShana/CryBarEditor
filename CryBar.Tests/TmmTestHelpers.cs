using System.Text;

using CryBar.TMM;

namespace CryBar.Tests;

internal static class TmmTestHelpers
{
    internal static byte[] CreateSyntheticTmm(
        uint version = 35,
        string[]? importNames = null,
        uint numMeshGroups = 0,
        string[]? materials = null,
        string[]? submodels = null,
        uint numBones = 0,
        uint numAttachments = 0,
        uint numVertices = 0,
        uint numTriangleVerts = 0,
        uint verticesStart = 0, uint verticesByteLength = 0,
        uint trianglesStart = 0, uint trianglesByteLength = 0,
        uint weightsStart = 0, uint weightsByteLength = 0,
        uint destructionStart = 0, uint destructionByteLength = 0,
        uint colorStart = 0, uint colorByteLength = 0,
        uint heightsStart = 0, uint heightsByteLength = 0,
        uint speedTreeStart = 0, uint speedTreeByteLength = 0)
    {
        importNames ??= [];
        materials ??= [];
        submodels ??= [];

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        WriteHeader(w, version);
        WriteImportMetadata(w, importNames);
        WriteBoundingBoxes(w);
        w.Write(3.0f); // bounds radius

        // Section counts
        w.Write(numMeshGroups);
        w.Write((uint)materials.Length);
        w.Write((uint)submodels.Length);
        w.Write(numBones);
        w.Write(0u); // SharedAnimationBucketCount
        w.Write(numAttachments);
        w.Write(numVertices);
        w.Write(numTriangleVerts);

        // Data block layout (7 pairs = 14 uint32s)
        w.Write(verticesStart); w.Write(verticesByteLength);
        w.Write(trianglesStart); w.Write(trianglesByteLength);
        w.Write(weightsStart); w.Write(weightsByteLength);
        w.Write(destructionStart); w.Write(destructionByteLength);
        w.Write(colorStart); w.Write(colorByteLength);
        w.Write(heightsStart); w.Write(heightsByteLength);
        w.Write(speedTreeStart); w.Write(speedTreeByteLength);

        // Flags
        w.Write((byte)0); // IsTerrainEmbellishment
        w.Write((byte)1); // EnableRayTracing

        // Main matrix (identity 4x3)
        w.Write(1.0f); w.Write(0.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.0f); w.Write(1.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.0f); w.Write(0.0f); w.Write(1.0f); w.Write(0.0f);

        // Attachments
        for (int i = 0; i < numAttachments; i++)
        {
            w.Write(0u); // TypeFlag
            w.Write(i < numBones ? i : -1); // ParentBoneId
            WriteUTF16String(w, $"attach_{i}");
            for (int j = 0; j < 24; j++) w.Write(0.0f); // two 4x3 matrices
            w.Write(0u); // DummyBoneMode
            w.Write(0u); // DummyBoneTransformMode
            WriteUTF16String(w, ""); // ForcedDummyBoneName
            w.Write(-1); // FrameLimit
            w.Write(0.0f); // FramePosition
            w.Write(0u); // DummyBoneAnimationFilter
            w.Write(0u); // DummySpecificAnimationsCount
        }

        // Mesh groups
        uint vertexOffset = 0;
        uint indexOffset = 0;
        uint vertsPerGroup = numMeshGroups > 0 ? numVertices / numMeshGroups : 0;
        uint indicesPerGroup = numMeshGroups > 0 ? numTriangleVerts / numMeshGroups : 0;
        for (uint i = 0; i < numMeshGroups; i++)
        {
            uint vc = (i == numMeshGroups - 1) ? numVertices - vertexOffset : vertsPerGroup;
            uint ic = (i == numMeshGroups - 1) ? numTriangleVerts - indexOffset : indicesPerGroup;
            w.Write(vertexOffset);
            w.Write(indexOffset);
            w.Write(vc);
            w.Write(ic);
            w.Write((uint)(i < materials.Length ? i : 0));
            w.Write(0u);
            vertexOffset += vc;
            indexOffset += ic;
        }

        // Materials
        foreach (var mat in materials)
            WriteUTF16String(w, mat);

        // Submodels
        foreach (var sub in submodels)
            WriteUTF16String(w, sub);

        // Bones
        for (int i = 0; i < numBones; i++)
        {
            WriteUTF16String(w, $"bone_{i}");
            w.Write(i == 0 ? -1 : i - 1); // parent id
            w.Write(0.0f); w.Write(0.0f); w.Write(0.0f); // collision offset
            w.Write(0.5f); // radius
            for (int m = 0; m < 3; m++)
            {
                w.Write(1.0f); w.Write(0.0f); w.Write(0.0f); w.Write(0.0f);
                w.Write(0.0f); w.Write(1.0f); w.Write(0.0f); w.Write(0.0f);
                w.Write(0.0f); w.Write(0.0f); w.Write(1.0f); w.Write(0.0f);
                w.Write(0.0f); w.Write(0.0f); w.Write(0.0f); w.Write(1.0f);
            }
        }

        return ms.ToArray();
    }

    internal static TmmFile CreateSyntheticTmmFile(
        uint numVertices, uint numTriangleVerts, bool hasSkinning,
        uint numMeshGroups = 0, string[]? materials = null, string[]? submodels = null,
        uint numBones = 0, uint numAttachments = 0,
        bool includeHeights = false)
    {
        if (hasSkinning && numBones == 0) numBones = 1;
        uint vertStart = 0;
        uint vertByteLen = numVertices * (uint)TmmVertex.SizeInBytes;
        uint triStart = vertStart + vertByteLen;
        uint triByteLen = numTriangleVerts * 2;
        uint weightStart = triStart + triByteLen;
        uint weightByteLen = hasSkinning ? numVertices * (uint)TmmSkinWeight.SizeInBytes : 0;
        uint heightStart = weightStart + weightByteLen;
        uint heightByteLen = includeHeights ? numVertices * 2 : 0;

        var tmmBytes = CreateSyntheticTmm(
            numMeshGroups: numMeshGroups, materials: materials, submodels: submodels,
            numBones: numBones, numAttachments: numAttachments,
            numVertices: numVertices, numTriangleVerts: numTriangleVerts,
            verticesStart: vertStart, verticesByteLength: vertByteLen,
            trianglesStart: triStart, trianglesByteLength: triByteLen,
            weightsStart: weightStart, weightsByteLength: weightByteLen,
            heightsStart: heightStart, heightsByteLength: heightByteLen);

        return new TmmFile(tmmBytes);
    }

    internal static byte[] CreateSyntheticData(
        uint numVertices, uint numTriangleVerts, bool hasSkinning,
        bool includeHeights = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        for (int i = 0; i < numVertices; i++)
        {
            w.Write((Half)1.5f);    // PosX
            w.Write((Half)2.5f);    // PosY
            w.Write((Half)(-0.5f)); // PosZ
            w.Write((Half)0.5f);    // U
            w.Write((Half)0.25f);   // V
            w.Write((ushort)16384); // TbnX
            w.Write((ushort)16384); // TbnY
            w.Write((ushort)16384); // TbnZ
        }

        for (int i = 0; i < numTriangleVerts; i++)
            w.Write((ushort)(i % Math.Max(numVertices, 1)));

        if (hasSkinning)
        {
            for (int i = 0; i < numVertices; i++)
            {
                w.Write((byte)200); // weight0
                w.Write((byte)55);  // weight1
                w.Write((byte)0);   // weight2
                w.Write((byte)0);   // weight3
                w.Write((byte)0);   // boneIndex0
                w.Write((byte)1);   // boneIndex1
                w.Write((byte)0);   // boneIndex2
                w.Write((byte)0);   // boneIndex3
            }
        }

        if (includeHeights)
        {
            for (int i = 0; i < numVertices; i++)
                w.Write((Half)1.0f);
        }

        return ms.ToArray();
    }

    internal static void WriteHeader(BinaryWriter w, uint version)
    {
        w.Write((byte)0x42); w.Write((byte)0x54);
        w.Write((byte)0x4d); w.Write((byte)0x4d);
        w.Write(version);
        w.Write((byte)0x44); w.Write((byte)0x50);
    }

    internal static void WriteImportMetadata(BinaryWriter w, string[] names)
    {
        int blockSize = 4;
        foreach (var name in names)
            blockSize += 4 + name.Length * 2 + 16;
        w.Write(blockSize);
        w.Write(names.Length);
        foreach (var name in names)
        {
            w.Write(name.Length);
            w.Write(Encoding.Unicode.GetBytes(name));
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
        }
    }

    internal static void WriteBoundingBoxes(BinaryWriter w)
    {
        w.Write(-1.0f); w.Write(-2.0f); w.Write(-3.0f);
        w.Write(1.0f); w.Write(2.0f); w.Write(3.0f);
        w.Write(-5.0f); w.Write(-5.0f); w.Write(-5.0f);
        w.Write(5.0f); w.Write(5.0f); w.Write(5.0f);
    }

    internal static void WriteUTF16String(BinaryWriter w, string value)
    {
        w.Write(value.Length);
        if (value.Length > 0)
            w.Write(Encoding.Unicode.GetBytes(value));
    }
}
