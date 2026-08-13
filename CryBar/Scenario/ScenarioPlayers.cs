using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace CryBar.Scenario;

/// <summary>
/// Editable view over one BP player block in the PL section. Only fields the
/// editor exposes are parsed into typed members; every other byte region is
/// preserved verbatim so an unedited player re-encodes byte-identical.
/// </summary>
public sealed class ScenarioPlayer
{
    public int Version { get; set; }

    // P1
    public uint Id { get; set; }
    public byte P1Pad { get; set; }
    public string StrId { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameStrId { get; set; } = "";
    public byte P1Unk4 { get; set; }
    public string StrId2 { get; set; } = "";
    public uint EndId { get; set; }
    public byte[] P1Tail { get; set; } = [];

    // P2
    public uint P2Magic { get; set; }
    public uint God { get; set; }
    public byte[] P2Tail { get; set; } = [];

    // P3
    public uint StartAge { get; set; }
    public int P3Unk2 { get; set; }
    public uint ClassGod { get; set; }
    public uint HeroicGod { get; set; }
    public uint MythicGod { get; set; }
    public uint MaxAge { get; set; }
    public int PopLimit { get; set; }
    public int InitPopCap { get; set; }
    public byte[] P3Tail { get; set; } = [];

    // P4/P5 are not editable; raw content preserved. AiPath/Color are
    // parsed out of P5 for display only and never written back.
    public byte[] P4Raw { get; set; } = [];
    public byte[] P5Raw { get; set; } = [];
    public string AiPath { get; init; } = "";
    public uint Color { get; init; }

    // P6
    public List<int> Diplomacy { get; set; } = [];
    public List<int> List2 { get; set; } = [];
    public int ResMagic { get; set; }
    public float Gold { get; set; }
    public float Wood { get; set; }
    public float Food { get; set; }
    public float Favor { get; set; }
    public float ResTotal { get; set; }
    public byte[] P6Tail { get; set; } = [];

    // Everything from the P7 marker to the end of the BP block (P7-P9 + BpTail).
    public byte[] TailRaw { get; set; } = [];
}

public sealed class ScenarioPlayersView
{
    public uint Unk1 { get; set; }
    public List<ScenarioPlayer> Players { get; } = [];
    public byte[] PlTail { get; set; } = [];
}
