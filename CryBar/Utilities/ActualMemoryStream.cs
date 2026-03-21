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

        var o = (int)offset;

        switch (origin)
        {
            case SeekOrigin.Begin:
                _position = o;
                break;
            case SeekOrigin.Current:
                _position += o;
                break;
            case SeekOrigin.End:
                _position = _buffer.Length - o;
                break;
        }

        return _position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException("Can not change underlying buffer");
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        var bfr = _buffer;
        var pos = _position;

        if (bfr.Length - pos < count)
            count = bfr.Length - pos;

        if (count <= 0)
            return;

        buffer.AsMemory(offset, count).CopyTo(bfr.Slice(pos));

        _position += count;
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
        var bfr = _buffer;
        var pos = _position;
        var count = Math.Min(bfr.Length - pos, buffer.Length);

        if (count <= 0)
            return;

        buffer.Slice(0, count).CopyTo(bfr.Span.Slice(pos));

        _position += count;
    }
}
