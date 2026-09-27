using System.Numerics;
using CryBar.TMM;
using static CryBar.Export.TmmWriteHelpers;

namespace CryBar.Export;

public static class TmaWriter
{
    public static (byte[] Tma, IReadOnlyList<string> Warnings) Write(
        GlbAnimation anim, GlbBone[] bones, GlbExtras.TmaSection? extras)
    {
        var warnings = new List<string>();
        var allControllers = extras?.Controllers ?? [];
        var controllers = allControllers
            .Where(c => c.Type == TmaControllerType.Visibility || c.Type == TmaControllerType.Footprint)
            .ToArray();
        foreach (var skipped in allControllers.Where(c => c.Type != TmaControllerType.Visibility && c.Type != TmaControllerType.Footprint))
            warnings.Add($"Animation '{anim.Name}' had unknown controller type {skipped.Type}; skipped.");

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // BTMA magic
        w.Write((byte)0x42); w.Write((byte)0x54); w.Write((byte)0x4D); w.Write((byte)0x41);
        w.Write(12u); // version

        // DP block (empty)
        w.Write((byte)0x44); w.Write((byte)0x50);
        w.Write(4);  // blockByteLength
        w.Write(0u); // numImportNames

        w.Write((uint)bones.Length); // numTracks
        w.Write(anim.FrameCount);
        w.Write(anim.Duration);

        // Root bbox: 2 x 3 floats (min XYZ, max XYZ) - zeroed when no positional data
        for (int i = 0; i < 6; i++) w.Write(0f);

        w.Write((uint)bones.Length); // numBones
        w.Write((uint)controllers.Length);

        WriteBones(w, bones);
        WriteTracks(w, anim, bones, warnings);
        WriteControllers(w, controllers, anim.Name, warnings);

        // Error section
        w.Write(0u); // errorFlags
        w.Write(0u); // errorCount

        var bytes = ms.ToArray();

        var validate = new TmaFile(bytes);
        if (!validate.Parsed)
            throw new InvalidOperationException("TmaWriter produced output that fails parse (writer bug).");

        return (bytes, warnings);
    }

    static void WriteBones(BinaryWriter w, GlbBone[] bones)
    {
        var worldMatrices = MatrixDecomp.ComputeBoneWorldMatrices(bones);

        for (int i = 0; i < bones.Length; i++)
        {
            var bone = bones[i];
            WriteUtf16String(w, bone.Name);
            w.Write(bone.ParentIndex);

            // localTransform = parent-relative (local) matrix
            WriteMatrix4x4Fmf(w, MatrixDecomp.ColMajorToMatrix(bone.LocalMatrix));
            // bindPose = world-space matrix
            WriteMatrix4x4Fmf(w, worldMatrices[i]);
            // inverseBindPose
            WriteMatrix4x4Fmf(w, MatrixDecomp.ColMajorToMatrix(bone.InverseBindMatrix));
        }
    }

    // Tolerances for Constant-track detection. Translation is in metres (1e-5 = 0.01 mm,
    // well below visual precision); rotation tolerance is far tighter than Quat64's
    // quantisation step (1/524287 ~= 2e-6).
    const float ConstantVec3Tol = 1e-5f;
    const float ConstantRotationTol = 1e-5f;

    static void WriteTracks(BinaryWriter w, GlbAnimation anim, GlbBone[] bones, List<string> warnings)
    {
        int frameCount = (int)anim.FrameCount;

        var tracksByBone = new GlbBoneTrack?[bones.Length];
        if (anim.Tracks != null)
        {
            foreach (var t in anim.Tracks)
            {
                if ((uint)t.BoneIndex < (uint)bones.Length)
                    tracksByBone[t.BoneIndex] = t;
            }
        }

        int sampleCount = Math.Max(1, frameCount);
        var tmaT = new Vector3[sampleCount];
        var tmaR = new Quaternion[sampleCount];
        var tmaS = new Vector3[sampleCount];

        for (int i = 0; i < bones.Length; i++)
        {
            WriteUtf16String(w, bones[i].Name);

            // bones[i].LocalMatrix is in glTF (axis-mirrored) space, so the decomposed bind pose
            // is mirror(bindT_orig) and mirror_quat(bindR_orig). The forward composition in
            // GlbExporter uses bindT_orig / bindR_orig (the original game-space pose), so we must
            // unmirror back before subtracting/inverting to recover the original delta.
            MatrixDecomp.Decompose(bones[i].LocalMatrix, out var bindT_glb, out var bindR_glb, out var bindS);
            var bindT = new Vector3(-bindT_glb.X, bindT_glb.Y, bindT_glb.Z);
            var bindR = new Quaternion(bindR_glb.X, -bindR_glb.Y, -bindR_glb.Z, bindR_glb.W);
            var invBindR = Quaternion.Inverse(bindR);

            var track = tracksByBone[i];

            // Pre-compute all per-frame TMA-space values, then choose the most compact encoding.
            for (int f = 0; f < sampleCount; f++)
            {
                var glbT = SampleVec3(track?.Translations, f, frameCount, anim.Duration, bindT_glb);
                // Forward: glbT = mirror(bindT + bindR * tmaT). Reverse: tmaT = invBindR * (unmirror(glbT) - bindT).
                var deltaParent = new Vector3(-glbT.X, glbT.Y, glbT.Z) - bindT;
                tmaT[f] = Vector3.Transform(deltaParent, invBindR);

                var glbR = SampleTrackRotation(track, f, frameCount, anim.Duration, bindR);
                // Forward: glbR = mirror(bindR * conj(tmaR)). Reverse: tmaR = conj(invBindR * unmirror(glbR)).
                var unmirrored = new Quaternion(glbR.X, -glbR.Y, -glbR.Z, glbR.W);
                tmaR[f] = Quaternion.Conjugate(invBindR * unmirrored);
            }

            // glbS = bindS * tmaS; an empty scale track is the rest scale, i.e. tmaS = 1.
            var scales = track?.Scales;
            bool hasScale = scales is { Length: > 0 };
            if (hasScale)
            {
                for (int f = 0; f < sampleCount; f++)
                {
                    var glbS = SampleVec3(scales, f, frameCount, anim.Duration, bindS);
                    tmaS[f] = new Vector3(
                        bindS.X != 0f ? glbS.X / bindS.X : 1f,
                        bindS.Y != 0f ? glbS.Y / bindS.Y : 1f,
                        bindS.Z != 0f ? glbS.Z / bindS.Z : 1f);
                }
            }
            else
            {
                tmaS[0] = Vector3.One;
            }

            bool tConst = IsConstantVec3(tmaT, frameCount);
            bool rConst = IsConstantQuat(tmaR, frameCount);
            bool sConst = !hasScale || IsConstantVec3(tmaS, frameCount);

            // Track header: version + 3 encoding bytes + keyframeCount
            w.Write((byte)1); // trackVersion
            w.Write((byte)(tConst ? TmaEncoding.Constant : TmaEncoding.Raw));      // translation
            w.Write((byte)(rConst ? TmaEncoding.Constant : TmaEncoding.Quat64));   // rotation
            w.Write((byte)(sConst ? TmaEncoding.Constant : TmaEncoding.Raw));      // scale
            w.Write(frameCount);

            WriteVec3Channel(w, tmaT, tConst, frameCount);

            // Rotation: Constant = 16 bytes inline (X, Y, Z, W); Quat64 = 4-byte size prefix + frameCount * 8.
            if (rConst)
            {
                w.Write(tmaR[0].X); w.Write(tmaR[0].Y); w.Write(tmaR[0].Z); w.Write(tmaR[0].W);
            }
            else
            {
                w.Write(frameCount * 8);
                for (int f = 0; f < frameCount; f++)
                    w.Write(EncodeQuat64(tmaR[f]));
            }

            WriteVec3Channel(w, tmaS, sConst, frameCount);
        }
    }

    // Constant = 16 bytes inline (X, Y, Z, padding); Raw = 4-byte size prefix + frameCount * 12.
    static void WriteVec3Channel(BinaryWriter w, Vector3[] values, bool isConst, int frameCount)
    {
        if (isConst)
        {
            w.Write(values[0].X); w.Write(values[0].Y); w.Write(values[0].Z); w.Write(0f);
            return;
        }

        w.Write(frameCount * 12);
        for (int f = 0; f < frameCount; f++)
        {
            w.Write(values[f].X); w.Write(values[f].Y); w.Write(values[f].Z);
        }
    }

    static bool IsConstantVec3(Vector3[] values, int count)
    {
        if (count <= 1) return true;
        var first = values[0];
        for (int i = 1; i < count; i++)
        {
            var v = values[i];
            if (MathF.Abs(v.X - first.X) > ConstantVec3Tol) return false;
            if (MathF.Abs(v.Y - first.Y) > ConstantVec3Tol) return false;
            if (MathF.Abs(v.Z - first.Z) > ConstantVec3Tol) return false;
        }
        return true;
    }

    // q and -q encode the same rotation, so accept either as "equal" within tolerance.
    static bool IsConstantQuat(Quaternion[] values, int count)
    {
        if (count <= 1) return true;
        var first = values[0];
        for (int i = 1; i < count; i++)
        {
            var v = values[i];
            float dx = v.X - first.X, dy = v.Y - first.Y, dz = v.Z - first.Z, dw = v.W - first.W;
            if (MathF.Abs(dx) <= ConstantRotationTol && MathF.Abs(dy) <= ConstantRotationTol
             && MathF.Abs(dz) <= ConstantRotationTol && MathF.Abs(dw) <= ConstantRotationTol) continue;
            float ex = -v.X - first.X, ey = -v.Y - first.Y, ez = -v.Z - first.Z, ew = -v.W - first.W;
            if (MathF.Abs(ex) <= ConstantRotationTol && MathF.Abs(ey) <= ConstantRotationTol
             && MathF.Abs(ez) <= ConstantRotationTol && MathF.Abs(ew) <= ConstantRotationTol) continue;
            return false;
        }
        return true;
    }

    static void WriteControllers(BinaryWriter w, GlbExtras.TmaControllerEntry[] controllers, string animName, List<string> warnings)
    {
        foreach (var c in controllers)
        {
            switch (c.Type)
            {
                case TmaControllerType.Visibility:
                    w.Write(TmaControllerType.Visibility);
                    w.Write(c.Start);
                    w.Write(c.End);
                    w.Write(c.EaseIn);
                    w.Write(c.EaseOut);
                    w.Write((byte)(c.InvertLogic ? 1 : 0));
                    WriteUtf16String(w, c.AttachPointName);
                    break;
                case TmaControllerType.Footprint:
                    w.Write(TmaControllerType.Footprint);
                    w.Write(c.SpawnTime);
                    WriteUtf16String(w, c.FootprintName);
                    w.Write(c.FootprintId);
                    w.Write((byte)(c.InvertTextureY ? 1 : 0));
                    WriteUtf16String(w, c.AttachPointName);
                    w.Write((byte)(c.IsRightSide ? 1 : 0));
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled controller type {c.Type} (should have been filtered).");
            }
        }
    }

    // Returns the glTF-space value for frame f.
    // If no or empty samples: the glTF-space rest value.
    // If sample count matches frameCount: direct lookup.
    // Otherwise: LERP resample at uniform time t = f * duration / (frameCount - 1).
    static Vector3 SampleVec3(Vector3[]? samples, int f, int frameCount, float duration, Vector3 rest)
    {
        if (samples == null || samples.Length == 0)
            return rest;

        if (samples.Length == frameCount)
            return samples[f];

        float t = frameCount > 1 ? f * duration / (frameCount - 1) : 0f;
        return LerpVec3Uniform(samples, t, duration);
    }

    // Returns the glTF-space rotation for frame f.
    // If no track or empty track: rest pose = mirror(bindR) = (bindR.X, -bindR.Y, -bindR.Z, bindR.W).
    // If sample count matches frameCount: direct lookup.
    // Otherwise: SLERP resample at uniform time t = f * duration / (frameCount - 1).
    static Quaternion SampleTrackRotation(GlbBoneTrack? track, int f, int frameCount, float duration, Quaternion bindR)
    {
        if (track == null || track.Rotations.Length == 0)
            return new Quaternion(bindR.X, -bindR.Y, -bindR.Z, bindR.W);

        var samples = track.Rotations;
        if (samples.Length == frameCount)
            return samples[f];

        float t = frameCount > 1 ? f * duration / (frameCount - 1) : 0f;
        return SlerpQuatUniform(samples, t, duration);
    }

    // LERP on a uniformly-sampled Vector3 array where sample i is at time i*duration/(n-1).
    static Vector3 LerpVec3Uniform(Vector3[] samples, float t, float duration)
    {
        int n = samples.Length;
        if (n == 1 || duration <= 0f) return samples[0];
        float tNorm = Math.Clamp(t / duration, 0f, 1f) * (n - 1);
        int lo = (int)tNorm;
        int hi = Math.Min(lo + 1, n - 1);
        float a = tNorm - lo;
        return Vector3.Lerp(samples[lo], samples[hi], a);
    }

    // SLERP on a uniformly-sampled Quaternion array where sample i is at time i*duration/(n-1).
    static Quaternion SlerpQuatUniform(Quaternion[] samples, float t, float duration)
    {
        int n = samples.Length;
        if (n == 1 || duration <= 0f) return samples[0];
        float tNorm = Math.Clamp(t / duration, 0f, 1f) * (n - 1);
        int lo = (int)tNorm;
        int hi = Math.Min(lo + 1, n - 1);
        float a = tNorm - lo;
        return Quaternion.Slerp(samples[lo], samples[hi], a);
    }

    // Encodes a quaternion into Quat64 "smallest three" format (8 bytes).
    // Layout: [4-bit dropped-index][20-bit C2][20-bit C1][20-bit C0], each 20-bit = sign + 19-bit magnitude.
    internal static ulong EncodeQuat64(Quaternion q)
    {
        // Normalize
        float mag = MathF.Sqrt(q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W);
        if (mag > 0f) { q = new Quaternion(q.X / mag, q.Y / mag, q.Z / mag, q.W / mag); }

        Span<float> c = stackalloc float[4];
        c[0] = q.X; c[1] = q.Y; c[2] = q.Z; c[3] = q.W;

        // Find the largest absolute component
        int largestIdx = 0;
        float largestAbs = Math.Abs(c[0]);
        for (int i = 1; i < 4; i++)
        {
            float a = Math.Abs(c[i]);
            if (a > largestAbs) { largestAbs = a; largestIdx = i; }
        }

        // Ensure largest component is positive (canonical form)
        if (c[largestIdx] < 0f)
            for (int i = 0; i < 4; i++) c[i] = -c[i];

        const float Scale = 0.70710678118f; // 1/sqrt(2): max magnitude of a non-largest component
        const float MaxMag = 524287f;        // 2^19 - 1

        // Collect the three non-largest components.
        // Decoder reads comp=3 from low bits, comp=2 from next, comp=1 from highest,
        // so we pack in descending component order (3->2->1->0 skipping largestIdx).
        ulong packed = 0;
        int bitOffset = 0;
        for (int i = 3; i >= 0; i--)
        {
            if (i == largestIdx) continue;
            float val = c[i] / Scale;
            float clamped = Math.Clamp(val, -1f, 1f);
            uint magnitude = (uint)MathF.Round(MathF.Abs(clamped) * MaxMag);
            uint signBit = clamped < 0f ? 1u : 0u; // decoder: negative = (bit >> 19) != 0
            // 19-bit magnitude, then 1 sign bit
            packed |= ((ulong)(magnitude & 0x7FFFF)) << bitOffset;
            bitOffset += 19;
            packed |= ((ulong)signBit) << bitOffset;
            bitOffset++;
        }

        // Dropped-component index occupies bits [63:60] (top 4 bits).
        // Decoder uses (packed >> 60) & 0xF as index.
        // Map from component order [X=0,Y=1,Z=2,W=3] to the index the decoder expects.
        // TmaDecoder reads: idx = (packed >> 60) & 0xF, then fills slots 3,2,1,0 skipping idx.
        // We need largestIdx to map such that the decoder reconstructs the dropped component correctly.
        // The decoder slot order (high to low): slot3=comp3, slot2=comp2, slot1=comp1, slot0=comp0,
        // where comp 0,1,2,3 map to X,Y,Z,W. So idx directly equals largestIdx.
        packed |= ((ulong)(largestIdx & 0xF)) << 60;

        return packed;
    }

}
