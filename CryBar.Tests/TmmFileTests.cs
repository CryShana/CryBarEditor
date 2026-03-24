using System.Buffers.Binary;

using CryBar.TMM;

using static CryBar.Tests.TmmTestHelpers;

namespace CryBar.Tests;

public class TmmFileTests
{
    #region Parse Tests

    [Fact]
    public void Parse_EmptyData_ReturnsFalse()
    {
        var tmm = new TmmFile(ReadOnlyMemory<byte>.Empty);
        Assert.False(tmm.Parsed);
        Assert.False(tmm.Parsed);
    }

    [Fact]
    public void Parse_TooShort_ReturnsFalse()
    {
        var tmm = new TmmFile(new byte[10]);
        Assert.False(tmm.Parsed);
    }

    [Fact]
    public void Parse_InvalidSignature_ReturnsFalse()
    {
        var data = new byte[100];
        data[0] = 0xFF;
        var tmm = new TmmFile(data);
        Assert.False(tmm.Parsed);
    }

    [Fact]
    public void Parse_InvalidVersion_ReturnsFalse()
    {
        var data = CreateMinimalTmmHeader(version: 10);
        var tmm = new TmmFile(data);
        Assert.False(tmm.Parsed);
    }

    [Fact]
    public void Parse_MissingDP_ReturnsFalse()
    {
        var data = new byte[100];
        data[0] = 0x42; data[1] = 0x54; data[2] = 0x4d; data[3] = 0x4d;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 35);
        data[8] = 0x00; data[9] = 0x00; // Not DP
        var tmm = new TmmFile(data);
        Assert.False(tmm.Parsed);
    }

    [Fact]
    public void Parse_MinimalValidFile_Succeeds()
    {
        var data = CreateSyntheticTmm();
        var tmm = new TmmFile(data);
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
        Assert.True(tmm.Parsed);
        Assert.Equal(2, tmm.ImportNames.Length);
        Assert.Equal("armory_a_age2", tmm.ImportNames[0]);
        Assert.Equal("test_model", tmm.ImportNames[1]);
    }

    [Fact]
    public void Parse_BoundingBoxes()
    {
        var data = CreateSyntheticTmm();
        var tmm = new TmmFile(data);
        Assert.True(tmm.Parsed);

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
            submodels: ["default"],
            numVertices: 200,
            numTriangleVerts: 600);

        var tmm = new TmmFile(data);
        Assert.True(tmm.Parsed);
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
        Assert.True(tmm.Parsed);
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
        Assert.True(tmm.Parsed);
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
            Assert.True(tmm.Parsed, $"Version {v} should be accepted");
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
        Assert.False(tmm.Parsed);
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
        Assert.True(tmm.Parsed);

        var summary = tmm.GetSummary();
        Assert.Contains("TMM Model", summary);
        Assert.Contains("wall_stone", summary);
        Assert.Contains("bone_0", summary);
        Assert.Contains("attach_0", summary);
    }

    [Fact]
    public void Parse_AttachmentWithSpecificAnimations()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        WriteHeader(w, 35);
        WriteImportMetadata(w, []);
        WriteBoundingBoxes(w);
        w.Write(3.0f); // bounds radius

        w.Write(0u); // meshGroups
        w.Write(0u); // materials
        w.Write(0u); // submodels
        w.Write(1u); // bones
        w.Write(0u); // SharedAnimationBucketCount
        w.Write(1u); // attachments
        w.Write(0u); // vertices
        w.Write(0u); // triangleVerts

        for (int i = 0; i < 14; i++) w.Write(0u); // data block layout
        w.Write((byte)0); w.Write((byte)0); // 2 bool bytes

        // Main matrix (identity 4x3)
        w.Write(1.0f); w.Write(0.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.0f); w.Write(1.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.0f); w.Write(0.0f); w.Write(1.0f); w.Write(0.0f);

        // Attachment
        w.Write(0u); // TypeFlag
        w.Write(0); // ParentBoneId
        WriteUTF16String(w, "test_attach");
        for (int j = 0; j < 24; j++) w.Write(0.0f); // two 4x3 matrices
        w.Write(0u); // DummyBoneMode
        w.Write(4u); // DummyBoneTransformMode = AllFrames
        WriteUTF16String(w, ""); // ForcedDummyBoneName
        w.Write(10); // FrameLimit
        w.Write(0.5f); // FramePosition
        w.Write(3u); // DummyBoneAnimationFilter = OnlySpecificAnimations
        w.Write(2u); // DummySpecificAnimationsCount
        WriteUTF16String(w, "anims/walk.tma");
        WriteUTF16String(w, "anims/run.tma");

        // Mesh groups (0), Materials (0), Submodels (0)

        // Bone
        WriteUTF16String(w, "root");
        w.Write(-1);
        w.Write(0.0f); w.Write(0.0f); w.Write(0.0f);
        w.Write(0.5f);
        for (int m = 0; m < 3; m++)
        {
            w.Write(1.0f); w.Write(0.0f); w.Write(0.0f); w.Write(0.0f);
            w.Write(0.0f); w.Write(1.0f); w.Write(0.0f); w.Write(0.0f);
            w.Write(0.0f); w.Write(0.0f); w.Write(1.0f); w.Write(0.0f);
            w.Write(0.0f); w.Write(0.0f); w.Write(0.0f); w.Write(1.0f);
        }

        var tmm = new TmmFile(ms.ToArray());
        Assert.True(tmm.Parsed);
        Assert.Single(tmm.Attachments);

        var att = tmm.Attachments[0];
        Assert.Equal("test_attach", att.Name);
        Assert.Equal(4u, att.DummyBoneTransformMode);
        Assert.Equal(10, att.FrameLimit);
        Assert.Equal(0.5f, att.FramePosition);
        Assert.Equal(3u, att.DummyBoneAnimationFilter);
        Assert.Equal(2, att.DummySpecificAnimations.Length);
        Assert.Equal("anims/walk.tma", att.DummySpecificAnimations[0]);
        Assert.Equal("anims/run.tma", att.DummySpecificAnimations[1]);
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

    // CreateMinimalTmmHeader is unique to this test class (used for invalid-signature tests)
    // All other TMM builders are in TmmTestHelpers

    #endregion
}
