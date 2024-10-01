using System.Text;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace CryBar;

public class TmmModel
{
    public bool HeaderParsed { get; private set; }
    public string[]? ModelNames { get; private set; }
    public int DataOffset { get; private set; } = -1;


    readonly ReadOnlyMemory<byte> _data;

    public TmmModel(ReadOnlyMemory<byte> data)
    {
        _data = data;
    }

    [MemberNotNullWhen(true, nameof(ModelNames))]
    public bool ParseHeader()
    {
        var data = _data.Span;
        if (data.Length < 16)
            return false;

        // signature must match "BTMM"
        if (data is not [0x42, 0x54, 0x4d, 0x4d, ..])
            return false;

        var offset = 4;

        // this seems to always be 34 (0x22 0x00 0x00 x00)
        var u1 = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        if (u1 != 34) return false;

        // this seems to always be "DP"
        if (data.Slice(offset, 2) is not [0x44, 0x50])
            return false;

        offset += 2;

        var data_offset = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        var model_count = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4)); offset += 4;
        if (model_count > 1000)
            return false;

        var model_names = new string[model_count];
        for (int i = 0; i < model_count; i++)
        {
            var name_length = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4)) * 2; offset += 4;
            if (name_length > 5000)
                return false;

            var name = Encoding.Unicode.GetString(data.Slice(offset, name_length)); offset += name_length;
            model_names[i] = name;

            // every model name ends with this
            if (data.Slice(offset, 2) is not [0xe8, 0x07])
                return false;

            offset += 4 * 4; // TODO: find out what this data is, it's after every model name
        }

        DataOffset = data_offset;
        ModelNames = model_names;
        HeaderParsed = true;
        return true;
    }

    public void ParseModel(int index)
    {
        if (!HeaderParsed && !ParseHeader())
            throw new Exception("Header not parsed");

        if (index < 0 || index >= ModelNames!.Length)
            throw new Exception("Model index outside bounds");

        var model_name = ModelNames[index];
        var offset = DataOffset;
        var data = _data.Span;

        // TODO: parse model data here

        // CHECK: the offset points to the middle of last model name data?
    }
}
