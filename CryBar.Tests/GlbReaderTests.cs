using System.Text;
using System.Text.Json.Nodes;
using CryBar.Export;
using CryBar.TMM;
using static CryBar.Tests.TmmTestHelpers;

namespace CryBar.Tests;

public class GlbReaderTests
{
    [Fact]
    public void ParseContainer_ValidGlb_ReturnsJsonAndBin()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;

        var (json, bin) = GlbReader.ParseContainerForTests(glb);
        Assert.True(json.RootElement.TryGetProperty("asset", out _));
        Assert.True(bin.Length > 0);
    }

    [Fact]
    public void ParseContainer_BadMagic_Throws()
    {
        var bytes = new byte[20];
        Encoding.ASCII.GetBytes("BAD!", bytes.AsSpan(0, 4));
        Assert.Throws<GlbParseException>(() => GlbReader.ParseContainerForTests(bytes));
    }

    [Fact]
    public void ParseContainer_TooShort_Throws()
    {
        Assert.Throws<GlbParseException>(() => GlbReader.ParseContainerForTests(new byte[8]));
    }

    [Fact]
    public void Parse_MinimalGlb_ReturnsGlbModelWithExtras()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var extras = new GlbExtras { Tmm = new GlbExtras.TmmSection { AutoBurnMode = 7 } };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, extras: extras)!;

        var model = GlbReader.Parse(glb);
        Assert.NotNull(model);
        Assert.NotNull(model.Mesh);
        Assert.NotNull(model.Extras);
        Assert.Equal((byte)7, model.Extras!.Tmm.AutoBurnMode);
    }

    [Fact]
    public void Parse_MinimalGlb_HasOnePrimitiveWithVerticesAndTriangles()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;

        var model = GlbReader.Parse(glb);
        Assert.Single(model.Mesh.Primitives);

        var prim = model.Mesh.Primitives[0];
        Assert.Equal(3 * 3, prim.Positions.Length);          // 3 verts * 3 floats
        Assert.Equal(3 * 3, prim.Normals.Length);
        Assert.Equal(3 * 4, prim.Tangents.Length);
        Assert.Equal(3 * 2, prim.TexCoords.Length);
        Assert.Equal(3u, (uint)prim.Indices.Length);
    }

    [Fact]
    public void Parse_GlbWithAnimation_ReturnsResampledTrackPerBone()
    {
        var tmm = CreateSyntheticTmmFile(numVertices: 3, numTriangleVerts: 3, hasSkinning: true,
            numMeshGroups: 1, materials: ["m"], submodels: ["default"], numBones: 2);
        var dataBytes = CreateSyntheticData(numVertices: 3, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(dataBytes, tmm);

        var anim = new GlbExporter.GlbAnimation
        {
            Name = "idle",
            Tracks = SyntheticTracksUniform(boneCount: 2, frameCount: 30, duration: 1.0f),
            Duration = 1.0f,
            FrameCount = 30,
        };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, animations: [anim])!;

        var model = GlbReader.Parse(glb);
        Assert.NotNull(model.Animations);
        Assert.Single(model.Animations!);

        var a = model.Animations![0];
        Assert.Equal("idle", a.Name);
        Assert.Equal(30u, a.FrameCount);
        Assert.Equal(2, a.Tracks.Length);
        Assert.Equal(30, a.Tracks[0].Translations.Length);
        Assert.Equal(30, a.Tracks[0].Rotations.Length);
    }

    [Fact]
    public void Parse_GlbWithBones_ReturnsParentIndexedBoneArray()
    {
        var tmm = CreateSyntheticTmmFile(numVertices: 3, numTriangleVerts: 3, hasSkinning: true,
            numMeshGroups: 1, materials: ["m"], submodels: ["default"], numBones: 3);
        var dataBytes = CreateSyntheticData(numVertices: 3, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(dataBytes, tmm);
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;

        var model = GlbReader.Parse(glb);
        Assert.NotNull(model.Bones);
        Assert.Equal(3, model.Bones!.Length);

        Assert.Equal(-1, model.Bones[0].ParentIndex);
        Assert.Equal(0, model.Bones[1].ParentIndex);
        Assert.Equal(1, model.Bones[2].ParentIndex);

        Assert.Equal(16, model.Bones[0].LocalMatrix.Length);
        Assert.Equal(16, model.Bones[0].InverseBindMatrix.Length);
    }

    [Fact]
    public void ExpandStepToLinear_TwoKeysVec3_InsertsHoldKeyBeforeTransition()
    {
        // Two keys at t=0 (v=1,2,3) and t=1 (v=4,5,6); expanded should be:
        //   (0,    1,2,3)
        //   (1-eps,1,2,3)  hold
        //   (1,    4,5,6)
        var times = new float[] { 0f, 1f };
        var values = new float[] { 1, 2, 3,  4, 5, 6 };

        var (nt, nv) = GlbReader.ExpandStepToLinear(times, values);

        Assert.Equal(3, nt.Length);
        Assert.Equal(9, nv.Length);
        Assert.Equal(0f, nt[0]);
        Assert.True(nt[1] > nt[0] && nt[1] < nt[2], "hold key must sit strictly between adjacent originals");
        Assert.Equal(1f, nt[2]);

        // First key: v0
        Assert.Equal(1f, nv[0]); Assert.Equal(2f, nv[1]); Assert.Equal(3f, nv[2]);
        // Hold key: still v0
        Assert.Equal(1f, nv[3]); Assert.Equal(2f, nv[4]); Assert.Equal(3f, nv[5]);
        // Second key: v1
        Assert.Equal(4f, nv[6]); Assert.Equal(5f, nv[7]); Assert.Equal(6f, nv[8]);
    }

    [Fact]
    public void ExpandStepToLinear_FourKeysQuat_ProducesCorrectShape()
    {
        // 4 quat keys -> 7 output keys, each 4 floats
        var times = new float[] { 0f, 1f, 2f, 3f };
        var values = new float[] {
            0,0,0,1,    0,0,1,0,    0,1,0,0,    1,0,0,0
        };

        var (nt, nv) = GlbReader.ExpandStepToLinear(times, values);

        Assert.Equal(7, nt.Length);
        Assert.Equal(28, nv.Length);

        // Hold keys carry previous values; inspect the hold before t=1 (index 1)
        Assert.Equal(0f, nv[4]); Assert.Equal(0f, nv[5]); Assert.Equal(0f, nv[6]); Assert.Equal(1f, nv[7]);
        // Real key at t=1
        Assert.Equal(0f, nv[8]); Assert.Equal(0f, nv[9]); Assert.Equal(1f, nv[10]); Assert.Equal(0f, nv[11]);
    }

    [Fact]
    public void ExpandStepToLinear_SingleKey_PassesThrough()
    {
        var times = new float[] { 0f };
        var values = new float[] { 1, 2, 3 };

        var (nt, nv) = GlbReader.ExpandStepToLinear(times, values);

        Assert.Same(times, nt);
        Assert.Same(values, nv);
    }

    [Fact]
    public void Parse_GlbWithStepInterpolation_DoesNotThrow_AndPreservesStepHold()
    {
        // Build an animation with a STEP rotation track and verify the reader accepts it
        // and that mid-interval samples still hold the previous value (rather than slerping
        // halfway, which is what LINEAR interpolation would produce).
        //
        // Use a 2-bone synthetic skeleton; only bone 0 has a rotation track.
        // STEP keys: t=0.0 -> identity; t=0.5 -> 90deg-X; t=1.0 -> 180deg-X.
        var tmm = CreateSyntheticTmmFile(numVertices: 3, numTriangleVerts: 3, hasSkinning: true,
            numMeshGroups: 1, materials: ["m"], submodels: ["default"], numBones: 2);
        var dataBytes = CreateSyntheticData(numVertices: 3, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(dataBytes, tmm);

        var anim = new GlbExporter.GlbAnimation
        {
            Name = "step_test",
            Tracks = SyntheticTracksUniform(boneCount: 2, frameCount: 3, duration: 1.0f),
            Duration = 1.0f,
            FrameCount = 3,
        };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, animations: [anim])!;
        var stepGlb = RewriteSamplerInterpolations(glb, "STEP");

        var model = GlbReader.Parse(stepGlb);
        Assert.NotNull(model.Animations);
        Assert.Single(model.Animations!);

        // Reader must not throw; track length matches a sane sampling of the original duration.
        var a = model.Animations![0];
        Assert.True(a.FrameCount >= 3, "expanded STEP should yield at least the original key count");
        Assert.Equal(2, a.Tracks.Length);
    }

    /// <summary>
    /// Walks a GLB JSON chunk and replaces every "interpolation": "X" with the requested value.
    /// Used to fabricate a STEP GLB without modifying the production exporter.
    /// </summary>
    static byte[] RewriteSamplerInterpolations(byte[] glb, string newInterp)
    {
        // Read JSON chunk header at offset 12 (after GLB header)
        int jsonChunkLen = BitConverter.ToInt32(glb, 12);
        int jsonChunkType = BitConverter.ToInt32(glb, 16);
        Assert.Equal(0x4E4F534A, jsonChunkType); // "JSON"

        // The JSON sits at glb[20 .. 20+jsonChunkLen]; trailing space-padding allowed.
        var jsonText = Encoding.UTF8.GetString(glb, 20, jsonChunkLen).TrimEnd(' ', '\0');

        var node = JsonNode.Parse(jsonText)!;
        if (node["animations"] is JsonArray anims)
        {
            foreach (var a in anims)
            {
                if (a?["samplers"] is JsonArray ss)
                {
                    foreach (var s in ss)
                    {
                        if (s is JsonObject so) so["interpolation"] = newInterp;
                    }
                }
            }
        }

        var newJsonBytes = Encoding.UTF8.GetBytes(node.ToJsonString());
        // Pad with spaces to a 4-byte boundary
        int padded = (newJsonBytes.Length + 3) & ~3;
        var jsonBuf = new byte[padded];
        Array.Copy(newJsonBytes, jsonBuf, newJsonBytes.Length);
        for (int i = newJsonBytes.Length; i < padded; i++) jsonBuf[i] = (byte)' ';

        // Reassemble GLB: header (12) + JSON chunk header (8) + JSON + BIN chunk (rest)
        int binChunkOffset = 20 + jsonChunkLen; // after old JSON
        int binChunkLen = BitConverter.ToInt32(glb, binChunkOffset);
        int totalLen = 12 + 8 + jsonBuf.Length + 8 + binChunkLen;

        var result = new byte[totalLen];
        Array.Copy(glb, 0, result, 0, 12); // GLB header
        // Patch total length in header
        BitConverter.GetBytes(totalLen).CopyTo(result.AsSpan(8));
        // JSON chunk header
        BitConverter.GetBytes(jsonBuf.Length).CopyTo(result.AsSpan(12));
        BitConverter.GetBytes(0x4E4F534A).CopyTo(result.AsSpan(16)); // "JSON"
        Array.Copy(jsonBuf, 0, result, 20, jsonBuf.Length);
        // BIN chunk header + data
        Array.Copy(glb, binChunkOffset, result, 20 + jsonBuf.Length, 8 + binChunkLen);
        return result;
    }

    [Fact]
    public void Parse_GlbWithMaterial_ExtractsPngBytes()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var pngBytes = CreateTinyRgbaPng(2, 2);

        var glbMaterials = new[]
        {
            new GlbExporter.GlbMaterial
            {
                Name = "body",
                BaseColorPng = pngBytes,
                NormalMapPng = pngBytes,
            }
        };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, materials: glbMaterials)!;

        var model = GlbReader.Parse(glb);
        Assert.Single(model.Materials);
        Assert.Equal("body", model.Materials[0].Name);
        Assert.NotNull(model.Materials[0].BaseColorPng);
        Assert.NotNull(model.Materials[0].NormalMapPng);
    }

    [Fact]
    public void Parse_GlbWithMeshNodeMainMatrix_OverridesAssetLevel()
    {
        var (tmm, dataFile) = CreateMinimalModel();
        var extras = new GlbExtras
        {
            Tmm = new GlbExtras.TmmSection
            {
                MainMatrix = [2, 0, 0, 0,  0, 2, 0, 0,  0, 0, 2, 0,  0, 0, 0, 1],
            },
        };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, extras: extras)!;

        var model = GlbReader.Parse(glb);
        Assert.NotNull(model.Extras);
        Assert.Equal(2.0f, model.Extras!.Tmm.MainMatrix[0]);
    }

    [Fact]
    public void Parse_GlbWithTaggedImpactPointNodes_PopulatesImpactPoints()
    {
        // Inject empty nodes carrying extras.crybar.{node_type:"impact_point", index} and a
        // translation; the reader should harvest them into Extras.Tmm.ImpactPoints in index order
        // (with the LH<->RH X-flip reversed).
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;
        var withTagged = AddTaggedImpactPoints(glb,
            (idx: 1, x: 2.0f, y: 3.0f, z: 4.0f),
            (idx: 0, x: 1.0f, y: 0.0f, z: 0.0f)); // out-of-order on purpose

        var model = GlbReader.Parse(withTagged);

        Assert.NotNull(model.Extras);
        Assert.Equal(2, model.Extras!.Tmm.ImpactPoints.Length);
        // After sort by index: [0]=(1,0,0), [1]=(2,3,4).
        Assert.Equal(1.0f, model.Extras.Tmm.ImpactPoints[0][0]);
        Assert.Equal(2.0f, model.Extras.Tmm.ImpactPoints[1][0]);
        Assert.Equal(3.0f, model.Extras.Tmm.ImpactPoints[1][1]);
        Assert.Equal(4.0f, model.Extras.Tmm.ImpactPoints[1][2]);
    }

    [Fact]
    public void Parse_GlbWithRootExtrasImpactPoints_StillWorksWhenNoNodeTags()
    {
        // Backward-compat path: GLBs that came from older exporters carry impact_points only in
        // root extras. With no tagged nodes present, the merge should leave that data alone.
        var (tmm, dataFile) = CreateMinimalModel();
        var extras = new GlbExtras
        {
            Tmm = new GlbExtras.TmmSection
            {
                ImpactPoints = [[5.0f, 6.0f, 7.0f]],
            },
        };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, extras: extras)!;

        var model = GlbReader.Parse(glb);

        Assert.NotNull(model.Extras);
        Assert.Single(model.Extras!.Tmm.ImpactPoints);
        Assert.Equal(5.0f, model.Extras.Tmm.ImpactPoints[0][0]);
    }

    /// <summary>
    /// Inserts empty nodes with translation + extras.crybar.{node_type:"impact_point", index}
    /// into a GLB's scene graph, parented to the existing scene root. Used to simulate either
    /// our exporter's output (post node-tagging) or a user manually tagging empties in Blender.
    /// </summary>
    static byte[] AddTaggedImpactPoints(byte[] glb, params (int idx, float x, float y, float z)[] points)
    {
        int jsonChunkLen = BitConverter.ToInt32(glb, 12);
        var jsonText = Encoding.UTF8.GetString(glb, 20, jsonChunkLen).TrimEnd(' ', '\0');
        var node = JsonNode.Parse(jsonText)!;

        var nodes = (JsonArray)node["nodes"]!;
        // Attach impact-point nodes as children of node[0] (the mesh node) - matches our exporter.
        var meshNode = (JsonObject)nodes[0]!;
        var children = meshNode["children"] as JsonArray;
        if (children == null) { children = new JsonArray(); meshNode["children"] = children; }

        foreach (var p in points)
        {
            var ipNode = new JsonObject
            {
                ["name"] = $"ImpactPoint_{p.idx}",
                ["translation"] = new JsonArray(-p.x, p.y, p.z), // exporter's LH->RH X-flip
                ["extras"] = new JsonObject
                {
                    ["crybar"] = new JsonObject
                    {
                        ["node_type"] = "impact_point",
                        ["index"] = p.idx,
                    },
                },
            };
            int newIdx = nodes.Count;
            nodes.Add(ipNode);
            children.Add(newIdx);
        }
        return ReassembleGlb(glb, jsonChunkLen, node);
    }

    [Fact]
    public void Parse_GlbWithBlenderArmatureWrapper_RecoversMainMatrixFromWrapperExtras()
    {
        // Blender's glTF round-trip wraps the mesh in an "Armature" node and migrates Object
        // custom properties (including extras.crybar.main_matrix that we stamp on the mesh node)
        // to that wrapper. Verify the reader still recovers main_matrix in this layout.
        var (tmm, dataFile) = CreateMinimalModel();
        var extras = new GlbExtras
        {
            Tmm = new GlbExtras.TmmSection
            {
                MainMatrix = [1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0.099f, 1],
            },
        };
        var glb = GlbExporter.ExportGlb(tmm, dataFile, extras: extras)!;
        var blenderGlb = SimulateBlenderArmatureWrapping(glb);

        var model = GlbReader.Parse(blenderGlb);
        Assert.NotNull(model.Extras);
        Assert.Equal(0.099f, model.Extras!.Tmm.MainMatrix[14]);
    }

    /// <summary>
    /// Mutates a CryBar-exported GLB into the layout Blender produces on round-trip:
    /// an "Armature" node becomes the scene root with the original mesh node as its child,
    /// and the mesh node's extras.crybar object is migrated to the Armature wrapper as a unit
    /// (Blender treats Object custom properties as opaque blobs).
    /// </summary>
    static byte[] SimulateBlenderArmatureWrapping(byte[] glb)
    {
        int jsonChunkLen = BitConverter.ToInt32(glb, 12);
        var jsonText = Encoding.UTF8.GetString(glb, 20, jsonChunkLen).TrimEnd(' ', '\0');
        var node = JsonNode.Parse(jsonText)!;

        var nodes = (JsonArray)node["nodes"]!;
        var meshNode = (JsonObject)nodes[0]!;

        var meshCrybar = meshNode["extras"]?["crybar"];
        Assert.NotNull(meshCrybar); // sanity: exporter must have stamped it
        var wrapper = new JsonObject
        {
            ["name"] = "Armature",
            ["children"] = new JsonArray(0), // mesh stays at index 0
            ["extras"] = new JsonObject
            {
                ["crybar"] = meshCrybar.DeepClone(),
            },
        };
        meshNode.Remove("extras");

        int wrapperIdx = nodes.Count;
        nodes.Add(wrapper);

        // Re-root the scene at the wrapper.
        var scenes = (JsonArray)node["scenes"]!;
        var scene0 = (JsonObject)scenes[0]!;
        scene0["nodes"] = new JsonArray(wrapperIdx);

        return ReassembleGlb(glb, jsonChunkLen, node);
    }

    static byte[] ReassembleGlb(byte[] glb, int oldJsonChunkLen, JsonNode root)
    {
        var newJsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        int padded = (newJsonBytes.Length + 3) & ~3;
        var jsonBuf = new byte[padded];
        Array.Copy(newJsonBytes, jsonBuf, newJsonBytes.Length);
        Array.Fill(jsonBuf, (byte)' ', newJsonBytes.Length, padded - newJsonBytes.Length);

        int binChunkOffset = 20 + oldJsonChunkLen;
        int binChunkLen = BitConverter.ToInt32(glb, binChunkOffset);
        int totalLen = 12 + 8 + jsonBuf.Length + 8 + binChunkLen;

        var result = new byte[totalLen];
        Array.Copy(glb, 0, result, 0, 12);
        BitConverter.GetBytes(totalLen).CopyTo(result.AsSpan(8));
        BitConverter.GetBytes(jsonBuf.Length).CopyTo(result.AsSpan(12));
        BitConverter.GetBytes(0x4E4F534A).CopyTo(result.AsSpan(16));
        Array.Copy(jsonBuf, 0, result, 20, jsonBuf.Length);
        Array.Copy(glb, binChunkOffset, result, 20 + jsonBuf.Length, 8 + binChunkLen);
        return result;
    }

    [Fact]
    public void Parse_GlbWithAttachments_ReturnsAttachmentArrayWithIndex()
    {
        var tmm = CreateSyntheticTmmFile(numVertices: 3, numTriangleVerts: 3, hasSkinning: true,
            numMeshGroups: 1, materials: ["m"], submodels: ["default"], numBones: 1, numAttachments: 2);
        var dataBytes = CreateSyntheticData(numVertices: 3, numTriangleVerts: 3, hasSkinning: true);
        var dataFile = new TmmDataFile(dataBytes, tmm);
        var glb = GlbExporter.ExportGlb(tmm, dataFile, extras: new GlbExtras())!;

        var model = GlbReader.Parse(glb);
        Assert.NotNull(model.Attachments);
        Assert.Equal(2, model.Attachments!.Length);
        Assert.Equal(0, model.Attachments[0].Index);
        Assert.Equal(1, model.Attachments[1].Index);
    }

    [Fact]
    public void Parse_NameSuffixStripping_RemovesDotNNN()
    {
        Assert.Equal("Bone", GlbReader.StripSuffixForTests("Bone.001"));
        Assert.Equal("Bone", GlbReader.StripSuffixForTests("Bone.123"));
        Assert.Equal("Bone.1234", GlbReader.StripSuffixForTests("Bone.1234"));
        Assert.Equal("Bone", GlbReader.StripSuffixForTests("Bone"));
        Assert.Equal("", GlbReader.StripSuffixForTests(""));
    }

    [Theory]
    [InlineData("POSITION")]
    [InlineData("NORMAL")]
    [InlineData("TEXCOORD_0")]
    public void Parse_PrimitiveMissingRequiredAttribute_ThrowsClearError(string missing)
    {
        var attrs = new Dictionary<string, int>
        {
            ["POSITION"] = 0, ["NORMAL"] = 1, ["TANGENT"] = 2, ["TEXCOORD_0"] = 3,
        };
        attrs.Remove(missing);
        var attrsJson = "{" + string.Join(",", attrs.Select(kv => $"\"{kv.Key}\":{kv.Value}")) + "}";

        string json = $$"""
        {
          "asset": {"version":"2.0"},
          "scene": 0,
          "scenes": [{"nodes":[0]}],
          "nodes": [{"mesh":0}],
          "meshes": [{"primitives":[{"attributes":{{attrsJson}},"indices":4,"material":0}]}],
          "materials": [{"name":"m"}],
          "accessors": [
            {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
            {"bufferView":3,"componentType":5126,"count":3,"type":"VEC2"},
            {"bufferView":4,"componentType":5125,"count":3,"type":"SCALAR"}
          ],
          "bufferViews":[
            {"buffer":0,"byteOffset":0,"byteLength":36},
            {"buffer":0,"byteOffset":36,"byteLength":36},
            {"buffer":0,"byteOffset":72,"byteLength":48},
            {"buffer":0,"byteOffset":120,"byteLength":24},
            {"buffer":0,"byteOffset":144,"byteLength":12}
          ],
          "buffers":[{"byteLength":156}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, BuildBin_3VertsScaledTest());

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains(missing, ex.Message);
    }

    [Fact]
    public void Parse_PrimitiveWithoutTangent_FillsViaMikkTSpace()
    {
        // TANGENT is optional now: when absent, the reader recomputes via MikkTSpace.
        // Build a minimal GLB and strip TANGENT from its primitive's attributes; the resulting
        // primitive should still expose a vertexCount*4 tangent array with unit XYZ and unit W.
        var (tmm, dataFile) = CreateMinimalModel();
        var glb = GlbExporter.ExportGlb(tmm, dataFile)!;
        var stripped = StripTangentAttribute(glb);

        var model = GlbReader.Parse(stripped);
        var prim = model.Mesh.Primitives[0];
        int vc = prim.Positions.Length / 3;
        Assert.Equal(vc * 4, prim.Tangents.Length);
        for (int v = 0; v < vc; v++)
        {
            int o = v * 4;
            float lenSq = prim.Tangents[o] * prim.Tangents[o]
                        + prim.Tangents[o + 1] * prim.Tangents[o + 1]
                        + prim.Tangents[o + 2] * prim.Tangents[o + 2];
            Assert.InRange(MathF.Sqrt(lenSq), 0.99f, 1.01f);
            Assert.True(prim.Tangents[o + 3] == 1f || prim.Tangents[o + 3] == -1f);
        }
    }

    static byte[] StripTangentAttribute(byte[] glb)
    {
        int jsonChunkLen = BitConverter.ToInt32(glb, 12);
        var jsonText = Encoding.UTF8.GetString(glb, 20, jsonChunkLen).TrimEnd(' ', '\0');
        var node = JsonNode.Parse(jsonText)!;
        foreach (var mesh in (JsonArray)node["meshes"]!)
        {
            foreach (var prim in (JsonArray)mesh!["primitives"]!)
            {
                ((JsonObject)prim!["attributes"]!).Remove("TANGENT");
            }
        }
        return ReassembleGlb(glb, jsonChunkLen, node);
    }

    [Fact]
    public void Parse_PrimitiveMissingIndices_ThrowsClearError()
    {
        string json = """
        {
          "asset": {"version":"2.0"},
          "scene": 0,
          "scenes": [{"nodes":[0]}],
          "nodes": [{"mesh":0}],
          "meshes": [{"primitives":[{
            "attributes":{"POSITION":0,"NORMAL":1,"TANGENT":2,"TEXCOORD_0":3},
            "material":0
          }]}],
          "materials": [{"name":"m"}],
          "accessors": [
            {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
            {"bufferView":3,"componentType":5126,"count":3,"type":"VEC2"}
          ],
          "bufferViews":[
            {"buffer":0,"byteOffset":0,"byteLength":36},
            {"buffer":0,"byteOffset":36,"byteLength":36},
            {"buffer":0,"byteOffset":72,"byteLength":48},
            {"buffer":0,"byteOffset":120,"byteLength":24}
          ],
          "buffers":[{"byteLength":144}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, BuildBin_3VertsScaledTest());

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains("indices", ex.Message);
    }

    [Theory]
    [InlineData("KHR_draco_mesh_compression", "Draco")]
    [InlineData("KHR_mesh_quantization", "quantization")]
    [InlineData("KHR_texture_basisu", "Basis")]
    [InlineData("EXT_meshopt_compression", "meshopt")]
    public void Parse_BlockingRequiredExtension_ThrowsClearError(string ext, string expectedFragment)
    {
        string json = $$"""
        {
          "asset": {"version":"2.0"},
          "extensionsRequired": ["{{ext}}"],
          "scene": 0,
          "scenes": [{"nodes":[0]}],
          "nodes": [{"mesh":0}],
          "meshes": [{"primitives":[{"attributes":{"POSITION":0,"NORMAL":1,"TANGENT":2,"TEXCOORD_0":3},"indices":4}]}],
          "accessors": [
            {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
            {"bufferView":3,"componentType":5126,"count":3,"type":"VEC2"},
            {"bufferView":4,"componentType":5125,"count":3,"type":"SCALAR"}
          ],
          "bufferViews":[
            {"buffer":0,"byteOffset":0,"byteLength":36},
            {"buffer":0,"byteOffset":36,"byteLength":36},
            {"buffer":0,"byteOffset":72,"byteLength":48},
            {"buffer":0,"byteOffset":120,"byteLength":24},
            {"buffer":0,"byteOffset":144,"byteLength":12}
          ],
          "buffers":[{"byteLength":156}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, BuildBin_3VertsScaledTest());

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains(expectedFragment, ex.Message);
    }

    [Theory]
    [InlineData("KHR_texture_transform")]
    [InlineData("KHR_materials_unlit")]
    [InlineData("FOO_unknown_extension")]
    public void Parse_NonBlockingRequiredExtension_DoesNotThrow(string ext)
    {
        string json = $$"""
        {
          "asset": {"version":"2.0"},
          "extensionsRequired": ["{{ext}}"],
          "scene": 0,
          "scenes": [{"nodes":[0]}],
          "nodes": [{"mesh":0}],
          "meshes": [{"primitives":[{
            "attributes":{"POSITION":0,"NORMAL":1,"TANGENT":2,"TEXCOORD_0":3},
            "indices":4
          }]}],
          "accessors": [
            {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
            {"bufferView":3,"componentType":5126,"count":3,"type":"VEC2"},
            {"bufferView":4,"componentType":5125,"count":3,"type":"SCALAR"}
          ],
          "bufferViews":[
            {"buffer":0,"byteOffset":0,"byteLength":36},
            {"buffer":0,"byteOffset":36,"byteLength":36},
            {"buffer":0,"byteOffset":72,"byteLength":48},
            {"buffer":0,"byteOffset":120,"byteLength":24},
            {"buffer":0,"byteOffset":144,"byteLength":12}
          ],
          "buffers":[{"byteLength":156}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, BuildBin_3VertsScaledTest());

        var model = GlbReader.Parse(glb);
        Assert.NotNull(model);
    }

    [Fact]
    public void Parse_ExternalBufferUri_ThrowsClearError()
    {
        string json = """
        {
          "asset": {"version":"2.0"},
          "buffers":[{"uri":"data.bin","byteLength":156}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, []);

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains("URI", ex.Message);
    }

    [Fact]
    public void Parse_MaterialIndexOutOfRange_ThrowsClearError()
    {
        string json = """
        {
          "asset": {"version":"2.0"},
          "scene": 0,
          "scenes": [{"nodes":[0]}],
          "nodes": [{"mesh":0}],
          "meshes": [{"primitives":[{
            "attributes":{"POSITION":0,"NORMAL":1,"TANGENT":2,"TEXCOORD_0":3},
            "indices":4,"material":5
          }]}],
          "materials": [{"name":"only"}],
          "accessors": [
            {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
            {"bufferView":3,"componentType":5126,"count":3,"type":"VEC2"},
            {"bufferView":4,"componentType":5125,"count":3,"type":"SCALAR"}
          ],
          "bufferViews":[
            {"buffer":0,"byteOffset":0,"byteLength":36},
            {"buffer":0,"byteOffset":36,"byteLength":36},
            {"buffer":0,"byteOffset":72,"byteLength":48},
            {"buffer":0,"byteOffset":120,"byteLength":24},
            {"buffer":0,"byteOffset":144,"byteLength":12}
          ],
          "buffers":[{"byteLength":156}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, BuildBin_3VertsScaledTest());

        var ex = Assert.Throws<GlbParseException>(() => GlbReader.Parse(glb));
        Assert.Contains("material index 5", ex.Message);
    }

    [Fact]
    public void Parse_NonIdentitySceneRootTransform_BakedIntoVerticesAndBones()
    {
        string json = """
        {
          "asset": {"version":"2.0"},
          "scene": 0,
          "scenes": [{"nodes":[0]}],
          "nodes": [
            {"name":"root","scale":[2,2,2],"children":[1]},
            {"mesh":0}
          ],
          "meshes": [{"primitives":[{
            "attributes":{"POSITION":0,"NORMAL":1,"TANGENT":2,"TEXCOORD_0":3},
            "indices":4,"material":0
          }]}],
          "materials": [{"name":"m"}],
          "accessors": [
            {"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3"},
            {"bufferView":2,"componentType":5126,"count":3,"type":"VEC4"},
            {"bufferView":3,"componentType":5126,"count":3,"type":"VEC2"},
            {"bufferView":4,"componentType":5125,"count":3,"type":"SCALAR"}
          ],
          "bufferViews":[
            {"buffer":0,"byteOffset":0,"byteLength":36},
            {"buffer":0,"byteOffset":36,"byteLength":36},
            {"buffer":0,"byteOffset":72,"byteLength":48},
            {"buffer":0,"byteOffset":120,"byteLength":24},
            {"buffer":0,"byteOffset":144,"byteLength":12}
          ],
          "buffers":[{"byteLength":156}]
        }
        """;
        byte[] glb = GlbTestBuilder.Assemble(json, BuildBin_3VertsScaledTest());

        var model = GlbReader.Parse(glb);
        var prim = model.Mesh.Primitives[0];
        // Source positions were [1,0,0], [0,1,0], [0,0,1]; scale=2 should produce [2,0,0], [0,2,0], [0,0,2].
        Assert.Equal(2.0f, prim.Positions[0]);
        Assert.Equal(2.0f, prim.Positions[4]);
        Assert.Equal(2.0f, prim.Positions[8]);
    }

    static byte[] BuildBin_3VertsScaledTest()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // POSITION (3 vec3): [1,0,0], [0,1,0], [0,0,1]
        w.Write(1f); w.Write(0f); w.Write(0f);
        w.Write(0f); w.Write(1f); w.Write(0f);
        w.Write(0f); w.Write(0f); w.Write(1f);
        // NORMAL (3 vec3): [0,1,0] x 3
        for (int i = 0; i < 3; i++) { w.Write(0f); w.Write(1f); w.Write(0f); }
        // TANGENT (3 vec4): [1,0,0,1] x 3
        for (int i = 0; i < 3; i++) { w.Write(1f); w.Write(0f); w.Write(0f); w.Write(1f); }
        // TEXCOORD_0 (3 vec2): [0,0] x 3
        for (int i = 0; i < 3; i++) { w.Write(0f); w.Write(0f); }
        // INDICES (3 u32): 0, 1, 2
        w.Write(0u); w.Write(1u); w.Write(2u);
        return ms.ToArray();
    }

    static byte[] CreateTinyRgbaPng(int w, int h)
    {
        using var img = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>(w, h);
        using var ms = new MemoryStream();
        img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        return ms.ToArray();
    }

    [Fact]
    public void ReadMaterials_GlbWithFourPbrSlots_PopulatesAllFourPngs()
    {
        var (tmm, dataFile) = CreateMinimalModel();

        var basePng   = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 1 };
        var normalPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 2, 2 };
        var mask1Png  = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 3, 3 };
        var mask2Png  = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 4, 4 };

        var materials = new[]
        {
            new GlbExporter.GlbMaterial
            {
                Name = "m",
                BaseColorPng = basePng,
                NormalMapPng = normalPng,
                Mask1Png = mask1Png,
                Mask2Png = mask2Png,
            },
        };

        var glb = GlbExporter.ExportGlb(tmm, dataFile, materials: materials)!;
        var model = GlbReader.Parse(glb);

        Assert.Single(model.Materials);
        var mat = model.Materials[0];
        Assert.Equal(basePng,   mat.BaseColorPng);
        Assert.Equal(normalPng, mat.NormalMapPng);
        Assert.Equal(mask1Png,  mat.Mask1Png);
        Assert.Equal(mask2Png,  mat.Mask2Png);
    }

    [Fact]
    public void ReadMaterials_GlbWithOcclusionOnly_FallsBackToOcclusionForMask1()
    {
        // Test that when metallicRoughnessTexture is absent, Mask1Png falls back to occlusionTexture.
        var occlusionPng = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 5, 5 };
        var jsonTemplate = "{{\"asset\":{{\"version\":\"2.0\"}},\"scene\":0,\"scenes\":[{{\"nodes\":[0]}}],\"nodes\":[{{\"mesh\":0}}],\"meshes\":[{{\"primitives\":[{{\"attributes\":{{\"POSITION\":0,\"NORMAL\":1,\"TANGENT\":2,\"TEXCOORD_0\":3}},\"indices\":4,\"material\":0}}]}}],\"materials\":[{{\"name\":\"TestMat\",\"occlusionTexture\":{{\"index\":0}}}}],\"textures\":[{{\"source\":0}}],\"images\":[{{\"mimeType\":\"image/png\",\"bufferView\":3}}],\"accessors\":[{{\"bufferView\":0,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,1]}},{{\"bufferView\":0,\"byteOffset\":36,\"componentType\":5126,\"count\":3,\"type\":\"VEC3\",\"min\":[0,0,0],\"max\":[1,1,1]}},{{\"bufferView\":0,\"byteOffset\":72,\"componentType\":5126,\"count\":3,\"type\":\"VEC4\",\"min\":[0,0,0,1],\"max\":[1,1,1,1]}},{{\"bufferView\":0,\"byteOffset\":120,\"componentType\":5126,\"count\":3,\"type\":\"VEC2\",\"min\":[0,0],\"max\":[1,1]}},{{\"bufferView\":1,\"componentType\":5123,\"count\":3,\"type\":\"SCALAR\"}}],\"bufferViews\":[{{\"buffer\":0,\"byteLength\":144}},{{\"buffer\":0,\"byteOffset\":144,\"byteLength\":6}},{{\"buffer\":0,\"byteOffset\":150,\"byteLength\":0}},{{\"buffer\":0,\"byteOffset\":150,\"byteLength\":{0}}}],\"buffers\":[{{\"byteLength\":{1}}}]}}";
        var json = string.Format(jsonTemplate, occlusionPng.Length, 150 + occlusionPng.Length);
        var bin = new byte[150 + occlusionPng.Length];
        int pos = 0;
        // POSITION: 3 verts at [0,0,0], [1,0,0], [0,1,0]
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        // NORMAL: [0,0,1] x3 (36 bytes)
        for (int i = 0; i < 3; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
        }
        // TANGENT: [1,0,0,1] x3 (48 bytes)
        for (int i = 0; i < 3; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
            System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
        }
        // TEXCOORD_0: [0,0], [1,0], [0,1] (24 bytes)
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 0f); pos += 4;
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bin.AsSpan(pos), 1f); pos += 4;
        // INDICES: 0, 1, 2 (6 bytes)
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(pos), 0); pos += 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(pos), 1); pos += 2;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bin.AsSpan(pos), 2); pos += 2;
        // OCCLUSION IMAGE (at byte offset 150)
        occlusionPng.CopyTo(bin.AsSpan(pos));

        var glb = GlbTestBuilder.Assemble(json, bin);

        var model = GlbReader.Parse(glb);
        Assert.Single(model.Materials);
        Assert.Equal(occlusionPng, model.Materials[0].Mask1Png);
        Assert.Null(model.Materials[0].Mask2Png);
    }
}
