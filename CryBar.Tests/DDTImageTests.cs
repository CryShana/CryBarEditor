using System.Buffers.Binary;

using CryBar;

namespace CryBar.Tests;

public class DDTImageTests
{
    #region GetMaxMinmapLevels Tests

    [Theory]
    [InlineData(256, 256, 7)]   // 256 -> 128 -> 64 -> 32 -> 16 -> 8 -> 4 = 7 levels (including base)
    [InlineData(4, 4, 1)]       // 4 is the stop condition, so just 1 level (base)
    [InlineData(1, 1, 1)]       // size=1 which is <=4, so 1 level
    [InlineData(2, 2, 1)]       // size=2 which is <=4, so 1 level
    [InlineData(8, 8, 2)]       // 8 -> 4 = 2 levels
    [InlineData(16, 16, 3)]     // 16 -> 8 -> 4 = 3 levels
    [InlineData(1024, 1024, 9)] // 1024 -> 512 -> 256 -> 128 -> 64 -> 32 -> 16 -> 8 -> 4 = 9
    [InlineData(512, 256, 7)]   // min(512,256) = 256, same as 256x256
    [InlineData(1024, 4, 1)]    // min(1024,4) = 4, so 1 level
    [InlineData(32, 64, 4)]     // min(32,64) = 32 -> 16 -> 8 -> 4 = 4 levels
    [InlineData(0, 0, 0)]       // size <= 0 returns 0
    [InlineData(-1, 100, 0)]    // negative returns 0
    [InlineData(100, -1, 0)]    // negative returns 0
    [InlineData(5, 5, 2)]       // 5 -> (5>>1=2, but 5>4 so level++) = 2 levels
    [InlineData(3, 3, 1)]       // 3 <= 4, so 1 level
    public void GetMaxMinmapLevels_VariousSizes(int width, int height, byte expected)
    {
        var result = DDTImage.GetMaxMinmapLevels(width, height);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetMaxMinmapLevels_LargePowerOfTwo()
    {
        // 2048 -> 1024 -> 512 -> 256 -> 128 -> 64 -> 32 -> 16 -> 8 -> 4 = 10
        Assert.Equal(10, DDTImage.GetMaxMinmapLevels(2048, 2048));
    }

    [Fact]
    public void GetMaxMinmapLevels_NonSquare_UsesSmallestDimension()
    {
        // min(2048, 16) = 16 -> 8 -> 4 = 3
        Assert.Equal(3, DDTImage.GetMaxMinmapLevels(2048, 16));
    }

    #endregion

    #region ParseHeader Tests

    [Fact]
    public void ParseHeader_EmptyData_ReturnsFalse()
    {
        var ddt = new DDTImage(ReadOnlyMemory<byte>.Empty);
        Assert.False(ddt.ParseHeader());
        Assert.False(ddt.HeaderParsed);
    }

    [Fact]
    public void ParseHeader_TooShort_ReturnsFalse()
    {
        var ddt = new DDTImage(new byte[] { 0x52, 0x54, 0x53, 0x34 }); // just "RTS4", not enough data
        Assert.False(ddt.ParseHeader());
    }

    [Fact]
    public void ParseHeader_InvalidSignature_ReturnsFalse()
    {
        var data = new byte[100];
        data[0] = 0xFF; data[1] = 0xFF; data[2] = 0xFF; data[3] = 0xFF;
        var ddt = new DDTImage(data);
        Assert.False(ddt.ParseHeader());
    }

    [Fact]
    public void ParseHeader_ValidRTS4Header_ParsesCorrectly()
    {
        var data = CreateSyntheticRTS4DDT(
            usage: DDTUsage.None,
            alpha: DDTAlpha.None,
            format: DDTFormat.DXT1,
            mipmapLevels: 3,
            width: 256,
            height: 128,
            colorTableSize: 0);

        var ddt = new DDTImage(data);
        var result = ddt.ParseHeader();

        Assert.True(result);
        Assert.True(ddt.HeaderParsed);
        Assert.Equal(DDTVersion.RTS4, ddt.Version);
        Assert.Equal(DDTUsage.None, ddt.UsageFlag);
        Assert.Equal(DDTAlpha.None, ddt.AlphaFlag);
        Assert.Equal(DDTFormat.DXT1, ddt.FormatFlag);
        Assert.Equal(3, ddt.MipmapLevels);
        Assert.Equal(256, ddt.BaseWidth);
        Assert.Equal(128, ddt.BaseHeight);
        Assert.NotNull(ddt.MipmapOffsets);
        Assert.Equal(3, ddt.MipmapOffsets.Length);
    }

    [Fact]
    public void ParseHeader_ValidRTS3Header_ParsesCorrectly()
    {
        var data = CreateSyntheticRTS3DDT(
            usage: DDTUsage.AlphaTest,
            alpha: DDTAlpha.Transparent,
            format: DDTFormat.DXT5,
            mipmapLevels: 2,
            width: 64,
            height: 64);

        var ddt = new DDTImage(data);
        var result = ddt.ParseHeader();

        Assert.True(result);
        Assert.True(ddt.HeaderParsed);
        Assert.Equal(DDTVersion.RTS3, ddt.Version);
        Assert.Equal(DDTUsage.AlphaTest, ddt.UsageFlag);
        Assert.Equal(DDTAlpha.Transparent, ddt.AlphaFlag);
        Assert.Equal(DDTFormat.DXT5, ddt.FormatFlag);
        Assert.Equal(2, ddt.MipmapLevels);
        Assert.Equal(64, ddt.BaseWidth);
        Assert.Equal(64, ddt.BaseHeight);
    }

    [Fact]
    public void ParseHeader_RTS4WithColorTable_ParsesColorTable()
    {
        int colorTableSize = 64;
        var data = CreateSyntheticRTS4DDT(
            usage: DDTUsage.None,
            alpha: DDTAlpha.None,
            format: DDTFormat.DXT1,
            mipmapLevels: 1,
            width: 16,
            height: 16,
            colorTableSize: colorTableSize);

        var ddt = new DDTImage(data);
        var result = ddt.ParseHeader();

        Assert.True(result);
        Assert.NotNull(ddt.ColorTable);
        Assert.Equal(colorTableSize, ddt.ColorTable.Value.Length);
    }

    [Fact]
    public void ParseHeader_MipmapDimensionsCalculatedCorrectly()
    {
        // 3 mipmap levels for 128x64:
        // level 0: 128x64, level 1: 64x32, level 2: 32x16
        var data = CreateSyntheticRTS4DDT(
            usage: DDTUsage.None,
            alpha: DDTAlpha.None,
            format: DDTFormat.DXT1,
            mipmapLevels: 3,
            width: 128,
            height: 64,
            colorTableSize: 0);

        var ddt = new DDTImage(data);
        ddt.ParseHeader();

        Assert.NotNull(ddt.MipmapOffsets);
        Assert.Equal(3, ddt.MipmapOffsets.Length);

        // Check dimensions (Item3 = width, Item4 = height)
        Assert.Equal(128, ddt.MipmapOffsets[0].Item3);
        Assert.Equal(64, ddt.MipmapOffsets[0].Item4);

        Assert.Equal(64, ddt.MipmapOffsets[1].Item3);
        Assert.Equal(32, ddt.MipmapOffsets[1].Item4);

        Assert.Equal(32, ddt.MipmapOffsets[2].Item3);
        Assert.Equal(16, ddt.MipmapOffsets[2].Item4);
    }

    #endregion

    #region Helper Methods

    static byte[] CreateSyntheticRTS4DDT(DDTUsage usage, DDTAlpha alpha, DDTFormat format,
        byte mipmapLevels, ushort width, ushort height, int colorTableSize)
    {
        // Calculate size needed
        int headerSize = 4 + 4 + 4 + 4; // signature + usage/alpha/format/mipmap + width + height
        headerSize += 4 + colorTableSize; // color table size + color table data (RTS4 only)
        headerSize += mipmapLevels * 8; // mipmap offset+length pairs
        int dataOffset = headerSize;

        // Add some dummy mipmap data
        int totalMipmapData = 0;
        for (int i = 0; i < mipmapLevels; i++)
            totalMipmapData += 16; // 16 bytes per mipmap (dummy)

        var data = new byte[headerSize + totalMipmapData];
        var offset = 0;

        // RTS4 signature
        data[offset++] = 0x52; data[offset++] = 0x54;
        data[offset++] = 0x53; data[offset++] = 0x34;

        // Usage, Alpha, Format, MipmapLevels
        data[offset++] = (byte)usage;
        data[offset++] = (byte)alpha;
        data[offset++] = (byte)format;
        data[offset++] = mipmapLevels;

        // Width (int32)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), width); offset += 4;
        // Height (int32)
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), height); offset += 4;

        // Color table
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), colorTableSize); offset += 4;
        offset += colorTableSize; // skip color table bytes (zeroed)

        // Mipmap offset/length pairs
        int mipmapDataPos = headerSize;
        for (int i = 0; i < mipmapLevels; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), mipmapDataPos); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 16); offset += 4;
            mipmapDataPos += 16;
        }

        return data;
    }

    static byte[] CreateSyntheticRTS3DDT(DDTUsage usage, DDTAlpha alpha, DDTFormat format,
        byte mipmapLevels, ushort width, ushort height)
    {
        int headerSize = 4 + 4 + 4 + 4; // signature + flags + width + height
        headerSize += mipmapLevels * 8; // mipmap offset+length pairs
        int totalMipmapData = mipmapLevels * 16;

        var data = new byte[headerSize + totalMipmapData];
        var offset = 0;

        // RTS3 signature
        data[offset++] = 0x52; data[offset++] = 0x54;
        data[offset++] = 0x53; data[offset++] = 0x33;

        data[offset++] = (byte)usage;
        data[offset++] = (byte)alpha;
        data[offset++] = (byte)format;
        data[offset++] = mipmapLevels;

        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), width); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), height); offset += 4;

        int mipmapDataPos = headerSize;
        for (int i = 0; i < mipmapLevels; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), mipmapDataPos); offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(offset), 16); offset += 4;
            mipmapDataPos += 16;
        }

        return data;
    }

    #endregion
}
