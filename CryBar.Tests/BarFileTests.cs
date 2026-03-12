using System.Buffers.Binary;
using System.Text;

using CryBar;

namespace CryBar.Tests;

public class BarFileTests
{
    #region Helper Methods

    /// <summary>
    /// Creates a minimal valid BAR v6 file in memory with the given entries.
    /// </summary>
    static MemoryStream CreateValidBarStream(string rootName, params (string path, byte[] content)[] files)
    {
        var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        // --- HEADER (292 bytes total) ---
        // Signature: ESPN
        writer.Write((byte)0x45); writer.Write((byte)0x53);
        writer.Write((byte)0x50); writer.Write((byte)0x4E);

        // Version: 6
        writer.Write((uint)6);

        // id1: 1144201745
        writer.Write((uint)1144201745);

        // Padding: 264 bytes
        writer.Write(new byte[264]);

        // Checksum (dummy)
        writer.Write((uint)0);

        // File count
        writer.Write(files.Length);

        // id2: 0
        writer.Write((uint)0);

        // File table offset placeholder (will fill later)
        long fileTableOffsetPos = ms.Position;
        writer.Write((long)0);

        // --- FILE CONTENT DATA ---
        // Write each file's content and record offsets
        var contentOffsets = new long[files.Length];
        for (int i = 0; i < files.Length; i++)
        {
            contentOffsets[i] = ms.Position;
            writer.Write(files[i].content);
        }

        // --- FILE TABLE ---
        long fileTableOffset = ms.Position;

        // Go back and write the file table offset
        ms.Seek(fileTableOffsetPos, SeekOrigin.Begin);
        writer.Write(fileTableOffset);
        ms.Seek(fileTableOffset, SeekOrigin.Begin);

        // Root name length (in Unicode chars)
        writer.Write(rootName.Length);
        // Root name (Unicode)
        writer.Write(Encoding.Unicode.GetBytes(rootName));
        // Root files count
        writer.Write(files.Length);

        // File entries
        for (int i = 0; i < files.Length; i++)
        {
            var (path, content) = files[i];
            int size = content.Length;

            // content_offset (int64)
            writer.Write(contentOffsets[i]);
            // size_uncompressed (int32)
            writer.Write(size);
            // size_compressed (int32) - same as uncompressed for uncompressed files
            writer.Write(size);
            // size_archive (int32) - same as uncompressed
            writer.Write(size);
            // file_name_length (int32) - in Unicode chars
            writer.Write(path.Length);
            // file_name (Unicode)
            writer.Write(Encoding.Unicode.GetBytes(path));
            // is_compressed (uint32) - 0 = not compressed
            writer.Write((uint)0);
        }

        ms.Seek(0, SeekOrigin.Begin);
        return ms;
    }

    #endregion

    #region Load Error Tests

    [Fact]
    public void Load_TooSmallStream_ReturnsFileTooSmall()
    {
        var ms = new MemoryStream(new byte[10]);
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.False(result);
        Assert.Equal(BarFileLoadError.FileTooSmall, error);
    }

    [Fact]
    public void Load_ExactHeaderSize_ReturnsFileTooSmall()
    {
        // HEADER_SIZE is 292, stream must be > HEADER_SIZE
        var ms = new MemoryStream(new byte[292]);
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.False(result);
        Assert.Equal(BarFileLoadError.FileTooSmall, error);
    }

    [Fact]
    public void Load_InvalidHeader_ReturnsInvalidBARHeader()
    {
        var data = new byte[300];
        // Write garbage instead of ESPN
        data[0] = 0xFF; data[1] = 0xFF; data[2] = 0xFF; data[3] = 0xFF;

        var ms = new MemoryStream(data);
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.False(result);
        Assert.Equal(BarFileLoadError.InvalidBARHeader, error);
    }

    [Fact]
    public void Load_WrongVersion_ReturnsUnsupportedBARVersion()
    {
        var data = new byte[300];
        // ESPN header
        data[0] = 0x45; data[1] = 0x53; data[2] = 0x50; data[3] = 0x4E;
        // Version = 5 (unsupported, only 6 is supported)
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 5);

        var ms = new MemoryStream(data);
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.False(result);
        Assert.Equal(BarFileLoadError.UnsupportedBARVersion, error);
    }

    [Fact]
    public void Load_WrongId1_ReturnsInvalidBARFormat()
    {
        var data = new byte[300];
        // ESPN
        data[0] = 0x45; data[1] = 0x53; data[2] = 0x50; data[3] = 0x4E;
        // Version 6
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 6);
        // Wrong id1
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 999);

        var ms = new MemoryStream(data);
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.False(result);
        Assert.Equal(BarFileLoadError.InvalidBARFormat, error);
    }

    [Fact]
    public void Load_NonSeekableStream_ReturnsStreamNotSeekable()
    {
        var bar = new BarFile(new NonSeekableStream());

        var result = bar.Load(out var error);

        Assert.False(result);
        Assert.Equal(BarFileLoadError.StreamNotSeekable, error);
    }

    [Fact]
    public void Load_AlreadyLoaded_ReturnsAlreadyLoaded()
    {
        using var ms = CreateValidBarStream("root", ("test.txt", "hello"u8.ToArray()));
        var bar = new BarFile(ms);

        var result1 = bar.Load(out var error1);
        Assert.True(result1);
        Assert.Equal(BarFileLoadError.None, error1);

        var result2 = bar.Load(out var error2);
        Assert.False(result2);
        Assert.Equal(BarFileLoadError.AlreadyLoaded, error2);
    }

    #endregion

    #region Successful Load Tests

    [Fact]
    public void Load_ValidBarFile_SingleEntry_LoadsSuccessfully()
    {
        byte[] content = "Hello, BAR!"u8.ToArray();
        using var ms = CreateValidBarStream("game\\art", ("textures\\test.txt", content));
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.True(result);
        Assert.Equal(BarFileLoadError.None, error);
        Assert.True(bar.Loaded);
        Assert.Equal((uint)6, bar.Version);
        Assert.Equal("game\\art", bar.RootPath);
        Assert.NotNull(bar.Entries);
        Assert.Single(bar.Entries);

        var entry = bar.Entries[0];
        Assert.Equal("textures\\test.txt", entry.RelativePath);
        Assert.Equal("test.txt", entry.Name);
        Assert.Equal(content.Length, entry.SizeUncompressed);
        Assert.Equal(content.Length, entry.SizeInArchive);
        Assert.False(entry.IsCompressed);
    }

    [Fact]
    public void Load_ValidBarFile_MultipleEntries_LoadsSuccessfully()
    {
        byte[] content1 = "File one"u8.ToArray();
        byte[] content2 = "File two content"u8.ToArray();
        byte[] content3 = "Third file data!!!"u8.ToArray();

        using var ms = CreateValidBarStream("root",
            ("dir\\file1.txt", content1),
            ("dir\\file2.xml", content2),
            ("other\\file3.dat", content3));

        var bar = new BarFile(ms);
        var result = bar.Load(out var error);

        Assert.True(result);
        Assert.Equal(3, bar.Entries!.Count);

        Assert.Equal("dir\\file1.txt", bar.Entries[0].RelativePath);
        Assert.Equal(content1.Length, bar.Entries[0].SizeInArchive);

        Assert.Equal("dir\\file2.xml", bar.Entries[1].RelativePath);
        Assert.Equal(content2.Length, bar.Entries[1].SizeInArchive);

        Assert.Equal("other\\file3.dat", bar.Entries[2].RelativePath);
        Assert.Equal(content3.Length, bar.Entries[2].SizeInArchive);
    }

    [Fact]
    public void Load_ValidBarFile_ZeroEntries_LoadsSuccessfully()
    {
        using var ms = CreateValidBarStream("root");
        var bar = new BarFile(ms);

        var result = bar.Load(out var error);

        Assert.True(result);
        Assert.NotNull(bar.Entries);
        Assert.Empty(bar.Entries);
    }

    [Fact]
    public void Load_ValidBarFile_EntryContentReadable()
    {
        byte[] content = "Readable content"u8.ToArray();
        using var ms = CreateValidBarStream("root", ("file.txt", content));
        var bar = new BarFile(ms);
        bar.Load(out _);

        var entry = bar.Entries![0];
        var readBack = entry.ReadDataRaw(ms);

        Assert.Equal(content, readBack);
    }

    #endregion

    #region Helper Classes

    /// <summary>
    /// A stream wrapper that reports CanSeek = false
    /// </summary>
    class NonSeekableStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    #endregion
}
