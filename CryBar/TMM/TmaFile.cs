using System.Text;

namespace CryBar.TMM;

/// <summary>
/// Parses .tma animation files (Age of Mythology: Retold, version 12).
///
/// Header layout (60 bytes):
///   [0]  u32  Signature "BTMA"
///   [4]  u32  Version (12)
///   [8]  u32  TrackCount
///   [12] u32  FrameCount
///   [16] f32  Duration
///   [20] 8×f32  Root-bone bounding box (min xyz w, max xyz w)
///   [52] u32  BoneCount
///   [56] u32  ControllerCount
/// Followed by: Bones × BoneCount, Tracks × TrackCount,
///              Controllers × ControllerCount, Error section.
/// </summary>
public class TmaFile
{
    const int MaxNameLength = 5000;
    const int MaxBones = 2000;
    const int MaxTracks = 2000;
    const int MaxControllers = 1000;
    const int MaxErrors = 1000;
    const int MaxKeyframes = 100_000;

    public bool Parsed { get; private set; }

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

    readonly ReadOnlyMemory<byte> _data;

    public TmaFile(ReadOnlyMemory<byte> data)
    {
        _data = data;
        FileSize = data.Length;
    }

    public bool Parse()
    {
        var data = _data.Span;
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

        // Basic header is valid - mark parsed so callers get version/track/frame/duration info
        // even if the body layout differs from the documented v12 format.
        Parsed = true;

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
        // NOTE: Track keyframe data skipping is not fully verified against real game files.
        // The data size formulas (TranslationDataBytes/RotationDataBytes) may be incorrect -
        // testing shows misalignment after track 1 in some files, suggesting that
        // Raw encoding may include frame-index arrays or other prefix data not in the docs.
        // Bones are parsed correctly; track/controller/error sections may silently fail.
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

            var tBytes = EncodingDataBytes(translationEncoding, keyframeCount, 3);
            var rBytes = EncodingDataBytes(rotationEncoding, keyframeCount, 4);
            var sBytes = EncodingDataBytes(scaleEncoding, keyframeCount, 3);

            // Skip the raw keyframe data blobs
            if (offset + tBytes + rBytes + sBytes > data.Length) return false;
            offset += tBytes + rBytes + sBytes;

            tracks[i] = new TmaTrack
            {
                Name                 = trackName,
                TrackVersion         = trackVersion,
                TranslationEncoding  = translationEncoding,
                RotationEncoding     = rotationEncoding,
                ScaleEncoding        = scaleEncoding,
                KeyframeCount        = keyframeCount,
                TranslationDataBytes = tBytes,
                RotationDataBytes    = rBytes,
                ScaleDataBytes       = sBytes,
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
        sb.AppendLine($"Version: {Version}");
        sb.AppendLine($"Duration: {Duration:F3}  |  Frames: {FrameCount}");
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
                sb.AppendLine($"  {t.Name}  [{t.KeyframeCount} keyframes]  " +
                              $"T:{t.TranslationEncoding} R:{t.RotationEncoding} S:{t.ScaleEncoding}");
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
                                      $"range=[{v.Start:F3},{v.End:F3}]  invert={v.InvertLogic}");
                        break;
                    case TmaFootprintController f:
                        sb.AppendLine($"  Footprint  attach={f.AttachPointName}  " +
                                      $"spawnTime={f.SpawnTime:F3}  right={f.IsRightSide}");
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

    // ── Encoding size helpers ─────────────────────────────────────────────────

    /// <summary>Bytes used by a keyframe data block with the given component count (3 for translation/scale, 4 for rotation).</summary>
    static int EncodingDataBytes(TmaEncoding enc, int keyframeCount, int componentCount) => enc switch
    {
        TmaEncoding.Constant => 16,
        TmaEncoding.Raw      => keyframeCount * componentCount * 4,
        TmaEncoding.Quat32   => keyframeCount * 4,
        TmaEncoding.Quat64   => keyframeCount * 8,
        _                    => 0,
    };

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
