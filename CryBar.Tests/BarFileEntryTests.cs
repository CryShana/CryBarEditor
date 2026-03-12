using CryBar;

namespace CryBar.Tests;

public class BarFileEntryTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_SimpleFileName_SetsNameAndEmptyDirectory()
    {
        var entry = new BarFileEntry("file.txt");

        Assert.Equal("file.txt", entry.Name);
        Assert.Equal("file.txt", entry.RelativePath);
        Assert.Equal("", entry.DirectoryPath);
    }

    [Fact]
    public void Constructor_PathWithDirectory_SplitsCorrectly()
    {
        var entry = new BarFileEntry("art\\textures\\image.ddt");

        Assert.Equal("image.ddt", entry.Name);
        Assert.Equal("art\\textures\\image.ddt", entry.RelativePath);
        Assert.Equal("art\\textures\\", entry.DirectoryPath);
    }

    [Fact]
    public void Constructor_SingleDirectory_SplitsCorrectly()
    {
        var entry = new BarFileEntry("data\\config.xml");

        Assert.Equal("config.xml", entry.Name);
        Assert.Equal("data\\config.xml", entry.RelativePath);
        Assert.Equal("data\\", entry.DirectoryPath);
    }

    [Fact]
    public void Constructor_DeeplyNested_SplitsCorrectly()
    {
        var entry = new BarFileEntry("a\\b\\c\\d\\e\\file.bin");

        Assert.Equal("file.bin", entry.Name);
        Assert.Equal("a\\b\\c\\d\\e\\", entry.DirectoryPath);
    }

    #endregion

    #region ReadDataRaw Tests

    [Fact]
    public void ReadDataRaw_ReadsCorrectBytes()
    {
        // Set up a stream with known content at a specific offset
        var streamData = new byte[100];
        byte[] expectedContent = [10, 20, 30, 40, 50];
        Array.Copy(expectedContent, 0, streamData, 25, expectedContent.Length);

        var ms = new MemoryStream(streamData);

        var entry = new BarFileEntry("test.bin")
        {
            ContentOffset = 25,
            SizeInArchive = 5,
            SizeUncompressed = 5
        };

        var result = entry.ReadDataRaw(ms);

        Assert.Equal(expectedContent, result);
    }

    [Fact]
    public void ReadDataRaw_SpanOverload_ReadsCorrectBytes()
    {
        var streamData = new byte[100];
        byte[] expectedContent = [0xAA, 0xBB, 0xCC];
        Array.Copy(expectedContent, 0, streamData, 50, expectedContent.Length);

        var ms = new MemoryStream(streamData);

        var entry = new BarFileEntry("test.bin")
        {
            ContentOffset = 50,
            SizeInArchive = 3,
            SizeUncompressed = 3
        };

        var buffer = new byte[3];
        entry.ReadDataRaw(ms, buffer);

        Assert.Equal(expectedContent, buffer);
    }

    [Fact]
    public void ReadDataRaw_DifferentOffsets_ReadsCorrectly()
    {
        var streamData = new byte[200];
        // Write identifiable patterns at different offsets
        streamData[0] = 0x01;
        streamData[100] = 0x02;
        streamData[150] = 0x03;

        var ms = new MemoryStream(streamData);

        var entry1 = new BarFileEntry("a.bin") { ContentOffset = 0, SizeInArchive = 1 };
        var entry2 = new BarFileEntry("b.bin") { ContentOffset = 100, SizeInArchive = 1 };
        var entry3 = new BarFileEntry("c.bin") { ContentOffset = 150, SizeInArchive = 1 };

        Assert.Equal([0x01], entry1.ReadDataRaw(ms));
        Assert.Equal([0x02], entry2.ReadDataRaw(ms));
        Assert.Equal([0x03], entry3.ReadDataRaw(ms));
    }

    #endregion

    #region CopyData Tests

    [Fact]
    public void CopyData_CopiesCorrectNumberOfBytes()
    {
        var sourceData = new byte[100];
        byte[] content = [1, 2, 3, 4, 5, 6, 7, 8];
        Array.Copy(content, 0, sourceData, 30, content.Length);

        var source = new MemoryStream(sourceData);
        var dest = new MemoryStream();

        var entry = new BarFileEntry("test.bin")
        {
            ContentOffset = 30,
            SizeInArchive = 8
        };

        entry.CopyData(source, dest);

        Assert.Equal(content, dest.ToArray());
    }

    [Fact]
    public void CopyData_LargerContent_CopiesCorrectly()
    {
        // Test with content larger than internal buffer (81920)
        int contentSize = 100_000;
        var sourceData = new byte[contentSize + 50];
        var expected = new byte[contentSize];
        new Random(42).NextBytes(expected);
        Array.Copy(expected, 0, sourceData, 50, contentSize);

        var source = new MemoryStream(sourceData);
        var dest = new MemoryStream();

        var entry = new BarFileEntry("large.bin")
        {
            ContentOffset = 50,
            SizeInArchive = contentSize
        };

        entry.CopyData(source, dest);

        Assert.Equal(expected, dest.ToArray());
    }

    [Fact]
    public void CopyData_DoesNotSeekDestination()
    {
        // Write some prefix data to dest, then copy
        var sourceData = new byte[50];
        byte[] content = [0xAA, 0xBB];
        sourceData[10] = 0xAA;
        sourceData[11] = 0xBB;

        var source = new MemoryStream(sourceData);
        var dest = new MemoryStream();

        // Write prefix
        dest.Write([0x01, 0x02, 0x03]);

        var entry = new BarFileEntry("test.bin")
        {
            ContentOffset = 10,
            SizeInArchive = 2
        };

        entry.CopyData(source, dest);

        byte[] result = dest.ToArray();
        Assert.Equal(5, result.Length);
        Assert.Equal(0x01, result[0]);
        Assert.Equal(0x02, result[1]);
        Assert.Equal(0x03, result[2]);
        Assert.Equal(0xAA, result[3]);
        Assert.Equal(0xBB, result[4]);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void ToString_ShowsPathAndSize()
    {
        var entry = new BarFileEntry("data\\test.xml")
        {
            SizeInArchive = 1234
        };

        var str = entry.ToString();
        Assert.Equal("data\\test.xml (1234 bytes)", str);
    }

    #endregion
}
