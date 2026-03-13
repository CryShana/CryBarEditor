using System.Buffers.Binary;
using System.Text;

using CryBar.TMM;

namespace CryBar.Tests;

public class TmmFileTests
{
    #region Parse Tests

    [Fact]
    public void Parse_EmptyData_ReturnsFalse()
    {
        var tmm = new TmmFile(ReadOnlyMemory<byte>.Empty);
        Assert.False(tmm.Parse());
        Assert.False(tmm.Parsed);
    }

    [Fact]
    public void Parse_TooShort_ReturnsFalse()
    {
        var tmm = new TmmFile(new byte[10]);
        Assert.False(tmm.Parse());
    }

    [Fact]
    public void Parse_InvalidSignature_ReturnsFalse()
    {
        var data = new byte[100];
        data[0] = 0xFF;
        var tmm = new TmmFile(data);
        Assert.False(tmm.Parse());
    }

    [Fact]
    public void Parse_InvalidVersion_ReturnsFalse()
    {
        var data = CreateMinimalTmmHeader(version: 10);
        var tmm = new TmmFile(data);
        Assert.False(tmm.Parse());
    }

    [Fact]
    public void Parse_MissingDP_ReturnsFalse()
    {
        var data = new byte[100];
        data[0] = 0x42; data[1] = 0x54; data[2] = 0x4d; data[3] = 0x4d;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 35);
        data[8] = 0x00; data[9] = 0x00; // Not DP
        var tmm = new TmmFile(data);
        Assert.False(tmm.Parse());
    }

    [Fact]
    public void Parse_MinimalValidFile_Succeeds()
    {
        var data = CreateSyntheticTmm();
        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());
        Assert.True(tmm.Parsed);
        Assert.Equal(35u, tmm.Version);
        Assert.Empty(tmm.ImportNames);
        Assert.Empty(tmm.MeshGroups);
        Assert.Empty(tmm.Materials);
        Assert.Empty(tmm.Bones);
        Assert.Empty(tmm.Attachments);
    }

    [Fact]
    public void Parse_WithImportNames()
    {
        var data = CreateSyntheticTmm(importNames: ["armory_a_age2", "test_model"]);
        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());
        Assert.Equal(2, tmm.ImportNames.Length);
        Assert.Equal("armory_a_age2", tmm.ImportNames[0]);
        Assert.Equal("test_model", tmm.ImportNames[1]);
    }

    [Fact]
    public void Parse_BoundingBoxes()
    {
        var data = CreateSyntheticTmm();
        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());

        Assert.Equal(-1.0f, tmm.BoundingBox.MinX);
        Assert.Equal(-2.0f, tmm.BoundingBox.MinY);
        Assert.Equal(-3.0f, tmm.BoundingBox.MinZ);
        Assert.Equal(1.0f, tmm.BoundingBox.MaxX);
        Assert.Equal(2.0f, tmm.BoundingBox.MaxY);
        Assert.Equal(3.0f, tmm.BoundingBox.MaxZ);
    }

    [Fact]
    public void Parse_WithMeshGroups()
    {
        var data = CreateSyntheticTmm(
            numMeshGroups: 2,
            materials: ["stone", "wood"],
            submodels: ["default"]);

        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());
        Assert.Equal(2, tmm.MeshGroups.Length);
        Assert.Equal(100u, tmm.MeshGroups[0].VertexCount);
        Assert.Equal(2, tmm.Materials.Length);
        Assert.Equal("stone", tmm.Materials[0]);
        Assert.Equal("wood", tmm.Materials[1]);
    }

    [Fact]
    public void Parse_WithBones()
    {
        var data = CreateSyntheticTmm(numBones: 2);
        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());
        Assert.Equal(2, tmm.Bones.Length);
        Assert.Equal("bone_0", tmm.Bones[0].Name);
        Assert.Equal(-1, tmm.Bones[0].ParentId);
        Assert.Equal("bone_1", tmm.Bones[1].Name);
        Assert.Equal(0, tmm.Bones[1].ParentId);
    }

    [Fact]
    public void Parse_WithAttachments()
    {
        var data = CreateSyntheticTmm(numAttachments: 1, numBones: 1);
        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());
        Assert.Single(tmm.Attachments);
        Assert.Equal("attach_0", tmm.Attachments[0].Name);
        Assert.Equal(0, tmm.Attachments[0].ParentBoneId);
    }

    [Fact]
    public void Parse_VersionTolerance()
    {
        // Should accept versions 30-255
        foreach (uint v in new uint[] { 30, 34, 35, 100, 255 })
        {
            var data = CreateSyntheticTmm(version: v);
            var tmm = new TmmFile(data);
            Assert.True(tmm.Parse(), $"Version {v} should be accepted");
        }
    }

    [Fact]
    public void Parse_OversizedCounts_ReturnsFalse()
    {
        // Create a header with impossibly large mesh group count
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteHeader(w, 35);
        WriteImportMetadata(w, []);
        WriteBoundingBoxes(w);
        w.Write(3.0f); // bounds radius

        // Write absurdly large mesh group count
        w.Write(uint.MaxValue); // numMeshGroups
        w.Write(0u); // numMaterials
        w.Write(0u); // numSubmodels
        w.Write(0u); // numBones
        w.Write(0u); // reserved
        w.Write(0u); // numAttachments
        w.Write(0u); // numVertices
        w.Write(0u); // numTriangleVerts

        var tmm = new TmmFile(ms.ToArray());
        Assert.False(tmm.Parse());
    }

    [Fact]
    public void GetSummary_ReturnsNonEmpty()
    {
        var data = CreateSyntheticTmm(
            numMeshGroups: 1,
            materials: ["wall_stone"],
            submodels: ["default"],
            numBones: 2,
            numAttachments: 1);

        var tmm = new TmmFile(data);
        Assert.True(tmm.Parse());

        var summary = tmm.GetSummary();
        Assert.Contains("TMM Model", summary);
        Assert.Contains("wall_stone", summary);
        Assert.Contains("bone_0", summary);
        Assert.Contains("attach_0", summary);
    }

    #endregion

    #region Helper Methods

    static byte[] CreateMinimalTmmHeader(uint version)
    {
        var data = new byte[100];
        data[0] = 0x42; data[1] = 0x54; data[2] = 0x4d; data[3] = 0x4d;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), version);
        data[8] = 0x44; data[9] = 0x50;
        return data;
    }

    static byte[] CreateSyntheticTmm(
        uint version = 35,
        string[]? importNames = null,
        uint numMeshGroups = 0,
        string[]? materials = null,
        string[]? submodels = null,
        uint numBones = 0,
        uint numAttachments = 0,
        uint numVertices = 0,
        uint numTriangleVerts = 0)
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
        w.Write(0u); // reserved
        w.Write(numAttachments);
        w.Write(numVertices);
        w.Write(numTriangleVerts);

        // Data block layout (all zeroed for synthetic)
        for (int i = 0; i < 14; i++) w.Write(0u); // 7 pairs of offset+length

        // 2 unknown bytes
        w.Write((byte)0); w.Write((byte)1);

        // Main matrix (identity 4x3)
        w.Write(1.0f); w.Write(0.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.0f); w.Write(1.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.0f); w.Write(0.0f); w.Write(1.0f); w.Write(0.0f);

        // Attachments
        for (int i = 0; i < numAttachments; i++)
        {
            w.Write(0u); // type flag
            w.Write(i < numBones ? i : -1); // parent bone id
            WriteUTF16String(w, $"attach_{i}");
            for (int j = 0; j < 24; j++) w.Write(0.0f); // two 4x3 matrices
            w.Write(0u); // flag1
            w.Write(0u); // flag2
            WriteUTF16String(w, ""); // second name
            w.Write(-1); w.Write(0); w.Write(0); w.Write(0); // terminator
        }

        // Mesh groups
        for (int i = 0; i < numMeshGroups; i++)
        {
            w.Write(0u); // vertex start
            w.Write(0u); // index start
            w.Write(100u); // vertex count
            w.Write(300u); // index count (100 triangles)
            w.Write((uint)(i < materials.Length ? i : 0)); // material index
            w.Write(0u); // shader index
        }

        // Materials
        foreach (var mat in materials)
            WriteUTF16String(w, mat);

        // Shader techniques
        foreach (var tech in submodels)
            WriteUTF16String(w, tech);

        // Bones
        for (int i = 0; i < numBones; i++)
        {
            WriteUTF16String(w, $"bone_{i}");
            w.Write(i == 0 ? -1 : i - 1); // parent id
            w.Write(0.0f); w.Write(0.0f); w.Write(0.0f); // collision offset
            w.Write(0.5f); // radius
            // Three 4x4 identity matrices
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

    static void WriteHeader(BinaryWriter w, uint version)
    {
        w.Write((byte)0x42); w.Write((byte)0x54);
        w.Write((byte)0x4d); w.Write((byte)0x4d);
        w.Write(version);
        w.Write((byte)0x44); w.Write((byte)0x50);
    }

    static void WriteImportMetadata(BinaryWriter w, string[] names)
    {
        // Calculate block byte length: 4 (count) + per name: 4 (len) + chars*2 + 16 (unknown)
        int blockSize = 4;
        foreach (var name in names)
            blockSize += 4 + name.Length * 2 + 16;
        w.Write(blockSize);
        w.Write(names.Length);
        foreach (var name in names)
        {
            w.Write(name.Length);
            w.Write(Encoding.Unicode.GetBytes(name));
            w.Write(0); w.Write(0); w.Write(0); w.Write(0); // 16 bytes unknown
        }
    }

    static void WriteBoundingBoxes(BinaryWriter w)
    {
        // Tight bounding box
        w.Write(-1.0f); w.Write(-2.0f); w.Write(-3.0f);
        w.Write(1.0f); w.Write(2.0f); w.Write(3.0f);
        // Extended bounding box
        w.Write(-5.0f); w.Write(-5.0f); w.Write(-5.0f);
        w.Write(5.0f); w.Write(5.0f); w.Write(5.0f);
    }

    static void WriteUTF16String(BinaryWriter w, string value)
    {
        w.Write(value.Length);
        if (value.Length > 0)
            w.Write(Encoding.Unicode.GetBytes(value));
    }

    #endregion
}
