namespace CryBar.Classes;

public class ActualMemoryStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;
    public override long Position { get => _position; set => Seek(_position, SeekOrigin.Begin); }
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
                _position = (_buffer.Length - 1) - o;
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

        var dst = buffer.AsMemory(offset, count);
        bfr.Slice(pos, count).CopyTo(dst);

        _position += count;
        return count;
    }
}
