using System.Buffers;

namespace CryBar.Classes;

/// <summary>
/// Buffer that utilizes ArrayPool to minimize allocations.
/// Returns rented buffer when disposed automatically
/// </summary>
public class PooledBuffer : IDisposable
{
	private readonly int _size;
	private byte[]? _buffer;

	public Span<byte> Span
	{
		get
		{
			var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledBuffer));
			return buffer.AsSpan(0, _size);
		}
	}

	public Memory<byte> Memory
	{
		get
		{
			var buffer = _buffer ?? throw new ObjectDisposedException(nameof(PooledBuffer));
			return buffer.AsMemory(0, _size);
		}
	}

	public int Length => _size;

	public PooledBuffer(int size)
	{
		_size = size;
		_buffer = ArrayPool<byte>.Shared.Rent(size);
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

	public void Dispose()
	{
		var buffer = _buffer;
		if (buffer == null)
			return;

		_buffer = null;
		ArrayPool<byte>.Shared.Return(buffer);
	}
}
