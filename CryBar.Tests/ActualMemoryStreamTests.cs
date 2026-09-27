using CryBar.Utilities;

namespace CryBar.Tests;

public class ActualMemoryStreamTests
{
    [Fact]
    public void Seek_End_NegativeOffset_PositionsBeforeEnd()
    {
        var stream = new ActualMemoryStream(new byte[10]);

        var pos = stream.Seek(-3, SeekOrigin.End);

        Assert.Equal(7, pos);
        Assert.Equal(7, stream.Position);
    }

    [Fact]
    public void Seek_End_ZeroOffset_PositionsAtEnd()
    {
        var stream = new ActualMemoryStream(new byte[10]);

        Assert.Equal(10, stream.Seek(0, SeekOrigin.End));
        Assert.Equal(0, stream.Read(new byte[4], 0, 4));
    }

    [Fact]
    public void Seek_End_PositiveOffset_PositionsPastEnd()
    {
        var stream = new ActualMemoryStream(new byte[10]);

        Assert.Equal(12, stream.Seek(2, SeekOrigin.End));
        Assert.Equal(0, stream.Read(new byte[4]));
    }

    [Fact]
    public void Seek_End_ReadsTrailingBytes()
    {
        var stream = new ActualMemoryStream(new byte[] { 1, 2, 3, 4, 5 });
        var buffer = new byte[2];

        stream.Seek(-2, SeekOrigin.End);
        var read = stream.Read(buffer, 0, 2);

        Assert.Equal(2, read);
        Assert.Equal(new byte[] { 4, 5 }, buffer);
    }

    [Fact]
    public void Seek_BeforeBeginning_ThrowsIOException()
    {
        var stream = new ActualMemoryStream(new byte[10]);

        Assert.Throws<IOException>(() => stream.Seek(-11, SeekOrigin.End));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Current));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void Write_ExactlyFillsBuffer_Succeeds()
    {
        var backing = new byte[4];
        var stream = new ActualMemoryStream(backing);

        stream.Write(new byte[] { 1, 2, 3, 4 });

        Assert.Equal(4, stream.Position);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, backing);
    }

    [Fact]
    public void Write_BeyondCapacity_ThrowsAndWritesNothing()
    {
        var backing = new byte[4];
        var stream = new ActualMemoryStream(backing);
        stream.Write(new byte[] { 1, 2, 3 });

        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 4, 5 }));
        Assert.Throws<NotSupportedException>(() => stream.Write(new byte[] { 4, 5 }, 0, 2));

        Assert.Equal(3, stream.Position);
        Assert.Equal(new byte[] { 1, 2, 3, 0 }, backing);
    }

    [Fact]
    public void Write_PastEndAfterSeek_Throws()
    {
        var stream = new ActualMemoryStream(new byte[4]);
        stream.Seek(1, SeekOrigin.End);

        Assert.Throws<NotSupportedException>(() => stream.WriteByte(1));
    }
}
