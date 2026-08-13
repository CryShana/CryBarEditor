using System.Buffers.Binary;
using CryBar.Scenario.Writers;

namespace CryBar.Scenario;

public partial class ScenarioFile
{
    /// Replaces the J1.TN and J1.Z1 section bytes from parsed views.
    /// Throws if J1 / TN / Z1 are missing or unparseable -- silently dropping
    /// edits would corrupt user work.
    public void FlushParsedViews(ScenarioTerrain terrain, IReadOnlyList<ScenarioEntity> entities, IReadOnlyList<string>? protoTable = null, ScenarioPlayersView? players = null)
    {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(entities);
        if (!Parsed) throw new InvalidOperationException("Scenario not parsed; cannot flush.");

        var j1Section = FindSection("J1")
            ?? throw new InvalidOperationException("Scenario has no J1 section; cannot flush edits.");

        var j1 = new ScenarioJ1(j1Section.Data);
        if (!j1.Parsed) throw new InvalidOperationException("J1 section did not parse; cannot flush edits.");

        ScenarioSection? tmSection = null, tn = null, z1 = null, pl = null;
        foreach (var sub in j1.Sections)
        {
            if (tmSection is null && (sub.Marker == "TM" || sub.Marker == "PT")) tmSection = sub;
            else if (tn is null && sub.Marker == "TN") tn = sub;
            else if (z1 is null && sub.Marker == "Z1") z1 = sub;
            else if (pl is null && sub.Marker == "PL") pl = sub;
        }
        if (tn is null) throw new InvalidOperationException("J1 has no TN sub-section; cannot flush terrain.");
        if (z1 is null) throw new InvalidOperationException("J1 has no Z1 sub-section; cannot flush entities.");

        if (players is not null)
        {
            var plTarget = pl ?? FindSection("PL")
                ?? throw new InvalidOperationException("Scenario has no PL section; cannot flush player edits.");
            plTarget.Data = WritePlayersView(players);
        }

        if (tmSection is not null && protoTable is not null)
        {
            // Preserve the section's "type" word so byte-identical for unchanged tables.
            var tmType = tmSection.Data.Length >= 4
                ? BinaryPrimitives.ReadUInt32LittleEndian(tmSection.Data)
                : 0u;
            tmSection.Data = WriteTmStrings(tmType, protoTable);
        }

        tn.Data = TnWriter.Write(terrain);

        var (version, flagsById) = ReadZ1VersionAndFlags(z1.Data);
        ushort[]? flags = null;
        if (flagsById.Count > 0)
        {
            flags = new ushort[entities.Count];
            for (int i = 0; i < entities.Count; i++)
                flagsById.TryGetValue(checked((ushort)entities[i].EntityId), out flags[i]);
        }
        z1.Data = Z1Writer.Write(entities, version, flags);

        j1Section.Data = j1.ToBytes();
    }

    /// <summary>
    /// Walks an existing Z1 body and extracts the version byte plus a map of
    /// entity_id -> flags word from each per-entity envelope. Mirrors the
    /// envelope walk in <see cref="ScenarioEntityListBuilder"/> but only
    /// reads the 4-byte (id, flags) header.
    /// </summary>
    static (byte version, Dictionary<ushort, ushort> flagsById) ReadZ1VersionAndFlags(byte[] z1Body)
    {
        var flags = new Dictionary<ushort, ushort>();
        if (z1Body.Length < 5) return (0, flags);

        var span = z1Body.AsSpan();
        var entityCount = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var version = span[4];

        int off = 5;
        for (uint ei = 0; ei < entityCount && off + 4 <= span.Length; ei++)
        {
            var entityId = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off));
            var flagsWord = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(off + 2));
            flags[entityId] = flagsWord;
            off += 4;

            // Skip the H1 (and any other) sub-sections in this envelope until we
            // hit a non-printable marker.
            while (off + 6 <= span.Length)
            {
                byte b0 = span[off], b1 = span[off + 1];
                if (b0 < 0x20 || b0 > 0x7E || b1 < 0x20 || b1 > 0x7E) break;

                var size = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(off + 2));
                if (size > ScenarioEntityListBuilder.MaxSubSectionSize || off + 6 + size > (uint)span.Length) break;
                off += 6 + (int)size;
            }
        }

        return (version, flags);
    }
}
