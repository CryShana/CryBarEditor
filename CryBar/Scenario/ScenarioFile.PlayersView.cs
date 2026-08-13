using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace CryBar.Scenario;

public partial class ScenarioFile
{
    /// <summary>
    /// Parses the PL section (top-level or inside J1) into an editable view.
    /// Returns null when the section is missing or has an unexpected layout;
    /// callers must treat that as "players not editable", not as an error.
    /// </summary>
    public ScenarioPlayersView? ParsePlayersView()
    {
        var pl = FindPlSectionData();
        if (pl is null) return null;

        try
        {
            return ParsePlayersView(pl);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return null;
        }
    }

    byte[]? FindPlSectionData()
    {
        var top = FindSection("PL");
        if (top is not null) return top.Data;
        return GetJ1() is { Parsed: true } j1 ? j1.FindSection("PL")?.Data : null;
    }

    internal static ScenarioPlayersView ParsePlayersView(byte[] plData)
    {
        var data = plData.AsSpan();
        if (data.Length < 8) throw new InvalidDataException("PL too short");

        var view = new ScenarioPlayersView();

        int off = 0;
        view.Unk1 = U32(data, ref off);
        var playerCount = U32(data, ref off);

        for (uint p = 0; p < playerCount; p++)
        {
            if (off + 7 > data.Length) throw new InvalidDataException("Truncated BP header");
            if (data[off] != 0x01 || data[off + 1] != (byte)'B' || data[off + 2] != (byte)'P')
                throw new InvalidDataException("Bad BP magic");
            off += 3;
            int bpEnd = off + 4 + (int)U32(data, ref off);
            if (bpEnd > data.Length) throw new InvalidDataException("BP length out of range");

            var bpVersion = I32(data, ref off);

            ExpectMarker(data, ref off, 'P', '1');
            int p1End = off + 4 + (int)U32(data, ref off);
            var id = U32(data, ref off);
            byte p1Pad = data[off++];
            if (!TryReadUTF16(data, off, out var strId, out off)) throw new InvalidDataException("P1 strId");
            if (!TryReadUTF16(data, off, out var name, out off)) throw new InvalidDataException("P1 name");
            if (!TryReadUTF16(data, off, out var nameStrId, out off)) throw new InvalidDataException("P1 nameStrId");
            byte p1Unk4 = data[off++];
            if (!TryReadUTF16(data, off, out var strId2, out off)) throw new InvalidDataException("P1 strId2");
            var endId = U32(data, ref off);
            var p1Tail = data[off..p1End].ToArray();
            off = p1End;

            ExpectMarker(data, ref off, 'P', '2');
            int p2End = off + 4 + (int)U32(data, ref off);
            var p2Magic = U32(data, ref off);
            var god = U32(data, ref off);
            off += 8; // 0xFF padding
            var p2Tail = data[off..p2End].ToArray();
            off = p2End;

            ExpectMarker(data, ref off, 'P', '3');
            int p3End = off + 4 + (int)U32(data, ref off);
            var startAge = U32(data, ref off);
            var p3Unk2 = I32(data, ref off);
            var classGod = U32(data, ref off);
            var heroicGod = U32(data, ref off);
            var mythicGod = U32(data, ref off);
            var maxAge = U32(data, ref off);
            var popLimit = I32(data, ref off);
            var initPopCap = I32(data, ref off);
            var p3Tail = data[off..p3End].ToArray();
            off = p3End;

            ExpectMarker(data, ref off, 'P', '4');
            var p4Len = U32(data, ref off);
            var p4Raw = data.Slice(off, (int)p4Len).ToArray();
            off += (int)p4Len;

            ExpectMarker(data, ref off, 'P', '5');
            int p5End = off + 4 + (int)U32(data, ref off);
            var p5Raw = data[off..p5End].ToArray();
            int p5Off = off + 3;
            if (!TryReadUTF16(data, p5Off, out var aiPath, out p5Off)) throw new InvalidDataException("P5 ai");
            if (!TryReadUTF16(data, p5Off, out _, out p5Off)) throw new InvalidDataException("P5 ai2");
            p5Off += 2;
            var color = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(p5Off));
            off = p5End;

            ExpectMarker(data, ref off, 'P', '6');
            int p6End = off + 4 + (int)U32(data, ref off);
            var diplomacy = ReadInt32List(data, ref off);
            var list2 = ReadInt32List(data, ref off);
            var resMagic = I32(data, ref off);
            var gold = F32(data, ref off);
            var wood = F32(data, ref off);
            var food = F32(data, ref off);
            var favor = F32(data, ref off);
            var resTotal = F32(data, ref off);
            off += 17; // Pad0<12> + Pad0<5>
            var p6Tail = data[off..p6End].ToArray();
            off = p6End;

            var tailRaw = data[off..bpEnd].ToArray();
            off = bpEnd;

            view.Players.Add(new ScenarioPlayer
            {
                Version = bpVersion,
                Id = id, P1Pad = p1Pad, StrId = strId, Name = name, NameStrId = nameStrId,
                P1Unk4 = p1Unk4, StrId2 = strId2, EndId = endId, P1Tail = p1Tail,
                P2Magic = p2Magic, God = god, P2Tail = p2Tail,
                StartAge = startAge, P3Unk2 = p3Unk2, ClassGod = classGod, HeroicGod = heroicGod,
                MythicGod = mythicGod, MaxAge = maxAge, PopLimit = popLimit, InitPopCap = initPopCap, P3Tail = p3Tail,
                P4Raw = p4Raw, P5Raw = p5Raw, AiPath = aiPath, Color = color,
                Diplomacy = diplomacy, List2 = list2, ResMagic = resMagic,
                Gold = gold, Wood = wood, Food = food, Favor = favor, ResTotal = resTotal, P6Tail = p6Tail,
                TailRaw = tailRaw,
            });
        }

        view.PlTail = data[off..].ToArray();
        return view;
    }

    static void ExpectMarker(ReadOnlySpan<byte> data, ref int off, char a, char b)
    {
        if (off + 2 > data.Length || data[off] != (byte)a || data[off + 1] != (byte)b)
            throw new InvalidDataException($"Expected {a}{b} marker");
        off += 2;
    }

    static uint U32(ReadOnlySpan<byte> data, ref int off)
    {
        var v = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off));
        off += 4;
        return v;
    }

    static int I32(ReadOnlySpan<byte> data, ref int off)
    {
        var v = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off));
        off += 4;
        return v;
    }

    static float F32(ReadOnlySpan<byte> data, ref int off)
    {
        var v = BitConverter.ToSingle(data.Slice(off, 4));
        off += 4;
        return v;
    }

    static List<int> ReadInt32List(ReadOnlySpan<byte> data, ref int off)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off)); off += 4;
        if (count > (uint)(data.Length - off) / 4) throw new InvalidDataException("Int32 list count out of range");
        var list = new List<int>((int)count);
        for (uint i = 0; i < count; i++)
        {
            list.Add(BinaryPrimitives.ReadInt32LittleEndian(data.Slice(off)));
            off += 4;
        }
        return list;
    }

    /// <summary>Re-encodes the PL section bytes from an edited view.</summary>
    public static byte[] WritePlayersView(ScenarioPlayersView view)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(view.Unk1);
        bw.Write((uint)view.Players.Count);

        foreach (var pl in view.Players)
        {
            var bp = WritePlayerBp(pl);
            bw.Write((byte)0x01); bw.Write((byte)'B'); bw.Write((byte)'P');
            bw.Write((uint)bp.Length);
            bw.Write(bp);
        }

        bw.Write(view.PlTail);
        return ms.ToArray();
    }

    static byte[] WritePlayerBp(ScenarioPlayer pl)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(pl.Version);

        SubSection(bw, "P1", w =>
        {
            w.Write(pl.Id);
            w.Write(pl.P1Pad);
            WriteString16(w, pl.StrId);
            WriteString16(w, pl.Name);
            WriteString16(w, pl.NameStrId);
            w.Write(pl.P1Unk4);
            WriteString16(w, pl.StrId2);
            w.Write(pl.EndId);
            w.Write(pl.P1Tail);
        });

        SubSection(bw, "P2", w =>
        {
            w.Write(pl.P2Magic);
            w.Write(pl.God);
            for (int i = 0; i < 8; i++) w.Write((byte)0xFF);
            w.Write(pl.P2Tail);
        });

        SubSection(bw, "P3", w =>
        {
            w.Write(pl.StartAge);
            w.Write(pl.P3Unk2);
            w.Write(pl.ClassGod);
            w.Write(pl.HeroicGod);
            w.Write(pl.MythicGod);
            w.Write(pl.MaxAge);
            w.Write(pl.PopLimit);
            w.Write(pl.InitPopCap);
            w.Write(pl.P3Tail);
        });

        WriteSubSection(bw, "P4", pl.P4Raw);
        WriteSubSection(bw, "P5", pl.P5Raw);

        SubSection(bw, "P6", w =>
        {
            WriteInt32List(w, pl.Diplomacy);
            WriteInt32List(w, pl.List2);
            w.Write(pl.ResMagic);
            w.Write(pl.Gold);
            w.Write(pl.Wood);
            w.Write(pl.Food);
            w.Write(pl.Favor);
            w.Write(pl.ResTotal);
            for (int i = 0; i < 17; i++) w.Write((byte)0);
            w.Write(pl.P6Tail);
        });

        bw.Write(pl.TailRaw);
        return ms.ToArray();
    }

    static void SubSection(BinaryWriter bw, string marker, Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        body(w);
        WriteSubSection(bw, marker, ms.ToArray());
    }

    static void WriteInt32List(BinaryWriter bw, List<int> values)
    {
        bw.Write((uint)values.Count);
        foreach (var v in values) bw.Write(v);
    }
}
