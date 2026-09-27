using System.Buffers.Binary;
using System.Text;
using CryBar.Scenario;

namespace CryBar.Tests;

public class ScenarioParsingHardeningTests
{
    #region Players (P9)

    const string EmptyP9PlayersXml = """
        <Scenario version="1">
          <Players unk1="0">
            <Player version="308">
              <P1 id="1" name="A" />
              <P2 god="1" />
              <P3 startAge="0" unk2="-1" classGod="0" heroicGod="0" mythicGod="0" maxAge="0" popLimit="-1" initPopCap="-1" />
              <P4 />
              <P5 color="1" />
              <P6 gold="0" wood="0" food="0" favor="0" />
              <P7 />
              <P8 />
              <P9 />
            </Player>
          </Players>
        </Scenario>
        """;

    [Fact]
    public void Players_EmptyP9_IsWrittenAsZeroLengthSubSection()
    {
        var scenario = ScenarioFile.FromXml(EmptyP9PlayersXml);
        Assert.True(scenario.Parsed);

        var pl = scenario.FindSection("PL");
        Assert.NotNull(pl);
        Assert.True(pl!.Data.AsSpan().EndsWith("P9\0\0\0\0"u8));
    }

    [Fact]
    public void Players_EmptyP9_RoundTripsByteExact()
    {
        var scenario = ScenarioFile.FromXml(EmptyP9PlayersXml);
        var bytes = scenario.ToBytes();

        var roundTripped = ScenarioFile.FromXml(scenario.ToXml()).ToBytes();

        Assert.Equal(bytes, roundTripped);
    }

    #endregion

    #region Narrowing parses

    [Fact]
    public void FromXml_EntityPlayerOutOfByteRange_Throws()
    {
        var xml = TestFixtures.MakeMinimalScenarioFile().ToXml();

        var entitiesIdx = xml.IndexOf("<Entities", StringComparison.Ordinal);
        Assert.True(entitiesIdx >= 0);
        var entityIdx = xml.IndexOf("<Entity ", entitiesIdx, StringComparison.Ordinal);
        Assert.True(entityIdx >= 0);
        var patched = SetAttribute(xml, entityIdx, "player", "256");

        Assert.Throws<OverflowException>(() => ScenarioFile.FromXml(patched));
    }

    [Fact]
    public void FromXml_W7SepOutOfByteRange_Throws()
    {
        var xml = TestFixtures.MakeMinimalScenarioFile().ToXml();

        var w7Idx = xml.IndexOf("<W7", StringComparison.Ordinal);
        Assert.True(w7Idx >= 0);
        var patched = SetAttribute(xml, w7Idx, "sep", "256");

        Assert.Throws<OverflowException>(() => ScenarioFile.FromXml(patched));
    }

    static string SetAttribute(string xml, int tagStart, string name, string value)
    {
        var tagEnd = xml.IndexOf('>', tagStart);
        var key = $" {name}=\"";
        var attrIdx = xml.IndexOf(key, tagStart, tagEnd - tagStart, StringComparison.Ordinal);
        if (attrIdx < 0)
        {
            var nameEnd = xml.IndexOfAny([' ', '/', '>'], tagStart + 1);
            return xml[..nameEnd] + key + value + "\"" + xml[nameEnd..];
        }

        var valueStart = attrIdx + key.Length;
        var valueEnd = xml.IndexOf('"', valueStart);
        return xml[..valueStart] + value + xml[valueEnd..];
    }

    #endregion

    #region Triggers

    [Fact]
    public void CanParseTr_NormalStringArg_Succeeds()
    {
        var data = BuildStandaloneTr("hello");

        Assert.True(ScenarioFile.CanParseTr(data, out _));
        Assert.Contains("hello", ScenarioFile.SectionToTriggersXml(new ScenarioSection("TR", data)));
    }

    [Fact]
    public void CanParseTr_StringArgOverMaxLength_IsRejected()
    {
        var data = BuildStandaloneTr(new string('x', 10_001));

        Assert.False(ScenarioFile.CanParseTr(data, out _));
        Assert.Throws<InvalidOperationException>(() => ScenarioFile.SectionToTriggersXml(new ScenarioSection("TR", data)));
    }

    [Fact]
    public void ToXml_OversizedTriggerString_FallsBackToRawAndRoundTrips()
    {
        var body = BuildStandaloneTr(new string('x', 10_001));
        var file = WrapTopLevelSection("TR", body);

        var scenario = new ScenarioFile(file);
        Assert.True(scenario.Parsed);

        var roundTripped = ScenarioFile.FromXml(scenario.ToXml()).ToBytes();

        Assert.Equal(file, roundTripped);
    }

    static byte[] BuildStandaloneTr(string argValue)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(11);
        bw.Write(0u); bw.Write(0u); bw.Write(0u);

        bw.Write(1u);
        bw.Write(9u); bw.Write(20_000u); bw.Write(0u); bw.Write(0u);
        TmmTestHelpers.WriteUTF16String(bw, "T");
        bw.Write(0);
        bw.Write(new byte[5]);
        TmmTestHelpers.WriteUTF16String(bw, "");

        bw.Write(1u);
        bw.Write(6u);
        WriteString8(bw, "c");
        WriteString8(bw, "c");
        bw.Write(1u);
        bw.Write(10u);
        WriteString8(bw, "k");
        WriteString8(bw, "k");
        bw.Write(0u);
        bw.Write(1);
        TmmTestHelpers.WriteUTF16String(bw, argValue);
        WriteString8(bw, "cmd");
        bw.Write(0u);
        bw.Write((ushort)0);

        bw.Write(0u);
        bw.Write(0u);

        bw.Flush();
        return ms.ToArray();
    }

    static void WriteString8(BinaryWriter bw, string value)
    {
        bw.Write(value.Length + 1);
        bw.Write(Encoding.Latin1.GetBytes(value));
        bw.Write((byte)0);
    }

    static byte[] WrapTopLevelSection(string marker, byte[] body)
    {
        var file = new byte[10 + 6 + body.Length + 1];
        file[0] = (byte)'B';
        file[1] = (byte)'G';
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), (uint)(file.Length - 7));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(6), 1u);
        file[10] = (byte)marker[0];
        file[11] = (byte)marker[1];
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(12), (uint)body.Length);
        body.CopyTo(file, 16);
        return file;
    }

    #endregion

    #region Terrain

    // TN body: hasT3(1) + "T3"(2) + u32 size, then the T3 body.
    const int T3BodyOffset = 7;

    // 0..5 = TileGroups, TileSubs, TilePT, WaterColors, WaterNames, WaterType; 6 = heightCount.
    static int T3ListOffset(byte[] tn, int index)
    {
        int off = T3BodyOffset + 4;
        off += 6 + (int)BinaryPrimitives.ReadUInt32LittleEndian(tn.AsSpan(off + 2));
        off += 16;
        for (int i = 0; i < index; i++)
            off += 6 + (int)BinaryPrimitives.ReadUInt32LittleEndian(tn.AsSpan(off + 2));
        return off;
    }

    static int TmSizeOffset(byte[] tn)
    {
        var t3Size = (int)BinaryPrimitives.ReadUInt32LittleEndian(tn.AsSpan(3));
        return T3BodyOffset + t3Size + 1 + 2;
    }

    static ScenarioFile FixtureWithPatchedTn(Action<byte[]> patch, out byte[] patchedTn)
    {
        var scenario = TestFixtures.MakeMinimalScenarioFile();
        var j1 = scenario.GetJ1()!;
        var tn = j1.FindSection("TN")!;
        patch(tn.Data);
        patchedTn = tn.Data;
        scenario.FindSection("J1")!.Data = j1.ToBytes();
        return scenario;
    }

    static void PatchU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    [Theory]
    [InlineData("t3Size")]
    [InlineData("tileGroupsCount")]
    [InlineData("tileSubsSize")]
    [InlineData("waterNamesSize")]
    [InlineData("waterTypeCount")]
    [InlineData("heightCount")]
    [InlineData("tmSize")]
    public void TryParse_CorruptTerrainCounts_ReturnsNull(string field)
    {
        var scenario = FixtureWithPatchedTn(tn =>
        {
            switch (field)
            {
                case "t3Size": PatchU32(tn, 3, 0xFFFFFFF0); break;
                case "tileGroupsCount": PatchU32(tn, T3ListOffset(tn, 0) + 6, 0xFFFFFFFF); break;
                case "tileSubsSize": PatchU32(tn, T3ListOffset(tn, 1) + 2, 0xFFFFFFF0); break;
                case "waterNamesSize": PatchU32(tn, T3ListOffset(tn, 4) + 2, 0x7FFFFFFF); break;
                case "waterTypeCount": PatchU32(tn, T3ListOffset(tn, 5) + 6, 0x7FFFFFFF); break;
                case "heightCount": PatchU32(tn, T3ListOffset(tn, 6), 0x7FFFFFFF); break;
                case "tmSize": PatchU32(tn, TmSizeOffset(tn), 0xFFFFFFF0); break;
            }
        }, out _);

        Assert.Null(ScenarioTerrain.TryParse(scenario));
    }

    [Fact]
    public void TryParse_HugeTerrainGroupCount_StopsAtEndOfData()
    {
        var expected = ScenarioTerrain.TryParse(TestFixtures.MakeMinimalScenarioFile());
        Assert.NotNull(expected);

        var scenario = FixtureWithPatchedTn(tn => PatchU32(tn, T3BodyOffset + 4 + 6 + 4, 0xFFFFFFFF), out _);

        var terrain = ScenarioTerrain.TryParse(scenario);
        Assert.NotNull(terrain);
        Assert.Equal(expected!.TerrainGroups.Length, terrain!.TerrainGroups.Length);
    }

    [Fact]
    public void ToXml_CorruptTnSizeList_FallsBackToRawAndPreservesBytes()
    {
        var scenario = FixtureWithPatchedTn(tn => PatchU32(tn, T3ListOffset(tn, 1) + 2, 0xFFFFFFF0), out var patchedTn);

        var fromXml = ScenarioFile.FromXml(scenario.ToXml());

        var tn = fromXml.GetJ1()!.FindSection("TN");
        Assert.NotNull(tn);
        Assert.Equal(patchedTn, tn!.Data);
    }

    #endregion

    #region Entities (P1 tail)

    [Fact]
    public void WalkP1Tail_ValidBdSection_Succeeds()
    {
        var p1 = new byte[77 + 6 + 2 + 4 + 1 + 4];
        p1[76] = 1;
        p1[77] = (byte)'B';
        p1[78] = (byte)'D';
        PatchU32(p1, 79, 2);

        Assert.True(ScenarioFile.WalkP1Tail(p1, out var p1End, out var resourceOff));
        Assert.Equal(p1.Length, p1End);
        Assert.Equal(-1, resourceOff);
    }

    [Theory]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(0x7FFFFFF0u)]
    [InlineData(100u)]
    public void WalkP1Tail_InvalidBdSize_ReturnsFalse(uint bdSize)
    {
        var p1 = new byte[120];
        p1[76] = 1;
        p1[77] = (byte)'B';
        p1[78] = (byte)'D';
        PatchU32(p1, 79, bdSize);

        Assert.False(ScenarioFile.WalkP1Tail(p1, out var p1End, out _));
        Assert.Equal(-1, p1End);
    }

    #endregion
}
