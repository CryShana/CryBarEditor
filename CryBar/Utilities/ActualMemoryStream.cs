namespace CryBar.Utilities;

public class ActualMemoryStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public Memory<byte> Buffer => _buffer;

    int _position = 0;
    readonly Memory<byte> _buffer;

    public ActualMemoryStream(Memory<byte> underlying_buffer)
    {
        _buffer = underlying_buffer;
    }

    public override void Flush() {}
    public override long Seek(long offset, SeekOrigin origin)
    {
        if (offset > int.MaxValue || offset < int.MinValue)
            throw new NotSupportedException("Only valid Int32 offsets are accepted");

        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => (long)_position + offset,
            SeekOrigin.End => (long)_buffer.Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };

        if (target < 0)
            throw new IOException("An attempt was made to move the position before the beginning of the stream");

        if (target > int.MaxValue)
            throw new NotSupportedException("Only valid Int32 positions are accepted");

        _position = (int)target;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("Can not change underlying buffer");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Write(buffer.AsSpan(offset, count));
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bfr = _buffer;
        var pos = _position;

        if (bfr.Length - pos < count)
            count = bfr.Length - pos;

        if (count <= 0)
            return 0;

        bfr.Span.Slice(pos, count).CopyTo(buffer.AsSpan(offset, count));

        _position += count;
        return count;
    }

    public override int Read(Span<byte> buffer)
    {
        var bfr = _buffer;
        var pos = _position;
        var count = Math.Min(bfr.Length - pos, buffer.Length);

        if (count <= 0)
            return 0;

        bfr.Span.Slice(pos, count).CopyTo(buffer);

        _position += count;
        return count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length == 0)
            return;

        var bfr = _buffer;
        var pos = _position;
        if (pos > bfr.Length - buffer.Length)
            throw new NotSupportedException($"Write of {buffer.Length} bytes at position {pos} exceeds fixed buffer capacity of {bfr.Length} bytes");

        buffer.CopyTo(bfr.Span.Slice(pos));

        _position += buffer.Length;
    }
}
