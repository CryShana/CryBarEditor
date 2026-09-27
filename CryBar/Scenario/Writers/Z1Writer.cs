using System.Buffers.Binary;

namespace CryBar.Scenario.Writers;

/// Emits the Z1 (entities) sub-section body. Output is byte-equivalent to the
/// FromXml binary path. Returned bytes are the inner body only (no "Z1" marker
/// or u32 length).
///
/// Layout:
///   u32 entity_count, u8 version
///   per entity: u16 id, u16 flags, "H1" + u32 size + body
///   h1 body: "EN" + u32 size + (prefix [PlayerId @ +8] + pos[Z,Y,X] + rot[9f] + tail) + suffix
///
/// ProtoIndex patches UnitP1 name_index/name_index_copy in H1Suffix:
///   new format: behind "P1" marker at suffix[6..14]
///   old format: inline at suffix[0..8]
public static class Z1Writer
{
    /// entityFlags lets callers preserve non-zero flag words on round-trip
    /// (ScenarioEntity doesn't model them). null = all zero (vanilla).
    /// rawEnvelopes are written verbatim right after their After entity (first when
    /// After is null, last when After is no longer in entities); tail is written last.
    public static byte[] Write(
        IReadOnlyList<ScenarioEntity> entities,
        byte version = 0,
        IReadOnlyList<ushort>? entityFlags = null,
        IReadOnlyList<(ScenarioEntity? After, byte[] Envelope)>? rawEnvelopes = null,
        byte[]? tail = null)
    {
        ArgumentNullException.ThrowIfNull(entities);
        if (entityFlags is not null && entityFlags.Count != entities.Count)
            throw new ArgumentException("entityFlags length must match entities length", nameof(entityFlags));

        rawEnvelopes ??= [];
        tail ??= [];

        var present = entities.ToHashSet();
        var anchored = rawEnvelopes
            .Where(r => r.After is not null && present.Contains(r.After))
            .ToLookup(r => r.After!, r => r.Envelope);

        long estimate = 5 + tail.Length;
        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            estimate += 4 + e.EnvelopeLeading.Length + 6 + 6 + e.H1Prefix.Length + 12 + 36 + e.H1EnTail.Length + e.H1Suffix.Length + e.EnvelopeTrailing.Length;
        }
        foreach (var raw in rawEnvelopes)
            estimate += raw.Envelope.Length;

        using var ms = new MemoryStream(checked((int)estimate));

        Span<byte> u32 = stackalloc byte[4];
        Span<byte> envelope = stackalloc byte[4];
        Span<byte> h1Header = stackalloc byte[6];
        h1Header[0] = (byte)'H';
        h1Header[1] = (byte)'1';

        BinaryPrimitives.WriteUInt32LittleEndian(u32, (uint)(entities.Count + rawEnvelopes.Count));
        ms.Write(u32);
        ms.WriteByte(version);

        foreach (var raw in rawEnvelopes)
            if (raw.After is null)
                ms.Write(raw.Envelope);

        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            // Format only encodes a 16-bit id; the model stores uint to play nicely
            // with HashSet<uint>/Dictionary<uint, *>. Reject overflow loudly.
            BinaryPrimitives.WriteUInt16LittleEndian(envelope, checked((ushort)e.EntityId));
            BinaryPrimitives.WriteUInt16LittleEndian(envelope.Slice(2), entityFlags is null ? (ushort)0 : entityFlags[i]);
            ms.Write(envelope);
            ms.Write(e.EnvelopeLeading);

            var h1 = BuildH1Body(e);
            BinaryPrimitives.WriteUInt32LittleEndian(h1Header.Slice(2), (uint)h1.Length);
            ms.Write(h1Header);
            ms.Write(h1, 0, h1.Length);

            ms.Write(e.EnvelopeTrailing);

            foreach (var raw in anchored[e])
                ms.Write(raw);
        }

        foreach (var raw in rawEnvelopes)
            if (raw.After is not null && !present.Contains(raw.After))
                ms.Write(raw.Envelope);

        ms.Write(tail);
        return ms.ToArray();
    }

    static byte[] BuildH1Body(ScenarioEntity e)
    {
        int enSize = e.H1Prefix.Length + 12 + 36 + e.H1EnTail.Length;
        int h1Size = 6 + enSize + e.H1Suffix.Length;

        var h1 = new byte[h1Size];
        var span = h1.AsSpan();

        span[0] = (byte)'E';
        span[1] = (byte)'N';
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(2, 4), (uint)enSize);

        var enBody = span.Slice(6, enSize);

        e.H1Prefix.AsSpan().CopyTo(enBody);
        // PlayerId as u32 LE at +8; parser reads h1[14] = low byte.
        if (e.H1Prefix.Length >= 12)
            BinaryPrimitives.WriteUInt32LittleEndian(enBody.Slice(8, 4), e.PlayerId);

        // File order is (Z, Y, X).
        int posOff = e.H1Prefix.Length;
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(posOff, 4),     e.Position.Z);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(posOff + 4, 4), e.Position.Y);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(posOff + 8, 4), e.Position.X);

        int rotOff = posOff + 12;
        var r = e.Rotation;
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff,      4), r.M11);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 4,  4), r.M12);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 8,  4), r.M13);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 12, 4), r.M21);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 16, 4), r.M22);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 20, 4), r.M23);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 24, 4), r.M31);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 28, 4), r.M32);
        BinaryPrimitives.WriteSingleLittleEndian(enBody.Slice(rotOff + 32, 4), r.M33);

        if (e.H1EnTail.Length > 0)
            e.H1EnTail.AsSpan().CopyTo(enBody.Slice(rotOff + 36));

        var suffix = span.Slice(6 + enSize, e.H1Suffix.Length);
        e.H1Suffix.AsSpan().CopyTo(suffix);
        PatchProtoIndex(suffix, e.ProtoIndex);

        return h1;
    }

    static void PatchProtoIndex(Span<byte> suffix, int protoIndex)
    {
        if (protoIndex < 0) return;
        var value = (uint)protoIndex;

        // New format: "P1" marker + u32 size, UnitP1 inner at +6.
        if (suffix.Length >= 14 && suffix[0] == (byte)'P' && suffix[1] == (byte)'1')
        {
            var p1Size = BinaryPrimitives.ReadUInt32LittleEndian(suffix.Slice(2, 4));
            if (p1Size >= 8 && 6 + p1Size <= (uint)suffix.Length)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(suffix.Slice(6, 4), value);
                BinaryPrimitives.WriteUInt32LittleEndian(suffix.Slice(10, 4), value);
                return;
            }
        }

        // Old format: first u32 only when bytes 0-1 are non-printable, mirroring
        // the parser's TryReadProtoIndex inline path. Without this guard we'd
        // overwrite the marker bytes of any other sub-section that happens to be
        // first in the suffix (Z2, K3, etc.).
        if (suffix.Length >= 8)
        {
            byte b0 = suffix[0], b1 = suffix[1];
            if (b0 < 0x20 || b0 > 0x7E || b1 < 0x20 || b1 > 0x7E)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(suffix.Slice(0, 4), value);
                BinaryPrimitives.WriteUInt32LittleEndian(suffix.Slice(4, 4), value);
            }
        }
    }
}
