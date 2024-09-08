using System.Text;
using System.Diagnostics;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

using CommunityToolkit.HighPerformance.Buffers;

namespace CryBar;

public class BarFile
{
    /// <summary>
    /// True if BAR contents have been loaded, if not, try calling Load() method
    /// </summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// Loaded BAR version
    /// </summary>
    public uint Version { get; private set; }
    public string? RootPath { get; private set; }

    /// <summary>
    /// Loaded BAR entries
    /// </summary>
    public IReadOnlyList<BarFileEntry>? Entries => _entries;

    #region Constants
    /// <summary>
    /// Max text length in bytes that is considered valid
    /// </summary>
    internal const int MAX_TEXT_LENGTH = 4096;

    /// <summary>
    /// Max entries in a collection (NOTE: most collections I've seen have 1000-4000 entries at most)
    /// </summary>
    internal const int MAX_ENTRY_COUNT = 1_000_000;
    internal const int MAX_BUFFER_SIZE = 500_000_000; // 500 MB
    internal const int HEADER_SIZE =
        4 +     // signature ESPN
        4 +     // version                      [uint32]
        4 +     // identifier (=1144201745)     [uint32] 
        264 +   // padding for future versions 
        4 +     // checksum                     [uint32]
        4 +     // number of files              [uint32]
        4 +     // identifier (?)               [uint32]
        8;      // file table offset            [int64]
    #endregion

    Stream _stream;
    List<BarFileEntry>? _entries = null;

    public BarFile(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Loads BAR content from stream
    /// </summary>
    /// <returns>True if successful</returns>
    [MemberNotNullWhen(true, nameof(_entries), nameof(Entries), nameof(RootPath))]
    public bool Load()
    {
        // TODO: replace exceptions with Result return type for better performance in case of errors
        if (Loaded)
        {
            throw new InvalidOperationException("Bar file already loaded");
        }

        var str = _stream;
        if (!str.CanSeek)
        {
            throw new NotSupportedException("Stream must support seeking");
        }

        var file_length = str.Length;
        if (file_length <= HEADER_SIZE)
        {
            // stream is too short
            throw new InvalidDataException("BAR file too small");
        }

        using var buffer = SpanOwner<byte>.Allocate(HEADER_SIZE);
        var span = buffer.Span;
        str.ReadExactly(span);

        // must start with ESPN
        if (span is not [0x45, 0x53, 0x50, 0x4E, ..])
        {
            // header not valid
            throw new InvalidDataException("Invalid BAR format header");
        }

        int offset = 4;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (version != 6)
        {
            // unsupported version
            throw new InvalidDataException("BAR version " + version + " is not supported");
        }

        uint id1 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (id1 != 1144201745)
        {
            // unsupported id1
            throw new InvalidDataException("Invalid BAR format");
        }

        // ignore the empty padding
        offset += 264;

        uint checksum = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        int file_count = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (file_count < 0 || file_count > MAX_ENTRY_COUNT)
        {
            // invalid file count
            throw new InvalidDataException("Invalid BAR file count");
        }

        uint id2 = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
        offset += 4;

        if (id2 != 0)
        {
            // unsupported id2
            throw new InvalidDataException("Invalid BAR format");
        }

        long file_table_offset = BinaryPrimitives.ReadInt64LittleEndian(span.Slice(264 + 24, 8));
        offset += 8;

        if (file_table_offset < HEADER_SIZE || file_table_offset >= file_length)
        {
            // file table offset out of bounds
            throw new InvalidDataException("Invalid BAR format, table offset out of bounds");
        }

        // PROCESS ENTRIES
        str.Seek(file_table_offset, SeekOrigin.Begin);

        var temp_span = span.Slice(0, 4);
        str.ReadExactly(temp_span);

        // all text is Unicode encoded, hene length must be multiplied by 2
        int root_name_length = BinaryPrimitives.ReadInt32LittleEndian(temp_span) * 2;
        if (root_name_length <= 0 || root_name_length > MAX_TEXT_LENGTH)
        {
            // invalid root name length
            throw new InvalidDataException("Invalid BAR root name");
        }

        temp_span = span.Slice(0, root_name_length + 4);
        str.ReadExactly(temp_span);

        var root_path = Encoding.Unicode.GetString(temp_span.Slice(0, root_name_length));
        int root_files_count = BinaryPrimitives.ReadInt32LittleEndian(temp_span.Slice(root_name_length));

        if (root_files_count != file_count)
        {
            // I think this should not happen, need to check
            throw new Exception("Root file count did not match global file count - should this happen?");
        }

        if (root_files_count < 0 || root_files_count > MAX_ENTRY_COUNT)
        {
            // invalid root file count
            throw new InvalidDataException("BAR file count mismatch");
        }

        var entries = new List<BarFileEntry>(root_files_count);
        for (int i = 0; i < file_count; i++)
        {
            temp_span = span.Slice(0,
                8 +     // file content offset  [int64]
                4 +     // size uncompressed    [int32]
                4 +     // size in archive      [int32]
                4 +     // size in archive?     [int32]
                4);     // file name length     [int32]

            str.ReadExactly(temp_span);

            // file offset
            long file_offset = BinaryPrimitives.ReadInt64LittleEndian(temp_span.Slice(0, 8));
            int file_size_uncompressed = BinaryPrimitives.ReadInt32LittleEndian(temp_span.Slice(8, 4));            
            int file_size_compressed = BinaryPrimitives.ReadInt32LittleEndian(temp_span.Slice(12, 4));            
            int file_size_archive = BinaryPrimitives.ReadInt32LittleEndian(temp_span.Slice(16, 4)); // this can be different from compressed size, often they are same
            int file_path_length = BinaryPrimitives.ReadInt32LittleEndian(temp_span.Slice(20, 4)) * 2;
            if (file_path_length > MAX_TEXT_LENGTH || file_path_length <= 0)
            {
                throw new InvalidDataException("Invalid file name length specified: " + file_path_length);
            }

            temp_span = span.Slice(0,
                file_path_length +  // file name
                4);                 // is compressed flag [uint32]

            str.ReadExactly(temp_span);
            var file_path = Encoding.Unicode.GetString(temp_span.Slice(0, file_path_length));
            var file_compressed = BinaryPrimitives.ReadUInt32LittleEndian(temp_span.Slice(file_path_length)) == 1;

            entries.Add(new BarFileEntry(file_path)
            {
                ContentOffset = file_offset,
                SizeUncompressed = file_size_uncompressed,
                SizeCompressed = file_size_compressed,
                SizeInArchive = file_size_archive,
                IsCompressed = file_compressed
            });
        }

        _entries = entries;
        Version = version;
        RootPath = root_path;
        Loaded = true;

#pragma warning disable CS8775 // Member must have a non-null value when exiting in some condition.
        return true;
#pragma warning restore CS8775 // Member must have a non-null value when exiting in some condition.
    }
}
