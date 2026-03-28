using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

using CryBar.TMM;

namespace CryBar.Export;

/// <summary>
/// Exports TMM game model data to glTF 2.0 binary (GLB) format.
/// </summary>
public static class GlbExporter
{
    public class GlbMaterial
    {
        public required string Name { get; init; }
        public byte[]? BaseColorPng { get; init; }
        public byte[]? NormalMapPng { get; init; }
    }

    public class GlbAnimation
    {
        public required string Name { get; set; }
        public required TmaDecoder.DecodedTrack[] Tracks { get; init; }
        public required float Duration { get; init; }
        public required uint FrameCount { get; init; }
    }

    // per-track offsets into the binary buffer for animation data
    readonly record struct AnimTrackBuffer(int BoneIndex, TmaDecoder.DecodedTrack Track, int TransOffset, int TransLen, int RotOffset, int RotLen, int ScaleOffset, int ScaleLen);
    readonly record struct AnimBuffer(string Name, float Duration, int FrameCount, int TimeOffset, int TimeLen, List<AnimTrackBuffer> Tracks);

    const uint GlbMagic = 0x46546C67;
    const uint GlbVersion = 2;
    const uint ChunkTypeJson = 0x4E4F534A;
    const uint ChunkTypeBin = 0x004E4942;

    /// <summary>
    /// Column-major 4x4 matrix indices that get negated for LH->RH Z-flip.
    /// Bool lookup is O(1) vs Array.IndexOf's O(N) scan per element.
    /// </summary>
    static readonly bool[] ZNegateMask = CreateZNegateMask();
    static bool[] CreateZNegateMask()
    {
        var mask = new bool[16];
        foreach (var i in new[] { 2, 6, 8, 9, 14 }) mask[i] = true;
        return mask;
    }

    /// <summary>
    /// Tracks byte offsets and lengths for each section of the binary buffer.
    /// Passed to BuildJson instead of ~27 individual parameters.
    /// </summary>
    readonly struct BufferLayout
    {
        public readonly int PositionsOffset, PositionsByteLength;
        public readonly int NormalsOffset, NormalsByteLength;
        public readonly int TangentsOffset, TangentsByteLength;
        public readonly int TexcoordsOffset, TexcoordsByteLength;
        public readonly int IndicesOffset, IndicesByteLength;
        public readonly int JointsOffset, JointsByteLength;
        public readonly int WeightsOffset, WeightsByteLength;
        public readonly int IbmOffset, IbmByteLength;
        public readonly List<int> ImageOffsets;
        public readonly List<int> ImageLengths;
        public readonly int TotalBinLength;

        public BufferLayout(
            int positionsOffset, int positionsByteLength,
            int normalsOffset, int normalsByteLength,
            int tangentsOffset, int tangentsByteLength,
            int texcoordsOffset, int texcoordsByteLength,
            int indicesOffset, int indicesByteLength,
            int jointsOffset, int jointsByteLength,
            int weightsOffset, int weightsByteLength,
            int ibmOffset, int ibmByteLength,
            List<int> imageOffsets, List<int> imageLengths,
            int totalBinLength)
        {
            PositionsOffset = positionsOffset; PositionsByteLength = positionsByteLength;
            NormalsOffset = normalsOffset; NormalsByteLength = normalsByteLength;
            TangentsOffset = tangentsOffset; TangentsByteLength = tangentsByteLength;
            TexcoordsOffset = texcoordsOffset; TexcoordsByteLength = texcoordsByteLength;
            IndicesOffset = indicesOffset; IndicesByteLength = indicesByteLength;
            JointsOffset = jointsOffset; JointsByteLength = jointsByteLength;
            WeightsOffset = weightsOffset; WeightsByteLength = weightsByteLength;
            IbmOffset = ibmOffset; IbmByteLength = ibmByteLength;
            ImageOffsets = imageOffsets; ImageLengths = imageLengths;
            TotalBinLength = totalBinLength;
        }
    }

    /// <summary>
    /// Exports a TMM model with its data file to GLB (glTF binary) format.
    /// </summary>
    /// <returns>GLB bytes, or null if the input is not valid.</returns>
    public static byte[]? ExportGlb(TmmFile tmm, TmmDataFile dataFile,
        IReadOnlyList<GlbMaterial>? materials = null,
        IReadOnlyList<GlbAnimation>? animations = null)
    {
        if (!tmm.Parsed || dataFile.Vertices == null || dataFile.Indices == null)
            return null;

        var vertices = dataFile.Vertices;
        var indices = dataFile.Indices;
        var meshGroups = tmm.MeshGroups!;
        var bones = tmm.Bones!;
        var attachments = tmm.Attachments!;

        if (vertices.Length == 0 || indices.Length == 0)
            return null;

        int vertexCount = vertices.Length;
        int indexCount = indices.Length;
        bool hasSkin = bones.Length > 0 && dataFile.SkinWeights != null;
        var skinWeights = dataFile.SkinWeights;
        bool hasMaterials = materials != null && materials.Count > 0;

        // --- Build the binary buffer ---
        // Calculate sizes and offsets for each section (all 4-byte aligned)

        int positionsByteLength = vertexCount * 12; // float[N*3]
        int normalsByteLength = vertexCount * 12;
        int tangentsByteLength = vertexCount * 16; // float[N*4]
        int texcoordsByteLength = vertexCount * 8; // float[N*2]
        int indicesByteLength = indexCount * 2; // ushort[indexCount]

        int positionsOffset = 0;
        int normalsOffset = positionsOffset + Align4(positionsByteLength);
        int tangentsOffset = normalsOffset + Align4(normalsByteLength);
        int texcoordsOffset = tangentsOffset + Align4(tangentsByteLength);
        int indicesOffset = texcoordsOffset + Align4(texcoordsByteLength);

        int afterIndices = indicesOffset + Align4(indicesByteLength);

        // Skinning data
        int jointsOffset = 0, jointsByteLength = 0;
        int weightsOffset = 0, weightsByteLength = 0;
        int ibmOffset = 0, ibmByteLength = 0;

        if (hasSkin)
        {
            jointsByteLength = vertexCount * 4; // ubyte4[N]
            jointsOffset = afterIndices;
            afterIndices = jointsOffset + Align4(jointsByteLength);

            weightsByteLength = vertexCount * 16; // float[N*4]
            weightsOffset = afterIndices;
            afterIndices = weightsOffset + Align4(weightsByteLength);

            ibmByteLength = bones.Length * 64; // float[bones*16]
            ibmOffset = afterIndices;
            afterIndices = ibmOffset + Align4(ibmByteLength);
        }

        // Embedded images
        var imageOffsets = new List<int>();
        var imageLengths = new List<int>();
        if (hasMaterials)
        {
            foreach (var mat in materials!)
            {
                if (mat.BaseColorPng != null)
                {
                    imageOffsets.Add(afterIndices);
                    imageLengths.Add(mat.BaseColorPng.Length);
                    afterIndices += Align4(mat.BaseColorPng.Length);
                }
                if (mat.NormalMapPng != null)
                {
                    imageOffsets.Add(afterIndices);
                    imageLengths.Add(mat.NormalMapPng.Length);
                    afterIndices += Align4(mat.NormalMapPng.Length);
                }
            }
        }

        // Animation data
        var animBuffers = new List<AnimBuffer>();
        bool hasAnimations = animations != null && animations.Count > 0 && hasSkin;
        if (hasAnimations)
        {
            var boneMap = new Dictionary<string, int>(bones.Length);
            for (int i = 0; i < bones.Length; i++)
                boneMap[bones[i].Name] = i;

            foreach (var anim in animations!)
            {
                int frameCount = (int)anim.FrameCount;
                if (frameCount <= 0) continue;

                int timeLen = frameCount * 4;
                int timeOffset = afterIndices;
                afterIndices += Align4(timeLen);

                var matchedTracks = new List<AnimTrackBuffer>();
                foreach (var track in anim.Tracks)
                {
                    if (!boneMap.TryGetValue(track.Name, out int boneIdx)) continue;
                    if (track.Translations.Length < frameCount ||
                        track.Rotations.Length < frameCount ||
                        track.Scales.Length < frameCount) continue;

                    int tLen = frameCount * 12; // VEC3
                    int tOff = afterIndices; afterIndices += Align4(tLen);
                    int rLen = frameCount * 16; // VEC4
                    int rOff = afterIndices; afterIndices += Align4(rLen);
                    int sLen = frameCount * 12; // VEC3
                    int sOff = afterIndices; afterIndices += Align4(sLen);

                    matchedTracks.Add(new AnimTrackBuffer(boneIdx, track, tOff, tLen, rOff, rLen, sOff, sLen));
                }

                if (matchedTracks.Count > 0)
                    animBuffers.Add(new AnimBuffer(anim.Name, anim.Duration, frameCount, timeOffset, timeLen, matchedTracks));
            }
        }

        int totalBinLength = afterIndices;
        int binPadded = Align4(totalBinLength);

        var layout = new BufferLayout(
            positionsOffset, positionsByteLength,
            normalsOffset, normalsByteLength,
            tangentsOffset, tangentsByteLength,
            texcoordsOffset, texcoordsByteLength,
            indicesOffset, indicesByteLength,
            jointsOffset, jointsByteLength,
            weightsOffset, weightsByteLength,
            ibmOffset, ibmByteLength,
            imageOffsets, imageLengths,
            totalBinLength);

        // Compute per-group min/max for positions (required by glTF for all POSITION accessors)
        var groupBounds = new (float[] min, float[] max)[meshGroups.Length];
        for (int g = 0; g < meshGroups.Length; g++)
        {
            var mg = meshGroups[g];
            ComputePositionBounds(vertices, (int)mg.VertexStart, (int)mg.VertexCount,
                out groupBounds[g].min, out groupBounds[g].max);
        }

        // Build JSON chunk first to know its size
        var jsonBytes = BuildJson(tmm, meshGroups, bones, attachments, hasSkin, hasMaterials, materials,
            vertexCount, layout, groupBounds, animBuffers);

        int jsonPadded = Align4(jsonBytes.Length);

        // Allocate final GLB buffer directly (header + JSON chunk + BIN chunk)
        int totalLength = 12 + 8 + jsonPadded + 8 + binPadded;
        var glb = new byte[totalLength];
        int off = 0;

        // GLB header (12 bytes)
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), GlbMagic); off += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), GlbVersion); off += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), (uint)totalLength); off += 4;

        // JSON chunk header
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), (uint)jsonPadded); off += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), ChunkTypeJson); off += 4;
        // JSON data + space padding
        jsonBytes.CopyTo(glb.AsSpan(off));
        for (int i = jsonBytes.Length; i < jsonPadded; i++)
            glb[off + i] = 0x20;
        off += jsonPadded;

        // BIN chunk header
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), (uint)binPadded); off += 4;
        BinaryPrimitives.WriteUInt32LittleEndian(glb.AsSpan(off), ChunkTypeBin); off += 4;

        // Write binary data directly into the GLB buffer's BIN section
        int binStart = off;

        WritePositions(glb, binStart + positionsOffset, vertices);
        WriteNormalsAndTangents(glb, binStart + normalsOffset, binStart + tangentsOffset, vertices);
        WriteTexCoords(glb, binStart + texcoordsOffset, vertices);
        WriteIndices(glb, binStart + indicesOffset, indices);

        if (hasSkin)
        {
            WriteJoints(glb, binStart + jointsOffset, skinWeights!);
            WriteWeights(glb, binStart + weightsOffset, skinWeights!);
            WriteInverseBindMatrices(glb, binStart + ibmOffset, bones);
        }

        if (hasMaterials)
        {
            int imgIdx = 0;
            foreach (var mat in materials!)
            {
                if (mat.BaseColorPng != null)
                {
                    mat.BaseColorPng.CopyTo(glb.AsSpan(binStart + imageOffsets[imgIdx]));
                    imgIdx++;
                }
                if (mat.NormalMapPng != null)
                {
                    mat.NormalMapPng.CopyTo(glb.AsSpan(binStart + imageOffsets[imgIdx]));
                    imgIdx++;
                }
            }
        }

        if (hasAnimations)
        {
            foreach (var ab in animBuffers)
            {
                WriteAnimationTime(glb, binStart + ab.TimeOffset, ab.Duration, ab.FrameCount);

                foreach (var mt in ab.Tracks)
                {
                    var bind = bones[mt.BoneIndex].ParentSpaceMatrix;
                    DecomposeColumnMajor(bind, out var bindT, out var bindR, out var bindS);

                    WriteComposedTranslations(glb, binStart + mt.TransOffset, mt.Track.Translations, bindT, ab.FrameCount);
                    WriteAnimationRotations(glb, binStart + mt.RotOffset, mt.Track.Rotations, bindR, ab.FrameCount);
                    WriteAnimationScales(glb, binStart + mt.ScaleOffset, mt.Track.Scales, bindS, ab.FrameCount);
                }
            }
        }

        return glb;
    }

    #region Binary Writers

    static void WritePositions(byte[] buf, int offset, TmmVertex[] vertices)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            float px = (float)v.PosX;
            float py = (float)v.PosY;
            float pz = -(float)v.PosZ; // negate Z for RH
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), px); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), py); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), pz); offset += 4;
        }
    }

    /// <summary>
    /// Decodes TBN quaternion once per vertex and writes both normal (Vec3) and tangent (Vec4) data.
    /// </summary>
    static void WriteNormalsAndTangents(byte[] buf, int normalOffset, int tangentOffset, TmmVertex[] vertices)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            var (qx, qy, qz, qw, handedness) = TbnDecoder.QuatFromPacked(v.TbnX, v.TbnY, v.TbnZ);
            var (tangent, _, normal) = TbnDecoder.QuatToTbn(qx, qy, qz, qw, handedness);

            // Normals: negate Z for RH
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(normalOffset), normal.x); normalOffset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(normalOffset), normal.y); normalOffset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(normalOffset), -normal.z); normalOffset += 4;

            // Tangents: negate Z for RH
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(tangentOffset), tangent.x); tangentOffset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(tangentOffset), tangent.y); tangentOffset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(tangentOffset), -tangent.z); tangentOffset += 4;

            // Z-negate (LH->RH) flips the TBN determinant, so tangent.w is inverted:
            //   hand=0 (det=+1 in game) -> det=-1 in glTF -> w = -1
            //   hand=1 (det=-1 in game) -> det=+1 in glTF -> w = +1
            float w = handedness == 0 ? -1.0f : 1.0f;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(tangentOffset), w); tangentOffset += 4;
        }
    }

    static void WriteTexCoords(byte[] buf, int offset, TmmVertex[] vertices, bool flipV = false)
    {
        for (int i = 0; i < vertices.Length; i++)
        {
            var v = vertices[i];
            float u = (float)v.U;
            float vCoord = flipV ? 1.0f - (float)v.V : (float)v.V;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), u); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), vCoord); offset += 4;
        }
    }

    static void WriteIndices(byte[] buf, int offset, ushort[] indices)
    {
        // Reverse triangle winding: for each triangle [i0, i1, i2] -> [i0, i2, i1]
        for (int i = 0; i < indices.Length; i += 3)
        {
            if (i + 2 < indices.Length)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), indices[i]); offset += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), indices[i + 2]); offset += 2;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), indices[i + 1]); offset += 2;
            }
            else
            {
                // Remaining indices that don't form a complete triangle
                for (int j = i; j < indices.Length; j++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(offset), indices[j]); offset += 2;
                }
            }
        }
    }

    static void WriteJoints(byte[] buf, int offset, TmmSkinWeight[] skinWeights)
    {
        for (int i = 0; i < skinWeights.Length; i++)
        {
            var sw = skinWeights[i];
            buf[offset] = sw.BoneIndex0; offset++;
            buf[offset] = sw.BoneIndex1; offset++;
            buf[offset] = sw.BoneIndex2; offset++;
            buf[offset] = sw.BoneIndex3; offset++;
        }
    }

    static void WriteWeights(byte[] buf, int offset, TmmSkinWeight[] skinWeights)
    {
        for (int i = 0; i < skinWeights.Length; i++)
        {
            var sw = skinWeights[i];
            float w0 = sw.Weight0 / 255.0f;
            float w1 = sw.Weight1 / 255.0f;
            float w2 = sw.Weight2 / 255.0f;
            float w3 = sw.Weight3 / 255.0f;

            // Normalize so they sum to 1.0
            float sum = w0 + w1 + w2 + w3;
            if (sum > 0.0f)
            {
                w0 /= sum;
                w1 /= sum;
                w2 /= sum;
                w3 /= sum;
            }
            else
            {
                w0 = 1.0f; // fallback: all weight on first bone
            }

            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), w0); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), w1); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), w2); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), w3); offset += 4;
        }
    }

    static void WriteInverseBindMatrices(byte[] buf, int offset, TmmBone[] bones)
    {
        for (int b = 0; b < bones.Length; b++)
        {
            var ibm = bones[b].InverseBindMatrix;
            for (int j = 0; j < 16; j++)
            {
                float val = ibm[j];
                if (ZNegateMask[j]) val = -val;
                BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), val); offset += 4;
            }
        }
    }

    static void WriteAnimationTime(byte[] buf, int offset, float duration, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            float t = frameCount > 1 ? (i / (float)(frameCount - 1)) * duration : 0f;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), t); offset += 4;
        }
    }

    /// <summary>
    /// Decomposes a column-major 4x4 affine matrix into translation, rotation, scale.
    /// Column-major columns become rows in System.Numerics row-vector Matrix4x4.
    /// </summary>
    static void DecomposeColumnMajor(float[] m, out Vector3 translation, out Quaternion rotation, out Vector3 scale)
    {
        translation = new Vector3(m[12], m[13], m[14]);

        // Extract scale from column lengths
        var col0 = new Vector3(m[0], m[1], m[2]);
        var col1 = new Vector3(m[4], m[5], m[6]);
        var col2 = new Vector3(m[8], m[9], m[10]);
        scale = new Vector3(col0.Length(), col1.Length(), col2.Length());

        // Build normalized rotation matrix
        if (scale.X > 0) col0 /= scale.X;
        if (scale.Y > 0) col1 /= scale.Y;
        if (scale.Z > 0) col2 /= scale.Z;

        // System.Numerics uses row-vector convention (v' = v * M),
        // so column-major columns become rows (transpose)
        var rotMatrix = new Matrix4x4(
            col0.X, col0.Y, col0.Z, 0,
            col1.X, col1.Y, col1.Z, 0,
            col2.X, col2.Y, col2.Z, 0,
            0, 0, 0, 1);
        rotation = Quaternion.CreateFromRotationMatrix(rotMatrix);
    }

    /// <summary>
    /// TMA translations are deltas from bind pose in parent space.
    /// Composes: final_T = bind_T + anim_T, then Z-negate for RH.
    /// </summary>
    static void WriteComposedTranslations(byte[] buf, int offset,
        Vector3[] translations, Vector3 bindT, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            var finalT = bindT + translations[i];
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), finalT.X); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), finalT.Y); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), -finalT.Z); offset += 4;
        }
    }

    /// <summary>
    /// TMA rotation deltas composed with bind pose in bone-local frame.
    /// Game uses conjugated quaternion convention - conj(delta) applied after bind.
    /// Z-mirror for LH->RH: q' = (-x, -y, z, w).
    /// Hemisphere consistency prevents SLERP flipping between frames.
    /// </summary>
    static void WriteAnimationRotations(byte[] buf, int offset,
        Quaternion[] rotations, Quaternion bindR, int frameCount)
    {
        var prev = Quaternion.Identity;
        for (int i = 0; i < frameCount; i++)
        {
            var finalR = Quaternion.Normalize(
                Quaternion.Multiply(bindR, Quaternion.Conjugate(rotations[i])));
            finalR = new Quaternion(-finalR.X, -finalR.Y, finalR.Z, finalR.W);
            if (i > 0 && Quaternion.Dot(prev, finalR) < 0)
                finalR = Quaternion.Negate(finalR);
            prev = finalR;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), finalR.X); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), finalR.Y); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), finalR.Z); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), finalR.W); offset += 4;
        }
    }

    /// <summary>
    /// Writes animation scales (component-wise multiply with bind scale).
    /// Scale is unaffected by Z-mirror.
    /// </summary>
    static void WriteAnimationScales(byte[] buf, int offset,
        Vector3[] scales, Vector3 bindS, int frameCount)
    {
        for (int i = 0; i < frameCount; i++)
        {
            var v = scales[i];
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), bindS.X * v.X); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), bindS.Y * v.Y); offset += 4;
            BinaryPrimitives.WriteSingleLittleEndian(buf.AsSpan(offset), bindS.Z * v.Z); offset += 4;
        }
    }

    #endregion

    #region JSON Builder

    static byte[] BuildJson(
        TmmFile tmm, TmmMeshGroup[] meshGroups, TmmBone[] bones, TmmAttachment[] attachments,
        bool hasSkin, bool hasMaterials, IReadOnlyList<GlbMaterial>? materials,
        int vertexCount, in BufferLayout bl,
        (float[] min, float[] max)[] groupBounds,
        List<AnimBuffer> animBuffers)
    {
        using var ms = new MemoryStream();
        using var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false });

        w.WriteStartObject();

        // asset
        w.WriteStartObject("asset");
        w.WriteString("version", "2.0");
        w.WriteString("generator", "CryBarEditor");
        w.WriteEndObject();

        // scene
        w.WriteNumber("scene", 0);

        // scenes
        w.WriteStartArray("scenes");
        w.WriteStartObject();
        w.WriteStartArray("nodes");
        w.WriteNumberValue(0);
        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();

        // --- Build nodes ---
        // Node 0 = mesh node
        // Nodes 1..N = bone nodes (if any)
        // Nodes N+1..N+A = attachment nodes (empty nodes parented to bones)
        // Nodes N+A+1..N+A+I = impact point nodes (empty nodes at world positions)
        var impactPoints = tmm.AutoAttachInfo?.ManualImpactPoints;
        int impactPointCount = impactPoints?.Length ?? 0;
        int boneNodeStart = 1;
        int attachmentNodeStart = boneNodeStart + bones.Length;
        int impactNodeStart = attachmentNodeStart + attachments.Length;

        w.WriteStartArray("nodes");

        // Node 0: mesh node
        w.WriteStartObject();
        w.WriteNumber("mesh", 0);
        if (hasSkin)
            w.WriteNumber("skin", 0);

        // Children of mesh node: root bones + orphan attachments
        var meshNodeChildren = new List<int>();
        if (hasSkin)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i].ParentId < 0)
                    meshNodeChildren.Add(boneNodeStart + i);
            }
        }
        // Attachments without a valid bone parent are children of the mesh node
        for (int i = 0; i < attachments.Length; i++)
        {
            if (!hasSkin || attachments[i].ParentBoneId < 0 || attachments[i].ParentBoneId >= bones.Length)
                meshNodeChildren.Add(attachmentNodeStart + i);
        }
        // Impact points are children of the mesh node
        for (int i = 0; i < impactPointCount; i++)
            meshNodeChildren.Add(impactNodeStart + i);
        if (meshNodeChildren.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var idx in meshNodeChildren)
                w.WriteNumberValue(idx);
            w.WriteEndArray();
        }
        w.WriteEndObject();

        // Bone nodes
        for (int i = 0; i < bones.Length; i++)
        {
            w.WriteStartObject();
            w.WriteString("name", bones[i].Name);

            // glTF spec: animated nodes MUST use TRS, not matrix.
            // Use TRS when animations are present, matrix otherwise.
            if (animBuffers.Count > 0)
                WriteBoneTrsJson(w, bones[i].ParentSpaceMatrix);
            else
                WriteZNegatedMatrixJson(w, bones[i].ParentSpaceMatrix);

            // Children: child bones + attachments parented to this bone
            var childIndices = new List<int>();
            for (int c = 0; c < bones.Length; c++)
            {
                if (bones[c].ParentId == i)
                    childIndices.Add(boneNodeStart + c);
            }
            for (int a = 0; a < attachments.Length; a++)
            {
                if (attachments[a].ParentBoneId == i)
                    childIndices.Add(attachmentNodeStart + a);
            }
            if (childIndices.Count > 0)
            {
                w.WriteStartArray("children");
                foreach (var idx in childIndices)
                    w.WriteNumberValue(idx);
                w.WriteEndArray();
            }

            w.WriteEndObject();
        }

        // Attachment nodes (Empty objects for VFX/particle attach points)
        for (int i = 0; i < attachments.Length; i++)
        {
            w.WriteStartObject();
            w.WriteString("name", attachments[i].Name);

            // Convert row-major 3×4 attachment transform to column-major 4×4 for glTF
            WriteZNegatedMatrixJson(w, RowMajor3x4ToColumnMajor4x4(attachments[i].AdjustmentTransformMatrix));

            w.WriteEndObject();
        }

        // Impact point nodes (empty objects at world-space positions)
        for (int i = 0; i < impactPointCount; i++)
        {
            var pt = impactPoints![i];
            w.WriteStartObject();
            w.WriteString("name", $"ImpactPoint_{i}");
            // LH->RH: negate Z
            w.WriteStartArray("translation");
            w.WriteNumberValue(pt[0]);
            w.WriteNumberValue(pt[1]);
            w.WriteNumberValue(-pt[2]);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        w.WriteEndArray(); // nodes

        // --- Build buffer views ---
        // Track buffer view indices
        int bvIndex = 0;
        int bvPositions = bvIndex++;
        int bvNormals = bvIndex++;
        int bvTangents = bvIndex++;
        int bvTexcoords = bvIndex++;
        int bvIndices = bvIndex++;
        int bvJoints = -1, bvWeights = -1, bvIbm = -1;
        if (hasSkin)
        {
            bvJoints = bvIndex++;
            bvWeights = bvIndex++;
            bvIbm = bvIndex++;
        }
        var bvImageStart = bvIndex;
        int imageCount = bl.ImageOffsets.Count;
        bvIndex += imageCount;

        w.WriteStartArray("bufferViews");

        WriteBufferView(w, 0, bl.PositionsOffset, bl.PositionsByteLength, 12);
        WriteBufferView(w, 0, bl.NormalsOffset, bl.NormalsByteLength, 12);
        WriteBufferView(w, 0, bl.TangentsOffset, bl.TangentsByteLength, 16);
        WriteBufferView(w, 0, bl.TexcoordsOffset, bl.TexcoordsByteLength, 8);
        WriteBufferView(w, 0, bl.IndicesOffset, bl.IndicesByteLength);

        if (hasSkin)
        {
            WriteBufferView(w, 0, bl.JointsOffset, bl.JointsByteLength, 4);
            WriteBufferView(w, 0, bl.WeightsOffset, bl.WeightsByteLength, 16);
            WriteBufferView(w, 0, bl.IbmOffset, bl.IbmByteLength);
        }

        for (int i = 0; i < imageCount; i++)
            WriteBufferView(w, 0, bl.ImageOffsets[i], bl.ImageLengths[i]);

        // animation buffer views: per animation: time + per track: T, R, S
        int bvAnimStart = bvIndex;
        foreach (var ab in animBuffers)
        {
            WriteBufferView(w, 0, ab.TimeOffset, ab.TimeLen); bvIndex++;
            foreach (var mt in ab.Tracks)
            {
                WriteBufferView(w, 0, mt.TransOffset, mt.TransLen); bvIndex++;
                WriteBufferView(w, 0, mt.RotOffset, mt.RotLen); bvIndex++;
                WriteBufferView(w, 0, mt.ScaleOffset, mt.ScaleLen); bvIndex++;
            }
        }

        w.WriteEndArray(); // bufferViews

        // --- Build accessors ---
        // Per mesh group: POSITION, NORMAL, TANGENT, TEXCOORD_0, INDICES (+ optionally JOINTS_0, WEIGHTS_0)
        // Plus IBM accessor if skinned
        int accessorIndex = 0;

        // Pre-compute per-group accessor indices
        int accessorsPerGroup = 5; // pos, normal, tangent, texcoord, indices
        if (hasSkin) accessorsPerGroup += 2; // joints, weights
        int ibmAccessorIndex = -1;

        w.WriteStartArray("accessors");

        for (int g = 0; g < meshGroups.Length; g++)
        {
            var mg = meshGroups[g];
            int vStart = (int)mg.VertexStart;
            int vCount = (int)mg.VertexCount;
            int iStart = (int)mg.IndexStart;
            int iCount = (int)mg.IndexCount;

            // POSITION accessor (min/max required by glTF spec for all POSITION accessors)
            WriteAccessor(w, bvPositions, vStart * 12, vCount, 5126, "VEC3", groupBounds[g].min, groupBounds[g].max, true);
            accessorIndex++;

            // NORMAL accessor
            WriteAccessor(w, bvNormals, vStart * 12, vCount, 5126, "VEC3");
            accessorIndex++;

            // TANGENT accessor
            WriteAccessor(w, bvTangents, vStart * 16, vCount, 5126, "VEC4");
            accessorIndex++;

            // TEXCOORD_0 accessor
            WriteAccessor(w, bvTexcoords, vStart * 8, vCount, 5126, "VEC2");
            accessorIndex++;

            // INDICES accessor
            WriteAccessor(w, bvIndices, iStart * 2, iCount, 5123, "SCALAR"); // 5123 = UNSIGNED_SHORT
            accessorIndex++;

            if (hasSkin)
            {
                // JOINTS_0 accessor
                WriteAccessor(w, bvJoints, vStart * 4, vCount, 5121, "VEC4"); // 5121 = UNSIGNED_BYTE
                accessorIndex++;

                // WEIGHTS_0 accessor
                WriteAccessor(w, bvWeights, vStart * 16, vCount, 5126, "VEC4");
                accessorIndex++;
            }
        }

        // IBM accessor (one for all bones)
        if (hasSkin)
        {
            ibmAccessorIndex = accessorIndex;
            WriteAccessor(w, bvIbm, 0, bones.Length, 5126, "MAT4");
            accessorIndex++;
        }

        // animation accessors: per animation: time + per track: T, R, S
        int animAccessorStart = accessorIndex;
        int animBvCursor = bvAnimStart;
        foreach (var ab in animBuffers)
        {
            // time accessor with min/max
            WriteAccessor(w, animBvCursor, 0, ab.FrameCount, 5126, "SCALAR",
                [0f], [ab.Duration], true);
            accessorIndex++; animBvCursor++;

            foreach (var mt in ab.Tracks)
            {
                WriteAccessor(w, animBvCursor, 0, ab.FrameCount, 5126, "VEC3"); // translation
                accessorIndex++; animBvCursor++;
                WriteAccessor(w, animBvCursor, 0, ab.FrameCount, 5126, "VEC4"); // rotation
                accessorIndex++; animBvCursor++;
                WriteAccessor(w, animBvCursor, 0, ab.FrameCount, 5126, "VEC3"); // scale
                accessorIndex++; animBvCursor++;
            }
        }

        w.WriteEndArray(); // accessors

        // --- meshes ---
        w.WriteStartArray("meshes");
        w.WriteStartObject();
        w.WriteStartArray("primitives");

        for (int g = 0; g < meshGroups.Length; g++)
        {
            int baseAccessor = g * accessorsPerGroup;
            w.WriteStartObject();

            w.WriteStartObject("attributes");
            w.WriteNumber("POSITION", baseAccessor);
            w.WriteNumber("NORMAL", baseAccessor + 1);
            w.WriteNumber("TANGENT", baseAccessor + 2);
            w.WriteNumber("TEXCOORD_0", baseAccessor + 3);
            if (hasSkin)
            {
                w.WriteNumber("JOINTS_0", baseAccessor + 5);
                w.WriteNumber("WEIGHTS_0", baseAccessor + 6);
            }
            w.WriteEndObject(); // attributes

            w.WriteNumber("indices", baseAccessor + 4);
            w.WriteNumber("mode", 4); // TRIANGLES

            if (hasMaterials && meshGroups[g].MaterialIndex < materials!.Count)
                w.WriteNumber("material", (int)meshGroups[g].MaterialIndex);

            w.WriteEndObject();
        }

        w.WriteEndArray(); // primitives
        w.WriteEndObject();
        w.WriteEndArray(); // meshes

        // --- materials, textures, images, samplers ---
        if (hasMaterials)
        {
            int textureIndex = 0;

            // Build materials
            w.WriteStartArray("materials");
            foreach (var mat in materials!)
            {
                w.WriteStartObject();
                w.WriteString("name", mat.Name);

                w.WriteStartObject("pbrMetallicRoughness");
                w.WriteNumber("metallicFactor", 0);
                w.WriteNumber("roughnessFactor", 1);
                if (mat.BaseColorPng != null)
                {
                    w.WriteStartObject("baseColorTexture");
                    w.WriteNumber("index", textureIndex);
                    w.WriteEndObject();
                    textureIndex++;
                }
                w.WriteEndObject(); // pbrMetallicRoughness

                if (mat.NormalMapPng != null)
                {
                    w.WriteStartObject("normalTexture");
                    w.WriteNumber("index", textureIndex);
                    w.WriteEndObject();
                    textureIndex++;
                }

                w.WriteEndObject();
            }
            w.WriteEndArray(); // materials

            // Build textures
            int totalTextures = textureIndex;
            if (totalTextures > 0)
            {
                w.WriteStartArray("textures");
                for (int t = 0; t < totalTextures; t++)
                {
                    w.WriteStartObject();
                    w.WriteNumber("source", t);
                    w.WriteNumber("sampler", 0);
                    w.WriteEndObject();
                }
                w.WriteEndArray(); // textures

                // Build images
                w.WriteStartArray("images");
                int bvImg = bvImageStart;
                foreach (var mat in materials!)
                {
                    if (mat.BaseColorPng != null)
                    {
                        w.WriteStartObject();
                        w.WriteString("mimeType", "image/png");
                        w.WriteNumber("bufferView", bvImg);
                        w.WriteEndObject();
                        bvImg++;
                    }
                    if (mat.NormalMapPng != null)
                    {
                        w.WriteStartObject();
                        w.WriteString("mimeType", "image/png");
                        w.WriteNumber("bufferView", bvImg);
                        w.WriteEndObject();
                        bvImg++;
                    }
                }
                w.WriteEndArray(); // images

                // Samplers
                w.WriteStartArray("samplers");
                w.WriteStartObject();
                w.WriteEndObject(); // default sampler
                w.WriteEndArray();
            }
        }

        // --- skins ---
        if (hasSkin)
        {
            w.WriteStartArray("skins");
            w.WriteStartObject();

            w.WriteStartArray("joints");
            for (int i = 0; i < bones.Length; i++)
                w.WriteNumberValue(boneNodeStart + i);
            w.WriteEndArray();

            w.WriteNumber("inverseBindMatrices", ibmAccessorIndex);

            // skeleton: first root bone
            for (int i = 0; i < bones.Length; i++)
            {
                if (bones[i].ParentId < 0)
                {
                    w.WriteNumber("skeleton", boneNodeStart + i);
                    break;
                }
            }

            w.WriteEndObject();
            w.WriteEndArray(); // skins
        }

        // --- animations ---
        if (animBuffers.Count > 0)
        {
            w.WriteStartArray("animations");

            int accCursor = animAccessorStart;
            foreach (var ab in animBuffers)
            {
                w.WriteStartObject();
                w.WriteString("name", ab.Name);

                int timeAccessor = accCursor++;

                // samplers: 3 per matched track (T, R, S)
                w.WriteStartArray("samplers");
                foreach (var mt in ab.Tracks)
                {
                    // translation sampler
                    w.WriteStartObject();
                    w.WriteNumber("input", timeAccessor);
                    w.WriteNumber("output", accCursor++);
                    w.WriteString("interpolation", "LINEAR");
                    w.WriteEndObject();

                    // rotation sampler
                    w.WriteStartObject();
                    w.WriteNumber("input", timeAccessor);
                    w.WriteNumber("output", accCursor++);
                    w.WriteString("interpolation", "LINEAR");
                    w.WriteEndObject();

                    // scale sampler
                    w.WriteStartObject();
                    w.WriteNumber("input", timeAccessor);
                    w.WriteNumber("output", accCursor++);
                    w.WriteString("interpolation", "LINEAR");
                    w.WriteEndObject();
                }
                w.WriteEndArray(); // samplers

                // channels: 3 per matched track
                w.WriteStartArray("channels");
                int samplerBase = 0;
                foreach (var mt in ab.Tracks)
                {
                    int nodeIdx = boneNodeStart + mt.BoneIndex;

                    // translation channel
                    w.WriteStartObject();
                    w.WriteNumber("sampler", samplerBase);
                    w.WriteStartObject("target");
                    w.WriteNumber("node", nodeIdx);
                    w.WriteString("path", "translation");
                    w.WriteEndObject();
                    w.WriteEndObject();

                    // rotation channel
                    w.WriteStartObject();
                    w.WriteNumber("sampler", samplerBase + 1);
                    w.WriteStartObject("target");
                    w.WriteNumber("node", nodeIdx);
                    w.WriteString("path", "rotation");
                    w.WriteEndObject();
                    w.WriteEndObject();

                    // scale channel
                    w.WriteStartObject();
                    w.WriteNumber("sampler", samplerBase + 2);
                    w.WriteStartObject("target");
                    w.WriteNumber("node", nodeIdx);
                    w.WriteString("path", "scale");
                    w.WriteEndObject();
                    w.WriteEndObject();

                    samplerBase += 3;
                }
                w.WriteEndArray(); // channels

                w.WriteEndObject(); // animation
            }

            w.WriteEndArray(); // animations
        }

        // --- buffers ---
        w.WriteStartArray("buffers");
        w.WriteStartObject();
        w.WriteNumber("byteLength", bl.TotalBinLength);
        w.WriteEndObject();
        w.WriteEndArray();

        w.WriteEndObject(); // root
        w.Flush();

        return ms.ToArray();
    }

    static void WriteBufferView(Utf8JsonWriter w, int buffer, int byteOffset, int byteLength, int? byteStride = null)
    {
        w.WriteStartObject();
        w.WriteNumber("buffer", buffer);
        w.WriteNumber("byteOffset", byteOffset);
        w.WriteNumber("byteLength", byteLength);
        if (byteStride.HasValue)
            w.WriteNumber("byteStride", byteStride.Value);
        w.WriteEndObject();
    }

    static void WriteAccessor(Utf8JsonWriter w, int bufferView, int byteOffset, int count,
        int componentType, string type, float[]? min = null, float[]? max = null, bool writeMinMax = false)
    {
        w.WriteStartObject();
        w.WriteNumber("bufferView", bufferView);
        w.WriteNumber("byteOffset", byteOffset);
        w.WriteNumber("componentType", componentType);
        w.WriteNumber("count", count);
        w.WriteString("type", type);
        if (writeMinMax && min != null && max != null)
        {
            w.WriteStartArray("min");
            foreach (var v in min) w.WriteNumberValue(v);
            w.WriteEndArray();
            w.WriteStartArray("max");
            foreach (var v in max) w.WriteNumberValue(v);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Decomposes a column-major parent-space matrix to TRS and writes as glTF
    /// "translation", "rotation", "scale" properties with Z-mirror for LH->RH.
    /// Required for nodes targeted by animation (glTF spec forbids "matrix" on animated nodes).
    /// </summary>
    static void WriteBoneTrsJson(Utf8JsonWriter w, float[] matrix)
    {
        DecomposeColumnMajor(matrix, out var t, out var r, out var s);

        w.WriteStartArray("translation");
        w.WriteNumberValue(t.X);
        w.WriteNumberValue(t.Y);
        w.WriteNumberValue(-t.Z);
        w.WriteEndArray();

        w.WriteStartArray("rotation");
        w.WriteNumberValue(-r.X);
        w.WriteNumberValue(-r.Y);
        w.WriteNumberValue(r.Z);
        w.WriteNumberValue(r.W);
        w.WriteEndArray();

        w.WriteStartArray("scale");
        w.WriteNumberValue(s.X);
        w.WriteNumberValue(s.Y);
        w.WriteNumberValue(s.Z);
        w.WriteEndArray();
    }

    /// <summary>
    /// Writes a column-major 4×4 matrix as a glTF "matrix" JSON array,
    /// negating the Z-axis components for LH->RH conversion.
    /// </summary>
    static void WriteZNegatedMatrixJson(Utf8JsonWriter w, float[] matrix)
    {
        w.WriteStartArray("matrix");
        for (int j = 0; j < 16; j++)
        {
            float val = matrix[j];
            if (ZNegateMask[j]) val = -val;
            w.WriteNumberValue(val);
        }
        w.WriteEndArray();
    }

    /// <summary>
    /// Converts a 12-float row-major 3×4 matrix (3 rows of 4 values, as used by
    /// attachment and main transforms) to a 16-float column-major 4×4 matrix for glTF.
    /// The implicit 4th row [0, 0, 0, 1] is appended.
    /// </summary>
    static float[] RowMajor3x4ToColumnMajor4x4(float[] tm)
    {
        return [
            tm[0], tm[4], tm[8],  0,
            tm[1], tm[5], tm[9],  0,
            tm[2], tm[6], tm[10], 0,
            tm[3], tm[7], tm[11], 1
        ];
    }

    static int Align4(int value) => (value + 3) & ~3;

    static void ComputePositionBounds(TmmVertex[] vertices, int start, int count, out float[] min, out float[] max)
    {
        min = [float.MaxValue, float.MaxValue, float.MaxValue];
        max = [float.MinValue, float.MinValue, float.MinValue];

        int end = start + count;
        for (int i = start; i < end; i++)
        {
            float px = (float)vertices[i].PosX;
            float py = (float)vertices[i].PosY;
            float pz = -(float)vertices[i].PosZ; // negated Z for RH

            if (px < min[0]) min[0] = px;
            if (py < min[1]) min[1] = py;
            if (pz < min[2]) min[2] = pz;
            if (px > max[0]) max[0] = px;
            if (py > max[1]) max[1] = py;
            if (pz > max[2]) max[2] = pz;
        }
    }

    #endregion
}
