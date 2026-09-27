using CryBar.Export;
using CryBar.TMM;

namespace CryBar.Tests;

public class TmmWriterTests
{
    [Fact]
    public void Write_SkinnedModel_SkinWeightsAreStartPadded()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                        JointIndices = [0, 1, 0, 0,  0, 0, 0, 0,  1, 0, 0, 0],
                        JointWeights = [0.5f, 0.5f, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0],
                    }
                ]
            },
            Bones =
            [
                new GlbBone { Name = "root", LocalMatrix = MatrixDecomp.Compose(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One), InverseBindMatrix = Identity16(), ParentIndex = -1 },
                new GlbBone { Name = "child", LocalMatrix = MatrixDecomp.Compose(System.Numerics.Vector3.Zero, System.Numerics.Quaternion.Identity, System.Numerics.Vector3.One), InverseBindMatrix = Identity16(), ParentIndex = 0 },
            ],
            Materials = [new GlbMaterial { Name = "m" }],
        };

        var (tmm, data, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.FullyParsed);
        var dataFile = new TmmDataFile(data, parsed);
        Assert.True(dataFile.Parsed);
        Assert.NotNull(dataFile.SkinWeights);

        // Vertex 0 had weights [0.5, 0.5, 0, 0]: zeros prepended -> [0, 0, 127, 128] (sum 255).
        var w0 = dataFile.SkinWeights![0];
        Assert.Equal(0, w0.Weight0); Assert.Equal(0, w0.Weight1);
        Assert.True(w0.Weight2 + w0.Weight3 == 255);
        Assert.Equal(0, w0.BoneIndex0); Assert.Equal(0, w0.BoneIndex1);
    }

    [Fact]
    public void Write_SkinWeights_AscendingOrder_DominantBoneAtSlot3()
    {
        // Vanilla format: weights ascending, dominant bone at slot 3.
        // Three nonzero weights: 0.1, 0.3, 0.6 with bones 5, 7, 9.
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                        // Order intentionally jumbled: slot 0=largest, to verify the writer sorts.
                        JointIndices = [9, 5, 7, 0,   0, 0, 0, 0,   0, 0, 0, 0],
                        JointWeights = [0.6f, 0.1f, 0.3f, 0,   1, 0, 0, 0,   1, 0, 0, 0],
                    }
                ]
            },
            Bones =
            [
                new GlbBone { Name = "b0", ParentIndex = -1, LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b1", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b2", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b3", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b4", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b5", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b6", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b7", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b8", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
                new GlbBone { Name = "b9", ParentIndex = 0,  LocalMatrix = Identity16(), InverseBindMatrix = Identity16() },
            ],
            Materials = [new GlbMaterial { Name = "m" }],
        };

        var (tmm, data, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        var dataFile = new TmmDataFile(data, parsed);
        var sw = dataFile.SkinWeights![0];

        Assert.Equal(0, sw.Weight0);
        Assert.True(sw.Weight1 < sw.Weight2);
        Assert.True(sw.Weight2 < sw.Weight3);
        Assert.Equal(255, sw.Weight1 + sw.Weight2 + sw.Weight3);
        Assert.Equal(9, sw.BoneIndex3);
        Assert.Equal(7, sw.BoneIndex2);
        Assert.Equal(5, sw.BoneIndex1);
    }

    [Fact]
    public void Write_SkinWeights_PreservesBoneIndexEvenWhenWeightZero()
    {
        // Vanilla retains bone indices in zero-weight slots; some engine paths read
        // the index regardless of weight.
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                        // Vertex 0 has bone 25 with weight 0; should still appear in the output.
                        JointIndices = [25, 18, 19, 29,   0, 0, 0, 0,   0, 0, 0, 0],
                        JointWeights = [0f, 106f/255f, 97f/255f, 52f/255f,   1, 0, 0, 0,   1, 0, 0, 0],
                    }
                ]
            },
            Bones = Enumerable.Range(0, 30)
                .Select(i => new GlbBone { Name = $"b{i}", ParentIndex = i == 0 ? -1 : 0, LocalMatrix = Identity16(), InverseBindMatrix = Identity16() })
                .ToArray(),
            Materials = [new GlbMaterial { Name = "m" }],
        };

        var (tmm, data, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        var dataFile = new TmmDataFile(data, parsed);
        var sw = dataFile.SkinWeights![0];

        // Bone 25 has weight 0; should still be present in the bone-index byte stream.
        var indices = new[] { sw.BoneIndex0, sw.BoneIndex1, sw.BoneIndex2, sw.BoneIndex3 };
        Assert.Contains((byte)25, indices);
        Assert.Contains((byte)18, indices);
        Assert.Contains((byte)19, indices);
        Assert.Contains((byte)29, indices);
        Assert.Equal(0, sw.Weight0);
    }

    [Fact]
    public void Write_BoundingBox_IsInGameSpace()
    {
        // glTF positions are X-negated relative to game; the written bbox must be in
        // game space (matching the vertex stream after negation).
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        // Asymmetric in X: glTF X = [-2, 5] -> game X = [-5, 2]
                        Positions = [-2, 0, 0,  5, 1, 0,  3, 1, 1],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                    }
                ]
            },
            Materials = [new GlbMaterial { Name = "m" }],
        };

        var (tmm, _, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.Equal(-5f, parsed.BoundingBox.MinX, 4);
        Assert.Equal(2f,  parsed.BoundingBox.MaxX, 4);
    }

    [Fact]
    public void Write_EmptyMainMatrix_FallsBackToIdentity()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh { Primitives = [] },
            Extras = new GlbExtras { Tmm = new GlbExtras.TmmSection { MainMatrix = [] } },
        };

        var (tmm, _, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.Parsed);
        // Identity 4x3 (12 floats expanded to 4x4 by parser): diagonal is 1.
        Assert.Equal(1f, parsed.MainMatrix![0]);
        Assert.Equal(1f, parsed.MainMatrix[5]);
        Assert.Equal(1f, parsed.MainMatrix[10]);
    }

    [Fact]
    public void Write_TwoBoneSkeleton_BoneRecordsRoundTrip()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh { Primitives = [] },
            Bones =
            [
                new GlbBone { Name = "root", ParentIndex = -1,
                    LocalMatrix = Identity16(),
                    InverseBindMatrix = Identity16() },
                new GlbBone { Name = "child", ParentIndex = 0,
                    LocalMatrix = TranslationMatrix(0, 1, 0),
                    InverseBindMatrix = TranslationMatrix(0, -1, 0) },
            ],
        };

        var (tmm, _, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.FullyParsed);
        Assert.Equal(2, parsed.Bones!.Length);
        Assert.Equal("root", parsed.Bones[0].Name);
        Assert.Equal("child", parsed.Bones[1].Name);
        Assert.Equal(-1, parsed.Bones[0].ParentId);
        Assert.Equal(0, parsed.Bones[1].ParentId);
    }

    [Fact]
    public void Write_WithLossySections_EmitsWarnings()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh { Primitives = [] },
            Extras = new GlbExtras
            {
                Tmm = new GlbExtras.TmmSection { LossySections = new List<string> { "destruction", "physics" } }
            },
        };
        var (_, _, warnings) = TmmWriter.Write(model);
        Assert.Contains(warnings, w => w.Contains("destruction", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, w => w.Contains("physics", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Write_WithExtrasAttachments_PopulatesAttachmentRecords()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh { Primitives = [] },
            Attachments = [ new GlbAttachment { Name = "spear_tip", Index = 0, ParentBoneIndex = 0,
                                                LocalMatrix = TranslationMatrix(1, 2, 3) } ],
            Bones = [ new GlbBone { Name = "root", ParentIndex = -1,
                                    LocalMatrix = Identity16(), InverseBindMatrix = Identity16() } ],
            Extras = new GlbExtras
            {
                Tmm = new GlbExtras.TmmSection
                {
                    Attachments =
                    [
                        new GlbExtras.AttachmentEntry
                        {
                            Name = "spear_tip", TypeFlag = 5, ForcedDummyBoneName = "tip",
                            FrameLimit = 10, FramePosition = 0.5f,
                        }
                    ]
                }
            },
        };
        var (tmm, _, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.FullyParsed);
        var atts = parsed.Attachments!;
        Assert.Single(atts);
        Assert.Equal(5u, atts[0].TypeFlag);
        Assert.Equal("tip", atts[0].ForcedDummyBoneName);
        Assert.Equal(10, atts[0].FrameLimit);
    }

    [Fact]
    public void Write_SelfValidates_ProducesParseableOutputForValidInput()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh { Primitives = [] },
        };
        var ex = Record.Exception(() => TmmWriter.Write(model));
        Assert.Null(ex);
    }

    [Fact]
    public void Write_BlenderStyleInvertedTangentW_ProducesCorrectHandedness()
    {
        // Two GLBs of the same X-mirrored surface differing only in tangent.w sign:
        // one matches MikkTSpace (CryBar-direct), one is sign-inverted (Blender re-export).
        // The writer must detect the inversion and produce identical TBN bytes for both.
        var positions = new float[] { -0f, 0, 0,  -1f, 0, 0,  -1f, 1f, 0 };
        var normals = new float[] { 0, 0, 1, 0, 0, 1, 0, 0, 1 };
        var texcoords = new float[] { 0, 0, 1, 0, 1, 1 };
        var directLikeTangents = new float[] { -1, 0, 0, -1,  -1, 0, 0, -1,  -1, 0, 0, -1 };
        var blenderLikeTangents = new float[] { -1, 0, 0, +1,  -1, 0, 0, +1,  -1, 0, 0, +1 };

        var (directTmm, directData, _) = TmmWriter.Write(MakeModel(positions, normals, directLikeTangents, texcoords));
        var (_, blenderData, _) = TmmWriter.Write(MakeModel(positions, normals, blenderLikeTangents, texcoords));

        var dirTmm = new TmmFile(directTmm);
        Assert.True(dirTmm.FullyParsed);

        for (int i = 0; i < (int)dirTmm.NumVertices; i++)
        {
            int off = i * 16 + 10;
            ushort dTbnX = BitConverter.ToUInt16(directData, off);
            ushort dTbnY = BitConverter.ToUInt16(directData, off + 2);
            ushort dTbnZ = BitConverter.ToUInt16(directData, off + 4);
            ushort bTbnX = BitConverter.ToUInt16(blenderData, off);
            ushort bTbnY = BitConverter.ToUInt16(blenderData, off + 2);
            ushort bTbnZ = BitConverter.ToUInt16(blenderData, off + 4);
            Assert.Equal(dTbnX, bTbnX);
            Assert.Equal(dTbnY, bTbnY);
            Assert.Equal(dTbnZ, bTbnZ);
            Assert.Equal(0, dTbnX & 0x8000);
            Assert.Equal(0, bTbnX & 0x8000);
        }
    }

    static GlbModel MakeModel(float[] positions, float[] normals, float[] tangents, float[] texcoords, uint[]? indices = null) => new()
    {
        Mesh = new GlbMesh
        {
            Primitives =
            [
                new GlbMeshPrimitive
                {
                    MaterialName = "m",
                    Positions = positions,
                    Normals = normals,
                    Tangents = tangents,
                    TexCoords = texcoords,
                    Indices = indices ?? [0, 1, 2],
                }
            ]
        },
        Materials = [new GlbMaterial { Name = "m" }],
    };

    static TmmSkinWeight WriteSingleVertexWeights(float[] weights, byte[] joints)
    {
        var model = MakeMinimalSkinnedModel(firstVertexWeights: weights, firstVertexJoints: joints, boneCount: 4);

        var (tmm, data, _) = TmmWriter.Write(model);
        var dataFile = new TmmDataFile(data, new TmmFile(tmm));
        Assert.True(dataFile.Parsed);
        return dataFile.SkinWeights![0];
    }

    static int WeightSum(TmmSkinWeight sw) => sw.Weight0 + sw.Weight1 + sw.Weight2 + sw.Weight3;

    [Fact]
    public void Write_SkinWeights_UnnormalizedEqualWeights_SumTo255WithoutWrap()
    {
        var sw = WriteSingleVertexWeights([0.5f, 0.5f, 0.5f, 0.5f], [0, 1, 2, 3]);

        Assert.Equal(255, WeightSum(sw));
        foreach (var b in new[] { sw.Weight0, sw.Weight1, sw.Weight2, sw.Weight3 })
            Assert.InRange(b, 63, 64);
        Assert.True(sw.Weight0 <= sw.Weight1 && sw.Weight1 <= sw.Weight2 && sw.Weight2 <= sw.Weight3);
    }

    [Fact]
    public void Write_SkinWeights_WeightAboveOne_ClampedAndNormalized()
    {
        var sw = WriteSingleVertexWeights([1.2f, 0, 0, 0], [2, 0, 0, 0]);

        Assert.Equal(255, WeightSum(sw));
        Assert.Equal(255, sw.Weight3);
        Assert.Equal(2, sw.BoneIndex3);
    }

    [Fact]
    public void Write_SkinWeights_NegativeAndNaN_TreatedAsZero()
    {
        var sw = WriteSingleVertexWeights([-0.5f, float.NaN, 0.25f, 0.25f], [1, 2, 3, 0]);

        Assert.Equal(255, WeightSum(sw));
        Assert.Equal(0, sw.Weight0);
        Assert.Equal(0, sw.Weight1);
        Assert.InRange(sw.Weight2, 127, 128);
        Assert.InRange(sw.Weight3, 127, 128);
    }

    [Fact]
    public void Write_SkinWeights_DroppedInfluences_RenormalizedTo255()
    {
        // The first four influences of an 8-influence vertex sum to less than 1.
        var sw = WriteSingleVertexWeights([0.3f, 0.2f, 0.1f, 0.1f], [0, 1, 2, 3]);

        Assert.Equal(255, WeightSum(sw));
        Assert.Equal(0, sw.BoneIndex3);
        Assert.InRange(sw.Weight3, 108, 110);
    }

    [Fact]
    public void QuantizeWeights_AlwaysSumsTo255AndPreservesOrder()
    {
        var rng = new Random(1234);
        Span<(float w, byte b)> pairs = stackalloc (float, byte)[4];
        Span<byte> bytes = stackalloc byte[4];

        for (int iter = 0; iter < 5000; iter++)
        {
            var ws = new float[4];
            for (int s = 0; s < 4; s++) ws[s] = rng.Next(4) == 0 ? 0f : (float)rng.NextDouble();
            Array.Sort(ws);

            float sum = 0f;
            for (int s = 0; s < 4; s++) { pairs[s] = (ws[s], (byte)s); sum += ws[s]; }

            TmmWriter.QuantizeWeights(pairs, bytes);

            int total = bytes[0] + bytes[1] + bytes[2] + bytes[3];
            Assert.Equal(sum > 0f ? 255 : 0, total);
            for (int s = 1; s < 4; s++) Assert.True(bytes[s - 1] <= bytes[s]);
        }
    }

    static GlbModel SinglePrimitiveModel(float[] positions, uint[] indices)
    {
        int vc = positions.Length / 3;
        var normals = new float[vc * 3];
        var tangents = new float[vc * 4];
        for (int i = 0; i < vc; i++)
        {
            normals[i * 3 + 2] = 1;
            tangents[i * 4] = 1;
            tangents[i * 4 + 3] = 1;
        }

        return MakeModel(positions, normals, tangents, new float[vc * 2], indices);
    }

    [Fact]
    public void Write_PrimitiveWithMoreThan65536Vertices_Throws()
    {
        var model = SinglePrimitiveModel(new float[65537 * 3], [0, 1, 65536]);

        var ex = Assert.Throws<InvalidDataException>(() => TmmWriter.Write(model));
        Assert.Contains("65537", ex.Message);
    }

    [Fact]
    public void Write_IndexOutOfRange_Throws()
    {
        var model = SinglePrimitiveModel([0, 0, 0,  1, 0, 0,  1, 1, 0], [0, 1, 3]);

        var ex = Assert.Throws<InvalidDataException>(() => TmmWriter.Write(model));
        Assert.Contains("out of range", ex.Message);
    }

    [Fact]
    public void Write_AttributeLengthMismatch_Throws()
    {
        var model = SinglePrimitiveModel([0, 0, 0,  1, 0, 0,  1, 1, 0], [0, 1, 2]);
        var prim = model.Mesh.Primitives[0];
        var broken = MakeModel(prim.Positions, [0, 0, 1], prim.Tangents, prim.TexCoords);

        var ex = Assert.Throws<InvalidDataException>(() => TmmWriter.Write(broken));
        Assert.Contains("NORMAL", ex.Message);
    }

    static float[] Identity16() => [1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1];

    static float[] TranslationMatrix(float x, float y, float z)
    {
        var m = (float[])Identity16().Clone();
        m[12] = x; m[13] = y; m[14] = z;
        return m;
    }

    [Fact]
    public void Write_NullExtrasOnSkinnedModel_EmitsThreeFallbackWarnings()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                        JointIndices = [0, 0, 0, 0,  0, 0, 0, 0,  0, 0, 0, 0],
                        JointWeights = [1, 0, 0, 0,  1, 0, 0, 0,  1, 0, 0, 0],
                    }
                ]
            },
            Materials = [new GlbMaterial { Name = "m" }],
            Bones =
            [
                new GlbBone
                {
                    Name = "root", ParentIndex = -1,
                    LocalMatrix = TmmTestHelpers.Identity16Local(),
                    InverseBindMatrix = TmmTestHelpers.Identity16Local()
                }
            ],
        };

        var (_, _, warnings) = TmmWriter.Write(model);

        Assert.Contains(warnings, w => w.Contains("main_matrix", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("extended_bbox", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("auto_attach", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_NonSkinnedModelWithoutExtras_DoesNotWarnAboutExtendedBboxOrAutoAttach()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                    }
                ]
            },
            Materials = [new GlbMaterial { Name = "m" }],
        };

        var (_, _, warnings) = TmmWriter.Write(model);

        Assert.Contains(warnings, w => w.Contains("main_matrix", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("extended_bbox", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("auto_attach", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_ExtrasPresent_NoFallbackWarnings()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1],
                        Indices = [0, 1, 2],
                    }
                ]
            },
            Materials = [new GlbMaterial { Name = "m" }],
            Extras = new GlbExtras
            {
                HasFullTmmBlock = true,
                Tmm = new GlbExtras.TmmSection
                {
                    MainMatrix = [1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0,  0, 0, 0, 1],
                    ExtendedBbox = [-1, -1, -1, 1, 1, 1],
                    AutoAttach = new GlbExtras.AutoAttachInfo(),
                }
            },
        };

        var (_, _, warnings) = TmmWriter.Write(model);

        Assert.DoesNotContain(warnings, w => w.Contains("main_matrix", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("extended_bbox", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("auto_attach", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_EmptyModel_ProducesParseableTmm()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh { Primitives = [] },
        };

        var (tmm, data, warnings) = TmmWriter.Write(model);

        Assert.NotNull(tmm);
        Assert.NotNull(data);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.ParseHeader());
        Assert.Equal(37u, parsed.Version);
    }

    [Fact]
    public void Write_SingleQuad_VertexBufferRoundTripsThroughTmmFileParse()
    {
        var model = new GlbModel
        {
            Mesh = new GlbMesh
            {
                Primitives =
                [
                    new GlbMeshPrimitive
                    {
                        MaterialName = "m",
                        Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0,  0, 1, 0],
                        Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1,   0, 0, 1],
                        Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                        TexCoords = [0, 0,  1, 0,  1, 1,  0, 1],
                        Indices = [0, 1, 2,  0, 2, 3],
                    }
                ]
            },
            Materials = [new GlbMaterial { Name = "m" }],
        };

        var (tmm, data, _) = TmmWriter.Write(model);

        var parsed = new TmmFile(tmm);
        Assert.True(parsed.ParseHeader());
        Assert.True(parsed.FullyParsed);
        Assert.Equal(4u, parsed.NumVertices);
        Assert.Equal(6u, parsed.NumTriangleVerts);

        var dataFile = new TmmDataFile(data, parsed);
        Assert.True(dataFile.Parsed);
        Assert.Equal(4, dataFile.Vertices!.Length);
        Assert.Equal(6, dataFile.Indices!.Length);
    }

    [Fact]
    public void Write_BoundsRadius_RoundTripsViaExtras()
    {
        var model = MakeMinimalSkinnedModel(
            new GlbExtras { Tmm = new GlbExtras.TmmSection { BoundsRadius = 1.2036f } });
        var (tmm, _, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.Parsed);
        Assert.Equal(1.2036f, parsed.BoundsRadius);
    }

    [Fact]
    public void Write_BoundsRadius_FallsBackToVertexMaxDistance()
    {
        // For a triangle at (0,0,0)-(1,0,0)-(1,1,0), max distance from origin is sqrt(2).
        var model = MakeMinimalSkinnedModel(extras: null);
        var (tmm, _, _) = TmmWriter.Write(model);
        var parsed = new TmmFile(tmm);
        Assert.True(parsed.Parsed);
        Assert.InRange(parsed.BoundsRadius, 1.4f, 1.5f);
    }

    static GlbModel MakeMinimalSkinnedModel(GlbExtras? extras = null,
        float[]? firstVertexWeights = null, byte[]? firstVertexJoints = null, int boneCount = 1) => new GlbModel
    {
        Mesh = new GlbMesh
        {
            Primitives =
            [
                new GlbMeshPrimitive
                {
                    MaterialName = "m",
                    Positions = [0, 0, 0,  1, 0, 0,  1, 1, 0],
                    Normals = [0, 0, 1,   0, 0, 1,   0, 0, 1],
                    Tangents = [1, 0, 0, 1,  1, 0, 0, 1,  1, 0, 0, 1],
                    TexCoords = [0, 0,  1, 0,  1, 1],
                    Indices = [0, 1, 2],
                    JointIndices = [.. firstVertexJoints ?? [0, 0, 0, 0],  0, 0, 0, 0,  0, 0, 0, 0],
                    JointWeights = [.. firstVertexWeights ?? [1, 0, 0, 0],  1, 0, 0, 0,  1, 0, 0, 0],
                }
            ]
        },
        Bones = Enumerable.Range(0, boneCount)
            .Select(i => new GlbBone { Name = i == 0 ? "root" : $"b{i}", ParentIndex = i == 0 ? -1 : 0, LocalMatrix = Identity16(), InverseBindMatrix = Identity16() })
            .ToArray(),
        Materials = [new GlbMaterial { Name = "m" }],
        Extras = extras,
    };
}
