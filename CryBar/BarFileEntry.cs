using System.Xml;
using System.Text;
using System.Buffers.Binary;
using CommunityToolkit.HighPerformance.Buffers;

namespace CryBar;

public class BarFileEntry
{
    #region Data in Archive
    public long ContentOffset { get; set; }
    public int SizeUncompressed { get; set; }
    public int SizeCompressed { get; set; }
    public int SizeInArchive { get; set; }
    public string RelativePath { get; set; }
    public bool IsCompressed { get; set; } 
    #endregion

    public string Name { get; set; }
    public string DirectoryPath { get; set; }

    public BarFileEntry(string relative_path)
    {
        Name = Path.GetFileName(relative_path);
        RelativePath = relative_path;
        DirectoryPath = Path.GetDirectoryName(relative_path) ?? "";
        if (DirectoryPath.Length > 0) DirectoryPath += "\\";
    }

    /// <summary>
    /// Is file a XMB file? This needs to be converted to be viewed.
    /// </summary>
    public bool IsXMB => RelativePath.EndsWith(".XMB", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// Is file a special DDT image file? This has to be converted to be viewed.
    /// </summary>
    public bool IsDDT => RelativePath.EndsWith(".DDT", StringComparison.OrdinalIgnoreCase);
    public bool IsCache => RelativePath.EndsWith(".CACHE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Is file a human readable text file? (There could be other extensions too)
    /// </summary>
    public bool IsText => RelativePath.EndsWith(".TXT", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".XS", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".XAML", StringComparison.OrdinalIgnoreCase) ||
        RelativePath.EndsWith(".XML", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Copies file data (not header) from [from] BAR stream to [to] stream.
    /// This will seek to content offset on [from] stream
    /// but won't seek at all for [to] stream, so make sure
    /// you set the position correctly for the destination
    /// </summary>
    /// <param name="from">Input stream containing BAR file content</param>
    /// <param name="to">Output stream to which data will be copied (only data)</param>
    public void CopyData(Stream from, Stream to)
    {
        from.Seek(ContentOffset, SeekOrigin.Begin);

        // copy [SizeInArchive] amount of bytes to [to] stream
        using var buffer = SpanOwner<byte>.Allocate(81920);
        var span = buffer.Span;
        var size = SizeInArchive;

        var copied_bytes = 0;
        do
        {
            var r = from.Read(span);
            if (r <= 0)
            {
                throw new Exception("Failed to read more data while copying");
            }

            to.Write(span.Slice(0, r));
            copied_bytes += r;
        } while (copied_bytes < size);
    }

    /// <summary>
    /// Reads file content from BAR stream and allocates new array for the data.
    /// <br />
    /// This data is raw and may be compressed
    /// </summary>
    public byte[] ReadDataRaw(Stream stream)
    {
        var buffer = new byte[SizeInArchive];
        ReadDataRaw(stream, buffer);
        return buffer;
    }

    /// <summary>
    /// Reads file content from BAR stream and outputs it to given Span.
    /// Make sure the span is large enough to accomodate [SizeInArchive] bytes.
    /// <br />
    /// This data is raw and may be compressed
    /// </summary>
    public void ReadDataRaw(Stream stream, Span<byte> read_data)
    {
        stream.Seek(ContentOffset, SeekOrigin.Begin);
        stream.ReadExactly(read_data.Slice(0, SizeInArchive));
    }

    public Memory<byte> ReadDataDecompressed(Stream stream)
    {
        var buffer = new byte[Math.Max(SizeInArchive, SizeUncompressed)];
        var r = ReadDataDecompressed(stream, buffer);
        if (r == -1) return Memory<byte>.Empty;

        return buffer.AsMemory(0, r);
    }

    /// <summary>
    /// Reads file content from BAR stream and outputs it to given Span.
    /// Make sure the span is large enough to accomodate AT LEAST [SizeUncompressed] bytes.
    /// <br />
    /// This data will be decompressed before returning.
    /// </summary>
    /// <returns>Numer of bytes read. Returns -1 if data failed to be decompressed.</returns>
    public int ReadDataDecompressed(Stream stream, Span<byte> read_data)
    {
        stream.Seek(ContentOffset, SeekOrigin.Begin);
        if (!IsCompressed)
        {
            // read directly into output
            stream.ReadExactly(read_data);
            return SizeInArchive;
        }

        using var raw_data = SpanOwner<byte>.Allocate(SizeInArchive);
        var raw = raw_data.Span;
        stream.ReadExactly(raw);

        if (raw.IsAlz4())
        {
            return BarCompression.DecompressAlz4(raw, read_data);
        }
        else if (raw.IsL33t() || raw.IsL66t())
        {
            return BarCompression.DecompressL33tL66t(raw, read_data);      
        }

        return -1;
    }

    public override string ToString() => $"{RelativePath ?? "Unset path"} ({SizeInArchive} bytes)";
}