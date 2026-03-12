using System.Buffers.Binary;
using System.Text;

using CryBar;

namespace CryBar.Tests;

public class TmmModelTests
{
    #region ParseHeader Tests

    [Fact]
    public void ParseHeader_EmptyData_ReturnsFalse()
    {
        var tmm = new TmmModel(ReadOnlyMemory<byte>.Empty);
        Assert.False(tmm.ParseHeader());
        Assert.False(tmm.HeaderParsed);
    }

    [Fact]
    public void ParseHeader_TooShort_ReturnsFalse()
    {
        var tmm = new TmmModel(new byte[10]);
        Assert.False(tmm.ParseHeader());
    }

    [Fact]
    public void ParseHeader_InvalidSignature_ReturnsFalse()
    {
        var data = new byte[100];
        data[0] = 0xFF; data[1] = 0xFF; data[2] = 0xFF; data[3] = 0xFF;

        var tmm = new TmmModel(data);
        Assert.False(tmm.ParseHeader());
    }

    [Fact]
    public void ParseHeader_BTMMSignatureButMissingDP_ReturnsFalse()
    {
        var data = new byte[100];
        // BTMM
        data[0] = 0x42; data[1] = 0x54; data[2] = 0x4d; data[3] = 0x4d;
        // version = 34 (valid)
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 34);
        // Not DP
        data[8] = 0x00; data[9] = 0x00;

        var tmm = new TmmModel(data);
        Assert.False(tmm.ParseHeader());
    }

    [Fact]
    public void ParseHeader_InvalidVersion_ReturnsFalse()
    {
        var data = new byte[100];
        // BTMM
        data[0] = 0x42; data[1] = 0x54; data[2] = 0x4d; data[3] = 0x4d;
        // version = 10 (too low, valid range 30-255)
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 10);

        var tmm = new TmmModel(data);
        Assert.False(tmm.ParseHeader());
    }

    [Fact]
    public void ParseHeader_ValidHeader_NoModels()
    {
        var data = CreateSyntheticTMM(34, 100, []);

        var tmm = new TmmModel(data);
        var result = tmm.ParseHeader();

        Assert.True(result);
        Assert.True(tmm.HeaderParsed);
        Assert.NotNull(tmm.ModelNames);
        Assert.Empty(tmm.ModelNames);
        Assert.Equal(100, tmm.DataOffset);
    }

    [Fact]
    public void ParseHeader_ValidHeader_SingleModel()
    {
        var data = CreateSyntheticTMM(34, 200, ["hero_model"]);

        var tmm = new TmmModel(data);
        var result = tmm.ParseHeader();

        Assert.True(result);
        Assert.NotNull(tmm.ModelNames);
        Assert.Single(tmm.ModelNames);
        Assert.Equal("hero_model", tmm.ModelNames[0]);
    }

    [Fact]
    public void ParseHeader_ValidHeader_MultipleModels()
    {
        string[] names = ["model_a", "model_b", "model_c"];
        var data = CreateSyntheticTMM(35, 500, names);

        var tmm = new TmmModel(data);
        var result = tmm.ParseHeader();

        Assert.True(result);
        Assert.NotNull(tmm.ModelNames);
        Assert.Equal(3, tmm.ModelNames.Length);
        Assert.Equal("model_a", tmm.ModelNames[0]);
        Assert.Equal("model_b", tmm.ModelNames[1]);
        Assert.Equal("model_c", tmm.ModelNames[2]);
    }

    [Fact]
    public void ParseHeader_DataOffsetIsSet()
    {
        int expectedDataOffset = 12345;
        var data = CreateSyntheticTMM(34, expectedDataOffset, ["test"]);

        var tmm = new TmmModel(data);
        tmm.ParseHeader();

        Assert.Equal(expectedDataOffset, tmm.DataOffset);
    }

    [Fact]
    public void ParseHeader_TooManyModels_ReturnsFalse()
    {
        var data = new byte[100];
        var offset = 0;

        // BTMM
        data[offset++] = 0x42; data[offset++] = 0x54;
        data[offset++] = 0x4d; data[offset++] = 0x4d;

        // version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), 34); offset += 4;

        // DP
        data[offset++] = 0x44; data[offset++] = 0x50;

        // data_offset
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 100); offset += 4;

        // model_count = 1001 (exceeds limit of 1000)
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), 1001); offset += 4;

        var tmm = new TmmModel(data);
        Assert.False(tmm.ParseHeader());
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a synthetic TMM byte array with the given parameters.
    /// </summary>
    static byte[] CreateSyntheticTMM(uint version, int dataOffset, string[] modelNames)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);

        // BTMM signature
        writer.Write((byte)0x42); writer.Write((byte)0x54);
        writer.Write((byte)0x4d); writer.Write((byte)0x4d);

        // Version
        writer.Write(version);

        // DP
        writer.Write((byte)0x44); writer.Write((byte)0x50);

        // Data offset
        writer.Write(dataOffset);

        // Model count
        writer.Write((uint)modelNames.Length);

        // Model names
        foreach (var name in modelNames)
        {
            // Name length (in Unicode chars)
            writer.Write(name.Length);
            // Name (Unicode)
            writer.Write(Encoding.Unicode.GetBytes(name));
            // 4 unknown int32 values after each name
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
        }

        return ms.ToArray();
    }

    #endregion
}
