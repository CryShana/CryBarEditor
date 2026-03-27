using System.Text;

namespace CryBar.TMM;

/// <summary>
/// Parses .tma animation files (Age of Mythology: Retold, version 12).
///
/// Header layout:
///   [0]  u32    Signature "BTMA"
///   [4]  u32    Version (12)
///   [+]  optional "DP" import metadata block
///   [+]  u32    TrackCount
///   [+]  u32    FrameCount (resample frame count)
///   [+]  f32    Duration (seconds)
///   [+]  6×f32  Root-bone bounding box (min XYZ, max XYZ)
///   [+]  u32    BoneCount
///   [+]  u32    ControllerCount
///
/// Followed by: Bones × BoneCount, Tracks × TrackCount,
///              Controllers × ControllerCount, Error section.
///
/// Track encoding blocks: Constant = 16 bytes inline (__m128);
/// Raw/Quat32/Quat64 = 4-byte size prefix (uint32) followed by data.
/// </summary>
public class TmaFile
{
    const int MaxNameLength = 5000;
    const int MaxBones = 2000;
    const int MaxTracks = 2000;
    const int MaxControllers = 1000;
    const int MaxErrors = 1000;
    const int MaxKeyframes = 100_000;

    public bool Parsed { get; }

    // ── Header ──────────────────────────────────────────────────────────────
    public uint Signature { get; private set; }
    public uint Version { get; private set; }
    public int FileSize { get; private set; }

    /// <summary>Number of animation tracks (one per animated bone channel).</summary>
    public uint NumTracks { get; private set; }

    /// <summary>Total frame count in the animation.</summary>
    public uint FrameCount { get; private set; }

    /// <summary>Animation duration (seconds or ticks - depends on context).</summary>
    public float Duration { get; private set; }

    /// <summary>Root bone bounding-box min (x,y,z,w).</summary>
    public float[] RootBBoxMin { get; private set; } = [];

    /// <summary>Root bone bounding-box max (x,y,z,w).</summary>
    public float[] RootBBoxMax { get; private set; } = [];

    /// <summary>Number of bones in the skeleton.</summary>
    public uint NumBones { get; private set; }

    /// <summary>Number of animation controllers (visibility, footprint, etc.).</summary>
    public uint NumControllers { get; private set; }

    // ── Body ────────────────────────────────────────────────────────────────
    public TmaBone[]? Bones { get; private set; }
    public TmaTrack[]? Tracks { get; private set; }
    public TmaController[]? Controllers { get; private set; }

    /// <summary>Error flags from the animation error section (0 = no errors).</summary>
    public uint ErrorFlags { get; private set; }

    /// <summary>Error strings reported by the exporter, if any.</summary>
    public string[]? ErrorStrings { get; private set; }

    public TmaFile(ReadOnlyMemory<byte> data)
    {
        FileSize = data.Length;
        Parsed = Parse(data.Span);
    }

    bool Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 10) return false;

        // Require "BTMA" signature
        if (data is not [0x42, 0x54, 0x4D, 0x41, ..]) return false;

        var offset = 0;
        if (!TryReadUInt32(data, ref offset, out var sig)) return false;
        Signature = sig;
        if (!TryReadUInt32(data, ref offset, out var ver)) return false;
        Version = ver;

        // Skip optional "DP" import metadata block (present in game-generated TMA files).
        // Identical to the block in TMM: 2-byte marker, 4-byte block length, then block data.
        if (offset + 2 <= data.Length && data[offset] == 0x44 && data[offset + 1] == 0x50)
        {
            offset += 2; // skip "DP" marker
            if (!TryReadInt32(data, ref offset, out var blockByteLength)) return false;
            if (blockByteLength < 0 || offset + blockByteLength > data.Length) return false;
            offset += blockByteLength;
        }

        if (offset + 12 > data.Length) return false;

        if (!TryReadUInt32(data, ref offset, out var numTracks)) return false;
        NumTracks = numTracks;

        if (!TryReadUInt32(data, ref offset, out var frameCount)) return false;
        FrameCount = frameCount;

        if (!TryReadFloat(data, ref offset, out var duration)) return false;
        Duration = duration;

        // Attempt full body parse (v12 layout per official docs).
        // Failure stops parsing but does not invalidate the header fields above.
        TryParseBody(data, ref offset);
        return true;
    }

    /// <summary>
    /// Attempts to parse the full TMA body (bounding box, bones, tracks, controllers, errors).
    /// Returns false if any read fails; caller ignores the return value.
    /// </summary>
    bool TryParseBody(ReadOnlySpan<byte> data, ref int offset)
    {
        // Root bone bounding box: 2 × 3 floats (min XYZ + max XYZ)
        if (offset + 24 > data.Length) return false;
        RootBBoxMin = ReadFloats(data, ref offset, 3);
        RootBBoxMax = ReadFloats(data, ref offset, 3);

        if (!TryReadUInt32(data, ref offset, out var numBones)) return false;
        if (numBones > MaxBones) return false;
        NumBones = numBones;

        if (!TryReadUInt32(data, ref offset, out var numControllers)) return false;
        if (numControllers > MaxControllers) return false;
        NumControllers = numControllers;

        // ── Bones ───────────────────────────────────────────────────────────
        var bones = new TmaBone[numBones];
        for (int i = 0; i < numBones; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var boneName)) return false;
            if (!TryReadInt32(data, ref offset, out var parentId)) return false;

            // local transform, bind pose, inverse bind pose - each 4×4 = 64 bytes = 16 floats
            if (offset + 192 > data.Length) return false;
            var localTransform  = ReadFloats(data, ref offset, 16);
            var bindPose        = ReadFloats(data, ref offset, 16);
            var inverseBindPose = ReadFloats(data, ref offset, 16);

            bones[i] = new TmaBone
            {
                Name            = boneName,
                ParentId        = parentId,
                LocalTransform  = localTransform,
                BindPose        = bindPose,
                InverseBindPose = inverseBindPose,
            };
        }
        Bones = bones;

        // ── Animation Tracks ────────────────────────────────────────────────
        // Non-Constant encoding blocks are prefixed with a 4-byte data-size (uint32).
        // Constant encoding stores an __m128 (16 bytes) directly with no prefix.
        if (NumTracks > MaxTracks) return false;
        var tracks = new TmaTrack[NumTracks];
        for (int i = 0; i < NumTracks; i++)
        {
            if (!TryReadUTF16String(data, ref offset, out var trackName)) return false;

            // 4 bytes: track version, translation encoding, rotation encoding, scale encoding
            if (offset + 4 > data.Length) return false;
            var trackVersion        = data[offset++];
            var translationEncoding = (TmaEncoding)data[offset++];
            var rotationEncoding    = (TmaEncoding)data[offset++];
            var scaleEncoding       = (TmaEncoding)data[offset++];

            if (!TryReadInt32(data, ref offset, out var keyframeCount)) return false;
            if (keyframeCount < 0 || keyframeCount > MaxKeyframes) return false;

            // Each encoding block: Constant = 16 bytes inline; others = 4-byte size prefix + data
            if (!TryReadEncodingBlock(data, ref offset, translationEncoding, out var tData)) return false;
            if (!TryReadEncodingBlock(data, ref offset, rotationEncoding, out var rData)) return false;
            if (!TryReadEncodingBlock(data, ref offset, scaleEncoding, out var sData)) return false;

            tracks[i] = new TmaTrack
            {
                Name                = trackName,
                TrackVersion        = trackVersion,
                TranslationEncoding = translationEncoding,
                RotationEncoding    = rotationEncoding,
                ScaleEncoding       = scaleEncoding,
                KeyframeCount       = keyframeCount,
                TranslationData     = tData,
                RotationData        = rData,
                ScaleData           = sData,
            };
        }
        Tracks = tracks;

        // ── Animation Controllers ────────────────────────────────────────────
        var controllers = new TmaController[numControllers];
        for (int i = 0; i < numControllers; i++)
        {
            if (!TryReadInt32(data, ref offset, out var controllerType)) return false;

            TmaController ctrl;
            switch (controllerType)
            {
                case 1: // Visibility
                {
                    if (!TryReadFloat(data, ref offset, out var start)) return false;
                    if (!TryReadFloat(data, ref offset, out var end)) return false;
                    if (!TryReadFloat(data, ref offset, out var easeIn)) return false;
                    if (!TryReadFloat(data, ref offset, out var easeOut)) return false;
                    if (offset >= data.Length) return false;
                    var invertLogic = data[offset++] != 0;
                    if (!TryReadUTF16String(data, ref offset, out var attachName)) return false;
                    ctrl = new TmaVisibilityController
                    {
                        Type            = controllerType,
                        Start           = start,
                        End             = end,
                        EaseIn          = easeIn,
                        EaseOut         = easeOut,
                        InvertLogic     = invertLogic,
                        AttachPointName = attachName,
                    };
                    break;
                }
                case 2: // Footprint
                {
                    if (!TryReadFloat(data, ref offset, out var spawnTime)) return false;
                    if (!TryReadUTF16String(data, ref offset, out var footprintName)) return false;
                    if (!TryReadInt32(data, ref offset, out var footprintId)) return false;
                    if (offset >= data.Length) return false;
                    var invertTextureY = data[offset++] != 0;
                    if (!TryReadUTF16String(data, ref offset, out var attachName)) return false;
                    if (offset >= data.Length) return false;
                    var isRightSide = data[offset++] != 0;
                    ctrl = new TmaFootprintController
                    {
                        Type            = controllerType,
                        SpawnTime       = spawnTime,
                        FootprintName   = footprintName,
                        FootprintId     = footprintId,
                        InvertTextureY  = invertTextureY,
                        AttachPointName = attachName,
                        IsRightSide     = isRightSide,
                    };
                    break;
                }
                default:
                    ctrl = new TmaUnknownController { Type = controllerType };
                    break;
            }
            controllers[i] = ctrl;
        }
        Controllers = controllers;

        // ── Error section ────────────────────────────────────────────────────
        if (offset + 8 <= data.Length)
        {
            if (!TryReadUInt32(data, ref offset, out var errorFlags)) return false;
            ErrorFlags = errorFlags;

            if (TryReadUInt32(data, ref offset, out var errorCount) && errorCount <= MaxErrors)
            {
                var errorStrings = new string[errorCount];
                bool errorsOk = true;
                for (int i = 0; i < errorCount; i++)
                {
                    if (!TryReadUTF16String(data, ref offset, out var errStr)) { errorsOk = false; break; }
                    errorStrings[i] = errStr;
                }
                if (errorsOk) ErrorStrings = errorStrings;
            }
        }

        return true;
    }

    /// <summary>
    /// Generates a human-readable summary of the parsed TMA file.
    /// </summary>
    public string GetSummary()
    {
        if (!Parsed) return "(TMA not parsed)";

        var sb = new StringBuilder();
        sb.AppendLine($"TMA Animation File");
        sb.AppendLine($"Signature: 0x{Signature:X8} (BTMA)");
        sb.AppendLine($"Version: {Version}  |  File size: {FileSize:N0} bytes");
        sb.AppendLine($"Duration: {Duration:F3}s  |  Frames: {FrameCount}");
        if (RootBBoxMin.Length > 0 && RootBBoxMax.Length > 0)
        {
            sb.AppendLine($"Root BBox: ({RootBBoxMin[0]:F2}, {RootBBoxMin[1]:F2}, {RootBBoxMin[2]:F2})" +
                          $" .. ({RootBBoxMax[0]:F2}, {RootBBoxMax[1]:F2}, {RootBBoxMax[2]:F2})");
        }
        sb.AppendLine();

        // Bones
        if (Bones != null && Bones.Length > 0)
        {
            sb.AppendLine($"Bones ({Bones.Length}):");
            foreach (var bone in Bones)
            {
                var parentName = bone.ParentId >= 0 && bone.ParentId < Bones.Length
                    ? Bones[bone.ParentId].Name : "none";
                sb.AppendLine($"  {bone.Name} (parent: {parentName})");
            }
            sb.AppendLine();
        }

        // Tracks
        if (Tracks != null && Tracks.Length > 0)
        {
            sb.AppendLine($"Animation Tracks ({Tracks.Length}):");
            foreach (var t in Tracks)
            {
                var totalBytes = t.TranslationData.Length + t.RotationData.Length + t.ScaleData.Length;
                sb.AppendLine($"  {t.Name}  [{t.KeyframeCount} kf]  " +
                              $"T:{t.TranslationEncoding} R:{t.RotationEncoding} S:{t.ScaleEncoding}  " +
                              $"({totalBytes:N0}B)");
            }
            sb.AppendLine();
        }

        // Controllers
        if (Controllers != null && Controllers.Length > 0)
        {
            sb.AppendLine($"Controllers ({Controllers.Length}):");
            foreach (var c in Controllers)
            {
                switch (c)
                {
                    case TmaVisibilityController v:
                        sb.AppendLine($"  Visibility  attach={v.AttachPointName}  " +
                                      $"range=[{v.Start:F3},{v.End:F3}]  " +
                                      $"ease=[{v.EaseIn:F3},{v.EaseOut:F3}]  invert={v.InvertLogic}");
                        break;
                    case TmaFootprintController f:
                        sb.AppendLine($"  Footprint  attach={f.AttachPointName}  " +
                                      $"name={f.FootprintName}  id={f.FootprintId}  " +
                                      $"spawnTime={f.SpawnTime:F3}  right={f.IsRightSide}  invertY={f.InvertTextureY}");
                        break;
                    default:
                        sb.AppendLine($"  Unknown (type {c.Type})");
                        break;
                }
            }
            sb.AppendLine();
        }

        // Errors
        if (ErrorFlags != 0)
            sb.AppendLine($"Error flags: 0x{ErrorFlags:X8}");
        if (ErrorStrings != null && ErrorStrings.Length > 0)
        {
            sb.AppendLine($"Exporter errors ({ErrorStrings.Length}):");
            foreach (var e in ErrorStrings)
                sb.AppendLine($"  {e}");
        }

        return sb.ToString();
    }

    // ── Encoding block helpers ───────────────────────────────────────────────

    /// <summary>
    /// Reads one encoding data block. Constant = 16 bytes inline (__m128).
    /// All other encodings are prefixed with a uint32 byte-count, then that many bytes of data.
    /// Returns a copy of the raw data bytes.
    /// </summary>
    static bool TryReadEncodingBlock(ReadOnlySpan<byte> data, ref int offset, TmaEncoding enc, out byte[] blockData)
    {
        blockData = [];
        if (enc == TmaEncoding.Constant)
        {
            if (offset + 16 > data.Length) return false;
            blockData = data.Slice(offset, 16).ToArray();
            offset += 16;
            return true;
        }

        // Non-constant: read 4-byte size prefix, then copy the data
        if (!TryReadInt32(data, ref offset, out var blockSize)) return false;
        if (blockSize < 0 || offset + blockSize > data.Length) return false;
        blockData = data.Slice(offset, blockSize).ToArray();
        offset += blockSize;
        return true;
    }

    // ── Low-level read helpers (delegated to shared TmmReadHelpers) ──────────

    static bool TryReadInt32(ReadOnlySpan<byte> data, ref int offset, out int value)
        => TmmReadHelpers.TryReadInt32(data, ref offset, out value);

    static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int offset, out uint value)
        => TmmReadHelpers.TryReadUInt32(data, ref offset, out value);

    static bool TryReadFloat(ReadOnlySpan<byte> data, ref int offset, out float value)
        => TmmReadHelpers.TryReadFloat(data, ref offset, out value);

    static bool TryReadUTF16String(ReadOnlySpan<byte> data, ref int offset, out string value)
        => TmmReadHelpers.TryReadUTF16String(data, ref offset, out value, MaxNameLength);

    static float[] ReadFloats(ReadOnlySpan<byte> data, ref int offset, int count)
        => TmmReadHelpers.ReadFloats(data, ref offset, count);
}
