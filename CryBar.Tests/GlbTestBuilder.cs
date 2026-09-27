using System.Runtime.InteropServices;
using System.Text;

namespace CryBar.Tests;

internal sealed class GlbTestBuilder
{
    readonly MemoryStream bin = new();
    readonly List<string> views = [];
    readonly List<string> accessors = [];

    public int View(byte[] data, int stride = 0)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        int offset = (int)bin.Length;
        bin.Write(data);

        string strideJson = stride > 0 ? $",\"byteStride\":{stride}" : "";
        views.Add($"{{\"buffer\":0,\"byteOffset\":{offset},\"byteLength\":{data.Length}{strideJson}}}");
        return views.Count - 1;
    }

    public int Accessor(int view, int componentType, int count, string type, int byteOffset = 0)
    {
        accessors.Add($"{{\"bufferView\":{view},\"byteOffset\":{byteOffset},\"componentType\":{componentType},\"count\":{count},\"type\":\"{type}\"}}");
        return accessors.Count - 1;
    }

    public int Floats(string type, params float[] values)
    {
        int comps = type switch { "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, _ => 16 };
        return Accessor(View(MemoryMarshal.AsBytes(values.AsSpan()).ToArray()), 5126, values.Length / comps, type);
    }

    public int UInts(params uint[] values) =>
        Accessor(View(MemoryMarshal.AsBytes(values.AsSpan()).ToArray()), 5125, values.Length, "SCALAR");

    public int JointBytes(params byte[] values) =>
        Accessor(View(values), 5121, values.Length / 4, "VEC4");

    public byte[] Build(string body)
    {
        while (bin.Length % 4 != 0) bin.WriteByte(0);
        var binBytes = bin.ToArray();
        string json = "{\"asset\":{\"version\":\"2.0\"}," + body +
            ",\"accessors\":[" + string.Join(",", accessors) + "]" +
            ",\"bufferViews\":[" + string.Join(",", views) + "]" +
            ",\"buffers\":[{\"byteLength\":" + binBytes.Length + "}]}";
        return Assemble(json, binBytes);
    }

    public static byte[] Assemble(string json, byte[] bin)
    {
        var jsonBytes = Encoding.UTF8.GetBytes(json);
        int jsonPadded = (jsonBytes.Length + 3) & ~3;
        int binPadded = (bin.Length + 3) & ~3;

        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x46546C67u); w.Write(2u); w.Write((uint)(12 + 8 + jsonPadded + 8 + binPadded));

        w.Write((uint)jsonPadded); w.Write(0x4E4F534Au); w.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < jsonPadded; i++) w.Write((byte)' ');

        w.Write((uint)binPadded); w.Write(0x004E4942u); w.Write(bin);
        for (int i = bin.Length; i < binPadded; i++) w.Write((byte)0);

        return ms.ToArray();
    }
}
