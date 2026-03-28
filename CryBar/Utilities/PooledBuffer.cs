using System.Buffers;

namespace CryBar.Utilities;

/// <summary>
/// Buffer that utilizes ArrayPool to minimize allocations.
/// Returns rented buffer when disposed automatically
/// </summary>
public class PooledBuffer : IDisposable
{
	private readonly int _offset;
	private readonly int _size;
	private byte[]? _buffer;
	private bool _moved;

	public Span<byte> Span
	{
		get
		{
			var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledBuffer));
			return buffer.AsSpan(_offset, _size);
		}
	}

	public Memory<byte> Memory
	{
		get
		{
			var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledBuffer));
			return buffer.AsMemory(_offset, _size);
		}
	}

	public int Length => _size;

	public PooledBuffer(int size)
	{
		_size = size;
		_buffer = ArrayPool<byte>.Shared.Rent(size);
	}

	PooledBuffer(PooledBuffer existing, int? offset = null, int? length = null)
	{
		if (existing._moved)
			throw new InvalidOperationException("Buffer already moved");

		if (offset.HasValue && length.HasValue)
		{
			_offset = offset.Value;
			_size = length.Value;
		}
		else
		{
			_offset = 0;
			_size = existing._size;
		}

		_buffer = existing._buffer;
		Interlocked.Exchange(ref existing._moved, true);
	}

	public static async ValueTask<PooledBuffer> FromFile(string path, CancellationToken token = default)
	{
		using var stream = File.OpenRead(path);
		if (stream.Length >= int.MaxValue)
			throw new InvalidOperationException("File too large for pooled buffer");

		var buffer = new PooledBuffer((int)stream.Length);
		try
		{
			await stream.ReadExactlyAsync(buffer.Memory, token);
			return buffer;
		}
		catch
		{
			buffer.Dispose();
			throw;
		}
	}

	/// <summary>
	/// Moves existing PooledBuffer to new wrapper. Existing one no longer owns the buffer, will not return to ArrayPool on dispose.
	/// The new PooledBuffer is now the owner of buffer. Skips renting step, just takes it from existing buffer.
	/// </summary>
	/// <param name="existing">Existing PooledBuffer to take buffer ownership from</param>
	/// <returns>New PooledBuffer with same buffer as existing one</returns>
	public static PooledBuffer MoveFrom(PooledBuffer existing) => new PooledBuffer(existing);

	/// <summary>
	/// Slices this buffer and returns new one (Ownership is transferred to returned buffer!)
	/// </summary>
	/// <param name="start_index">Start index</param>
	/// <param name="length">Length</param>
	public PooledBuffer Slice(int start_index, int length)
	{
		if (length <= 0 || start_index + length > _size)
			throw new IndexOutOfRangeException();

		return new PooledBuffer(this, _offset + start_index, length);
	}
	
	public void Dispose()
	{
		var buffer = Interlocked.Exchange(ref _buffer, null);
		if (buffer == null)
			return;

		// return if not moved
		if (!_moved)
		{
			ArrayPool<byte>.Shared.Return(buffer);
		}
	}
}
