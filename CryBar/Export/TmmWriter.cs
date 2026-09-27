using System.Buffers.Binary;
using System.Numerics;
using CryBar.TMM;
using static CryBar.Export.TmmWriteHelpers;

namespace CryBar.Export;

public static class TmmWriter
{
    public static (byte[] Tmm, byte[] TmmData, IReadOnlyList<string> Warnings) Write(GlbModel model)
    {
        var warnings = new List<string>();

        for (int p = 0; p < model.Mesh.Primitives.Length; p++)
            ValidatePrimitive(model.Mesh.Primitives[p], p);

        // Build .tmm.data buffer first so we know the offsets for the header.
        var data = BuildDataBuffer(model, out var dataLayout);

        var tmm = BuildTmmHeader(model, dataLayout, warnings);

        var validate = new TmmFile(tmm);
        if (!validate.Parsed)
            throw new InvalidOperationException("TmmWriter produced output that fails parse (writer bug).");

        return (tmm, data, warnings);
    }

    const int MaxVerticesPerPrimitive = ushort.MaxValue + 1;

    static void ValidatePrimitive(GlbMeshPrimitive prim, int primIdx)
    {
        string? error = MeshPrimitiveValidator.CheckLengths(prim.Positions, prim.Normals, prim.Tangents, prim.TexCoords,
            prim.JointIndices, prim.JointWeights, prim.Indices.Length);
        if (error != null)
            throw PrimitiveError(prim, primIdx, error);

        int vc = prim.Positions.Length / 3;
        if (vc > MaxVerticesPerPrimitive)
            throw new InvalidDataException(
                $"Primitive {primIdx} ('{prim.MaterialName}') has {vc} vertices; TMM uses 16-bit indices so a mesh group can hold at most {MaxVerticesPerPrimitive}. Split the mesh (or its material) into smaller parts.");
    }

    static InvalidDataException PrimitiveError(GlbMeshPrimitive prim, int primIdx, string error) =>
        new($"Primitive {primIdx} ('{prim.MaterialName}'): {error}.");

    // Offsets and byte lengths for each section inside the .tmm.data buffer.
    readonly struct DataLayout(
        uint vertStart, uint vertBytes,
        uint idxStart, uint idxBytes,
        uint weightStart, uint weightBytes,
        uint heightStart, uint heightBytes)
    {
        public readonly uint VertStart    = vertStart;
        public readonly uint VertBytes    = vertBytes;
        public readonly uint IdxStart     = idxStart;
        public readonly uint IdxBytes     = idxBytes;
        public readonly uint WeightStart  = weightStart;
        public readonly uint WeightBytes  = weightBytes;
        public readonly uint HeightStart  = heightStart;
        public readonly uint HeightBytes  = heightBytes;
    }

    static byte[] BuildDataBuffer(GlbModel model, out DataLayout layout)
    {
        var primitives = model.Mesh.Primitives;

        int totalVerts = 0;
        int totalIdxCount = 0;
        foreach (var prim in primitives)
        {
            totalVerts += prim.Positions.Length / 3;
            totalIdxCount += prim.Indices.Length;
        }

        bool hasSkin = model.Bones is { Length: > 0 } &&
                       primitives.Any(p => p.JointIndices != null && p.JointWeights != null);

        uint vertBytes   = (uint)(totalVerts    * TmmVertex.SizeInBytes);
        uint idxBytes    = (uint)(totalIdxCount * 2);
        uint weightBytes = hasSkin ? (uint)(totalVerts * TmmSkinWeight.SizeInBytes) : 0u;
        uint heightBytes = (uint)(totalVerts    * 2);

        uint vertStart   = 0;
        uint idxStart    = vertStart   + vertBytes;
        uint weightStart = idxStart    + idxBytes;
        uint heightStart = weightStart + weightBytes;
        uint totalBytes  = heightStart + heightBytes;

        var buf = new byte[totalBytes];

        int vOff = (int)vertStart;
        int iOff = (int)idxStart;
        int wOff = (int)weightStart;
        int hOff = (int)heightStart;

        for (int p = 0; p < primitives.Length; p++)
        {
            var prim = primitives[p];
            int vc = prim.Positions.Length / 3;

            // Must run before ShouldFlipTangentWForPrimitive, which dereferences indices unchecked.
            // Triangle winding: reverse [i0, i1, i2] -> [i0, i2, i1] (RH -> LH)
            var idx = prim.Indices;
            for (int t = 0; t < idx.Length; t += 3)
            {
                uint i0 = idx[t], i1 = idx[t + 1], i2 = idx[t + 2];
                if (i0 >= (uint)vc || i1 >= (uint)vc || i2 >= (uint)vc)
                    throw PrimitiveError(prim, p, MeshPrimitiveValidator.CheckIndexRange(idx, vc)!);

                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(iOff),     (ushort)i0);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(iOff + 2), (ushort)i2);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(iOff + 4), (ushort)i1);
                iOff += 6;
            }

            // Blender's glTF exporter writes tangent.w with the opposite sign of MikkTSpace
            // for our X-mirrored meshes; without correction the per-vertex handedness bit
            // ends up inverted and normal-map sampling reads the wrong bitangent direction.
            bool flipTw = ShouldFlipTangentWForPrimitive(prim);
            for (int i = 0; i < vc; i++)
            {
                float px = prim.Positions[i * 3];
                float py = prim.Positions[i * 3 + 1];
                float pz = prim.Positions[i * 3 + 2];

                // X-negate: convert glTF Y-up RH -> TMM Y-up LH
                BinaryPrimitives.WriteHalfLittleEndian(buf.AsSpan(vOff),     (Half)(-px));
                BinaryPrimitives.WriteHalfLittleEndian(buf.AsSpan(vOff + 2), (Half)py);
                BinaryPrimitives.WriteHalfLittleEndian(buf.AsSpan(vOff + 4), (Half)pz);

                float u  = prim.TexCoords[i * 2];
                float v  = prim.TexCoords[i * 2 + 1];
                // V passes through unchanged: GlbExporter does not flip on export, so the round-trip
                // to game V (which python originally Blender-flipped via 1-V) is already encoded.
                BinaryPrimitives.WriteHalfLittleEndian(buf.AsSpan(vOff + 6), (Half)u);
                BinaryPrimitives.WriteHalfLittleEndian(buf.AsSpan(vOff + 8), (Half)v);

                float nx = prim.Normals[i * 3];
                float ny = prim.Normals[i * 3 + 1];
                float nz = prim.Normals[i * 3 + 2];

                float tx = prim.Tangents[i * 4];
                float ty = prim.Tangents[i * 4 + 1];
                float tz = prim.Tangents[i * 4 + 2];
                float tw = prim.Tangents[i * 4 + 3];
                if (flipTw) tw = -tw;

                var (px16, py16, pz16) = PackTbn(nx, ny, nz, tx, ty, tz, tw);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(vOff + 10), px16);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(vOff + 12), py16);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(vOff + 14), pz16);

                // Height buffer: game Y is the height dimension (Y-up coordinate)
                BinaryPrimitives.WriteHalfLittleEndian(buf.AsSpan(hOff), (Half)py);

                vOff += TmmVertex.SizeInBytes;
                hOff += 2;
            }

            if (hasSkin)
                WritePrimitiveSkinWeights(buf, ref wOff, prim);
        }

        layout = new DataLayout(vertStart, vertBytes, idxStart, idxBytes, weightStart, weightBytes, heightStart, heightBytes);
        return buf;
    }

    static void WritePrimitiveSkinWeights(byte[] buf, ref int wOff, GlbMeshPrimitive prim)
    {
        int vc = prim.Positions.Length / 3;
        var joints  = prim.JointIndices;
        var weights = prim.JointWeights;

        Span<(float w, byte b)> pairs  = stackalloc (float, byte)[4];
        Span<byte>              wBytes = stackalloc byte[4];

        for (int i = 0; i < vc; i++)
        {
            // Keep all 4 (weight, bone) pairs verbatim. Vanilla preserves bone indices
            // even where weight=0, and the engine appears to read them for non-skinning
            // purposes (material/lookup), so dropping zero-weight pairs visibly breaks
            // textures on units like the chimera.
            for (int s = 0; s < 4; s++)
            {
                float wt = weights != null ? weights[i * 4 + s] : 0f;
                wt = float.IsNaN(wt) ? 0f : Math.Clamp(wt, 0f, 1f);
                byte  bi = joints  != null ? joints [i * 4 + s] : (byte)0;
                pairs[s] = (wt, bi);
            }

            // Sort ascending by weight (min..max). Zero-weight entries sort first,
            // so the dominant bone ends up at slot 3.
            for (int a = 1; a < 4; a++)
            {
                var tmp = pairs[a];
                int b = a - 1;
                while (b >= 0 && pairs[b].w > tmp.w) { pairs[b + 1] = pairs[b]; b--; }
                pairs[b + 1] = tmp;
            }

            QuantizeWeights(pairs, wBytes);

            for (int s = 0; s < 4; s++)
            {
                buf[wOff + s] = wBytes[s];
                buf[wOff + 4 + s] = pairs[s].b;
            }

            wOff += TmmSkinWeight.SizeInBytes;
        }
    }

    // Largest-remainder quantization so bytes sum to exactly 255; ties go to the higher slot to keep ascending order.
    internal static void QuantizeWeights(ReadOnlySpan<(float w, byte b)> pairs, Span<byte> wBytes)
    {
        wBytes.Clear();

        float sum = 0f;
        for (int s = 0; s < 4; s++) sum += pairs[s].w;
        if (!(sum > 0f)) return;

        Span<float> frac = stackalloc float[4];
        int total = 0;
        for (int s = 0; s < 4; s++)
        {
            float scaled = pairs[s].w / sum * 255f;
            int q = (int)MathF.Floor(scaled);
            wBytes[s] = (byte)q;
            frac[s] = scaled - q;
            total += q;
        }

        for (int remaining = 255 - total; remaining > 0; remaining--)
        {
            int best = 3;
            for (int s = 2; s >= 0; s--)
                if (frac[s] > frac[best]) best = s;

            wBytes[best]++;
            frac[best] = float.NegativeInfinity;
        }
    }

    // Packs a glTF-space (Y-up RH) normal/tangent/bitangentSign into three u16 TBN values.
    // Converts to game space (Y-up LH) by negating X, then runs Shepperd quaternion extraction.
    static (ushort X, ushort Y, ushort Z) PackTbn(
        float nx, float ny, float nz,
        float tx, float ty, float tz,
        float tw)
    {
        // Convert to game space: negate X (inverse of what GlbExporter does)
        float ngx = -nx, ngy = ny, ngz = nz;
        float tgx = -tx, tgy = ty, tgz = tz;

        // glTF tangent.w: -1 = non-mirrored (handedness=0), +1 = mirrored (handedness=1)
        int handedness = tw > 0.0f ? 1 : 0;

        // Game-space B = N x T. The TmmDecoder builds matrix [T | B | N] where col1 is exactly
        // this cross product (verified against vanilla samples). Using T x N would feed Shepperd
        // a left-handed frame and produce a quaternion whose decoded normal points the wrong way.
        // For mirrored faces (handedness=1) the cross still produces the "real" B; the handedness
        // bit signals the decoder to flip B back at unpack time.
        float bgx = ngy * tgz - ngz * tgy;
        float bgy = ngz * tgx - ngx * tgz;
        float bgz = ngx * tgy - ngy * tgx;

        // Build proper-rotation matrix [T_g | B_g | N_g] for Shepperd.
        // Mirrored faces use -B so the matrix has det=+1 (right-handed frame),
        // with the handedness bit signaling the decoder to flip B back.
        // sb * B_g: sb=+1 for handedness=0, sb=-1 for handedness=1
        // The cross product above already gives B_g before the sb flip.
        // When handedness=1, col1 = sb*B_g = -1 * (-cross(T,N)) = cross(T,N),
        // and cross(T,N) = bgx/bgy/bgz as computed. So col1 = bgx/bgy/bgz always.
        float m00 = tgx, m10 = tgy, m20 = tgz; // col 0 = T_g
        float m01 = bgx, m11 = bgy, m21 = bgz; // col 1 = sb*B_g
        float m02 = ngx, m12 = ngy, m22 = ngz; // col 2 = N_g

        // Shepperd quaternion extraction
        float trace = m00 + m11 + m22;
        float x, y, z, w;
        if (trace > 0.0f)
        {
            float s = 0.5f / MathF.Sqrt(trace + 1.0f);
            w = 0.25f / s;
            x = (m21 - m12) * s;
            y = (m02 - m20) * s;
            z = (m10 - m01) * s;
        }
        else if (m00 > m11 && m00 > m22)
        {
            float s = 2.0f * MathF.Sqrt(1.0f + m00 - m11 - m22);
            x = 0.25f * s; y = (m01 + m10) / s; z = (m02 + m20) / s; w = (m21 - m12) / s;
        }
        else if (m11 > m22)
        {
            float s = 2.0f * MathF.Sqrt(1.0f + m11 - m00 - m22);
            x = (m01 + m10) / s; y = 0.25f * s; z = (m12 + m21) / s; w = (m02 - m20) / s;
        }
        else
        {
            float s = 2.0f * MathF.Sqrt(1.0f + m22 - m00 - m11);
            x = (m02 + m20) / s; y = (m12 + m21) / s; z = 0.25f * s; w = (m10 - m01) / s;
        }

        float mag = MathF.Sqrt(x * x + y * y + z * z + w * w);
        if (mag > 0.0f) { x /= mag; y /= mag; z /= mag; w /= mag; }

        // Decoder reconstructs w = +sqrt(...), so store the w >= 0 representative.
        if (w < 0.0f) { x = -x; y = -y; z = -z; w = -w; }

        ushort px = (ushort)(TbnDecoder.FloatToU15(x) | (handedness != 0 ? 0x8000 : 0));
        ushort py = (ushort)TbnDecoder.FloatToU15(y);
        ushort pz = (ushort)TbnDecoder.FloatToU15(z);
        return (px, py, pz);
    }

    // Compares each input tangent.w against the spec-compliant w that MikkTSpace would derive
    // from positions, normals and UVs. Returns true iff a strict majority of vertices disagree,
    // signalling that the entire primitive's w convention is inverted and must be flipped.
    static bool ShouldFlipTangentWForPrimitive(GlbMeshPrimitive prim)
    {
        var spec = MikkTSpace.ComputeTangents(prim.Positions, prim.Normals, prim.TexCoords, prim.Indices);
        int vc = prim.Positions.Length / 3;
        int agree = 0, disagree = 0;
        for (int i = 0; i < vc; i++)
        {
            float wGlb  = prim.Tangents[i * 4 + 3];
            float wSpec = spec[i * 4 + 3];
            if (MathF.Sign(wGlb) == MathF.Sign(wSpec)) agree++;
            else disagree++;
        }
        return disagree > agree;
    }

    static byte[] BuildTmmHeader(GlbModel model, in DataLayout dl, List<string> warnings)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write((byte)0x42); w.Write((byte)0x54); w.Write((byte)0x4D); w.Write((byte)0x4D);
        w.Write(37u); // version

        // DP block (empty)
        w.Write((byte)0x44); w.Write((byte)0x50);
        w.Write(4);   // blockByteLength
        w.Write(0u);  // numImportNames

        var (bboxMin, bboxMax) = ComputeBbox(model);
        WriteBoundingBoxes(w, model, bboxMin, bboxMax);

        // Bounds radius: prefer the original (round-tripped via extras), else compute the
        // max distance from origin over all vertex positions. Engine uses this for
        // unit-selection footprint.
        float radius = model.Extras?.Tmm.BoundsRadius ?? 0f;
        if (radius <= 0f) radius = ComputeBoundsRadius(model);
        w.Write(radius);

        int meshGroupCount   = model.Mesh.Primitives.Length;
        int materialCount    = model.Materials.Length;
        int submodelCount    = model.Extras?.Tmm.Submodels.Length ?? 1;
        int boneCount        = model.Bones?.Length ?? 0;
        int attachmentCount  = model.Attachments?.Length ?? 0;

        int vertexCount = 0;
        int triVertCount = 0;
        foreach (var prim in model.Mesh.Primitives)
        {
            vertexCount  += prim.Positions.Length / 3;
            triVertCount += prim.Indices.Length;
        }

        w.Write((uint)meshGroupCount);
        w.Write((uint)materialCount);
        w.Write((uint)submodelCount);
        w.Write((uint)boneCount);
        w.Write(0u); // SharedAnimationBucketCount
        w.Write((uint)attachmentCount);
        w.Write((uint)vertexCount);
        w.Write((uint)triVertCount);

        // Data block layout: 7 pairs of (start, byteLength)
        // Order: vertices, indices, weights, destruction, color, heights, speedtree
        w.Write(dl.VertStart);    w.Write(dl.VertBytes);
        w.Write(dl.IdxStart);     w.Write(dl.IdxBytes);
        w.Write(dl.WeightStart);  w.Write(dl.WeightBytes);
        w.Write(0u);              w.Write(0u);  // destruction buffer
        w.Write(0u);              w.Write(0u);  // color buffer
        w.Write(dl.HeightStart);  w.Write(dl.HeightBytes);
        w.Write(0u);              w.Write(0u);  // speedtree buffer

        w.Write((byte)(model.Extras?.Tmm.TerrainEmb == true ? 1 : 0));
        w.Write((byte)(model.Extras?.Tmm.Raytracing == true ? 1 : 0));

        var mm = model.Extras?.Tmm.MainMatrix;
        if (mm is null || mm.Length < 12) mm = IdentityMatrix4x3();
        for (int i = 0; i < 12; i++) w.Write(mm[i]);

        // Attachment records
        if (model.Attachments is { Length: > 0 })
            WriteAttachments(w, model);

        // Lossy-section warnings
        foreach (var section in model.Extras?.Tmm.LossySections ?? [])
            warnings.Add($"Source had {section} data; dropped on re-import (v1 limitation)");

        // Partial extras (HasFullTmmBlock=false) means only main_matrix / impact_points
        // were recovered from node tags; bbox/auto_attach are still defaulted.
        if (model.Extras == null || !model.Extras.HasFullTmmBlock)
        {
            var mainMat = model.Extras?.Tmm.MainMatrix;
            if (mainMat is not { Length: 16 } || MatrixDecomp.ColMajorToMatrix(mainMat).IsIdentity)
                warnings.Add("No main_matrix in source GLB; defaulted to identity. Model may appear at wrong absolute scale in-game (relative bone motion correct).");
            if (model.Bones is { Length: > 0 })
            {
                warnings.Add("No extended_bbox in source GLB; using bbox * 3 heuristic. Animation may visibly clip near edges of unit footprint for asymmetric models.");
                warnings.Add("No auto_attach data in source GLB; defaulted to empty. Corpse-attach and impact-points behavior unavailable.");
            }
        }

        // Mesh groups: 24 bytes each (6 x uint32)
        uint vOffset = 0, iOffset = 0;
        for (int g = 0; g < model.Mesh.Primitives.Length; g++)
        {
            var prim = model.Mesh.Primitives[g];
            uint vc = (uint)(prim.Positions.Length / 3);
            uint ic = (uint)prim.Indices.Length;

            uint matIdx = FindMaterialIndex(model.Materials, prim.MaterialName, warnings);

            w.Write(vOffset);
            w.Write(iOffset);
            w.Write(vc);
            w.Write(ic);
            w.Write(matIdx);
            w.Write(1u); // SubmodelMask: index 1 = "default" (matches Python: always 1)

            vOffset += vc;
            iOffset += ic;
        }

        // Materials
        foreach (var mat in model.Materials)
            WriteUtf16String(w, mat.Name);

        // Submodels
        var submodels = model.Extras?.Tmm.Submodels;
        if (submodels != null && submodels.Length > 0)
        {
            foreach (var s in submodels)
                WriteUtf16String(w, s);
        }
        else
        {
            WriteUtf16String(w, "default");
        }

        if (model.Bones is { Length: > 0 })
            WriteBones(w, model);

        WriteTrailingSections(w, model);

        return ms.ToArray();
    }

    static uint FindMaterialIndex(GlbMaterial[] materials, string name, List<string> warnings)
    {
        for (int i = 0; i < materials.Length; i++)
        {
            if (materials[i].Name == name) return (uint)i;
        }
        if (materials.Length > 0) return 0;
        warnings.Add($"Material '{name}' not found");
        return 0;
    }

    static void WriteBoundingBoxes(BinaryWriter w, GlbModel model, Vector3 min, Vector3 max)
    {
        w.Write(min.X); w.Write(min.Y); w.Write(min.Z);
        w.Write(max.X); w.Write(max.Y); w.Write(max.Z);

        var (eMin, eMax) = ComputeExtendedBbox(model, min, max);
        w.Write(eMin.X); w.Write(eMin.Y); w.Write(eMin.Z);
        w.Write(eMax.X); w.Write(eMax.Y); w.Write(eMax.Z);
    }

    static (Vector3 Min, Vector3 Max) ComputeBbox(GlbModel model)
    {
        if (model.Mesh.Primitives.Length == 0)
            return (Vector3.Zero, Vector3.Zero);

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var prim in model.Mesh.Primitives)
        {
            for (int i = 0; i < prim.Positions.Length; i += 3)
            {
                var p = new Vector3(prim.Positions[i], prim.Positions[i + 1], prim.Positions[i + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        // Vertex stream is X-negated on write (glTF Y-up RH -> game Y-up LH).
        // Bbox must match game space, so swap X and negate.
        return (new Vector3(-max.X, min.Y, min.Z), new Vector3(-min.X, max.Y, max.Z));
    }

    static float ComputeBoundsRadius(GlbModel model)
    {
        float maxSq = 0f;
        foreach (var prim in model.Mesh.Primitives)
        {
            var pos = prim.Positions;
            for (int i = 0; i < pos.Length; i += 3)
            {
                // X is negated on write; magnitude is the same so we don't need to flip here.
                float x = pos[i], y = pos[i + 1], z = pos[i + 2];
                float dsq = x * x + y * y + z * z;
                if (dsq > maxSq) maxSq = dsq;
            }
        }
        return MathF.Sqrt(maxSq);
    }

    static (Vector3 Min, Vector3 Max) ComputeExtendedBbox(GlbModel model, Vector3 bboxMin, Vector3 bboxMax)
    {
        if (model.Extras != null && model.Extras.Tmm.ExtendedBbox.Length == 6)
        {
            var b = model.Extras.Tmm.ExtendedBbox;
            return (new Vector3(b[0], b[1], b[2]),
                    new Vector3(b[3], b[4], b[5]));
        }
        bool skinned = model.Bones is { Length: > 0 };
        if (skinned) return (bboxMin * 3, bboxMax * 3);
        return (bboxMin, bboxMax);
    }

    static float[] IdentityMatrix4x3() =>
        [1, 0, 0, 0,  0, 1, 0, 0,  0, 0, 1, 0];

    static void WriteBones(BinaryWriter w, GlbModel model)
    {
        var bones = model.Bones!;

        var worldMatrices = MatrixDecomp.ComputeBoneWorldMatrices(bones);

        var collisions = model.Extras?.Tmm.BoneCollisions ?? [];
        bool hasCollisions = collisions.Length == bones.Length * 4;

        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            WriteUtf16String(w, bone.Name);
            w.Write(bone.ParentIndex);

            // Collision offset (XYZ) + radius - sourced from extras when available, else defaults.
            if (hasCollisions)
            {
                w.Write(collisions[i * 4]);
                w.Write(collisions[i * 4 + 1]);
                w.Write(collisions[i * 4 + 2]);
                w.Write(collisions[i * 4 + 3]);
            }
            else
            {
                w.Write(0f); w.Write(0f); w.Write(0f);
                w.Write(0.5f);
            }

            var parentSpaceMat = MatrixDecomp.ColMajorToMatrix(bone.LocalMatrix);
            var worldMat       = worldMatrices[i];
            var invBindMat     = MatrixDecomp.ColMajorToMatrix(bone.InverseBindMatrix);

            // Three 4x4 matrices, each with F*M*F applied (F = diag(-1,1,1,1): flip X axis)
            WriteMatrix4x4Fmf(w, parentSpaceMat);
            WriteMatrix4x4Fmf(w, worldMat);
            WriteMatrix4x4Fmf(w, invBindMat);
        }
    }

    static void WriteAttachments(BinaryWriter w, GlbModel model)
    {
        var attachments = model.Attachments!;
        var extrasAttachments = model.Extras?.Tmm.Attachments ?? [];

        for (int i = 0; i < attachments.Length; i++)
        {
            var att = attachments[i];

            // Find matching extras entry by name (case-sensitive, exact match)
            GlbExtras.AttachmentEntry? extras = null;
            foreach (var e in extrasAttachments)
            {
                if (e.Name == att.Name) { extras = e; break; }
            }

            w.Write(extras?.TypeFlag ?? 0u);
            w.Write(att.ParentBoneIndex);
            WriteUtf16String(w, att.Name);

            // Slot 1 (AdjustmentTransformMatrix) is reconstructed from the glTF node matrix
            // (which GlbExporter sourced from the original AdjustmentTransformMatrix). The glTF
            // flat is col-major-4x4 of M_col with F*M*F (axis-flip indices {1,2,3,4,8,12}) applied;
            // we undo the flip and extract rows 0/1/2 to produce TMM's row-major-3x4 layout.
            var slot1 = ReconstructAdjustmentMatrixFromGlb(att.LocalMatrix);
            for (int j = 0; j < 12; j++) w.Write(slot1[j]);

            // Slot 2 (LocalTransformMatrix) is what the AoM:R runtime uses to place the
            // attachment relative to its parent bone, so it must reflect Blender Empty edits.
            // Detect "unchanged round-trip" by comparing slot1 against the snapshot stored in
            // extras.AdjustmentMatrix: match -> preserve original Slot 2 verbatim so vanilla
            // TMMs with naturally differing slots round-trip exactly; otherwise the artist
            // moved/created the Empty, so write Slot 2 = Slot 1.
            bool roundTripUnchanged =
                extras != null && Matrix4x3CloseTo(slot1, extras.AdjustmentMatrix);
            var slot2 = roundTripUnchanged ? extras!.LocalMatrix : slot1;
            for (int j = 0; j < 12; j++) w.Write(slot2[j]);

            w.Write(extras?.DummyBoneMode ?? 0u);
            w.Write(extras?.DummyBoneTransformMode ?? 0u);
            WriteUtf16String(w, extras?.ForcedDummyBoneName ?? "");
            w.Write(extras?.FrameLimit ?? -1);
            w.Write(extras?.FramePosition ?? 0f);
            w.Write(extras?.DummyBoneAnimationFilter ?? 0u);

            var anims = extras?.SpecificAnimations ?? [];
            w.Write((uint)anims.Length);
            foreach (var anim in anims) WriteUtf16String(w, anim);
        }
    }

    // Inverts the per-flat-index axis flip applied by GlbExporter on attachment node matrices,
    // then returns the reconstructed TMM row-major-3x4 layout (rows 0/1/2 of M_col).
    // Source flat is col-major-4x4 of (F*M_col*F): col-major flat[col*4 + row], so row r of M_col
    // is at flat indices (0*4+r), (1*4+r), (2*4+r), (3*4+r) = r, r+4, r+8, r+12.
    static float[] ReconstructAdjustmentMatrixFromGlb(float[] flat)
    {
        Span<float> u = stackalloc float[16];
        flat.CopyTo(u);
        u[1] = -u[1]; u[2] = -u[2]; u[3] = -u[3];
        u[4] = -u[4]; u[8] = -u[8]; u[12] = -u[12];

        var result = new float[12];
        int k = 0;
        for (int row = 0; row < 3; row++)
            for (int col = 0; col < 4; col++)
                result[k++] = u[col * 4 + row];
        return result;
    }

    // Per-element compare with tolerance loose enough to absorb the float<->float32 trip
    // through Blender's matrix decomposition + recomposition while still flagging real edits.
    static bool Matrix4x3CloseTo(float[] a, float[] b)
    {
        if (a.Length != 12 || b.Length != 12) return false;
        const float eps = 1e-4f;
        for (int i = 0; i < 12; i++)
            if (MathF.Abs(a[i] - b[i]) > eps) return false;
        return true;
    }

    static void WriteTrailingSections(BinaryWriter w, GlbModel model)
    {
        if (model.Bones is { Length: > 0 })
        {
            var modBones = model.Extras?.Tmm.ModifiedBones ?? [];
            w.Write((uint)modBones.Length);
            foreach (var mb in modBones)
            {
                w.Write(mb.BoneIndex);
                w.Write(mb.OriginalRadius);
                w.Write(mb.RadiusMultiplier);
            }
        }

        w.Write(model.Extras?.Tmm.AutoBurnMode ?? (byte)0);
        w.Write((byte)0); // hasDestruction = false
        w.Write((byte)0); // hasPhysics = false
        w.Write((byte)0); // hasTreeSkeleton = false

        w.Write((byte)0);          // clickVolumeType
        w.Write((byte)0x56); w.Write((byte)0x58); // "VX" tag
        w.Write(1);                // VX payload length
        w.Write((byte)0);          // areVoxelsDefined = false

        var aa = model.Extras?.Tmm.AutoAttach;
        w.Write((byte)(aa?.AutoAttachCorpseToBone == true ? 1 : 0));
        WriteUtf16String(w, aa?.CorpseAttachBoneName ?? "");
        WriteUtf16String(w, aa?.DefaultDeathAnimation ?? "");
        w.Write((byte)(aa == null || aa.UsesAutoGeneratedImpactPoints ? 1 : 0));
        WriteUtf16String(w, aa?.DefaultIdleAnimationPath ?? "");

        var impactPoints = model.Extras?.Tmm.ImpactPoints ?? [];
        w.Write((uint)impactPoints.Length);
        foreach (var pt in impactPoints)
            for (int i = 0; i < 4; i++) w.Write(i < pt.Length ? pt[i] : 0f);
    }
}
