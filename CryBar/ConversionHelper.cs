using CryBar.TMM;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

using System.Globalization;
using System.Text;

namespace CryBar;

/// <summary>
/// Common file conversion operations (XMB→XML, DDT→TGA, DDT→PNG, TMM→OBJ/GLB).
/// </summary>
public static class ConversionHelper
{
    /// <summary>
    /// Converts XMB binary data to formatted XML text.
    /// Data should already be decompressed before calling this.
    /// </summary>
    /// <returns>Formatted XML string, or null if parsing failed.</returns>
    public static string? ConvertXmbToXmlText(ReadOnlySpan<byte> data)
    {
        return BarFormatConverter.XMBtoFormattedXmlString(data);
    }

    /// <summary>
    /// Converts XMB data to UTF-8 XML bytes, ready for writing to a file.
    /// Data should already be decompressed before calling this.
    /// </summary>
    public static byte[]? ConvertXmbToXmlBytes(ReadOnlySpan<byte> data)
    {
        var text = ConvertXmbToXmlText(data);
        if (text == null) return null;
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>
    /// Converts DDT image data to PNG bytes.
    /// Data should already be decompressed before calling this.
    /// </summary>
    public static async Task<byte[]?> ConvertDdtToPngBytes(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        var ddt = new DDTImage(data);
        using var image = await BarFormatConverter.ParseDDT(ddt, token: token);
        if (image == null) return null;
        using var memory = new MemoryStream();
        await image.SaveAsPngAsync(memory, new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.BestSpeed
        }, token);
        return memory.ToArray();
    }

    /// <summary>
    /// Converts DDT image data to TGA bytes.
    /// Data should already be decompressed before calling this.
    /// </summary>
    /// <param name="data">Raw DDT file data (decompressed)</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>TGA bytes, or null if conversion failed.</returns>
    public static async Task<byte[]?> ConvertDdtToTgaBytes(ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        var ddt = new DDTImage(data);
        using var image = await BarFormatConverter.ParseDDT(ddt, token: token);
        if (image == null) return null;
        return await ImageToTgaBytes(image, token);
    }

    /// <summary>
    /// Saves an ImageSharp image to TGA byte array (32-bit).
    /// </summary>
    public static async Task<byte[]> ImageToTgaBytes(Image<Rgba32> image, CancellationToken token = default)
    {
        using var memory = new MemoryStream();
        await image.SaveAsTgaAsync(memory, new TgaEncoder
        {
            BitsPerPixel = TgaBitsPerPixel.Pixel32
        }, token);
        return memory.ToArray();
    }

    /// <summary>
    /// Converts a TMM+TMM.DATA pair to Wavefront OBJ format.
    /// Positions, UVs, and normals (decoded from TBN) are included.
    /// Faces are grouped by mesh group/material.
    /// </summary>
    /// <param name="tmmData">Raw .tmm file bytes (decompressed).</param>
    /// <param name="tmmDataData">Raw .tmm.data file bytes (decompressed).</param>
    /// <returns>OBJ text as bytes, or null if parsing failed.</returns>
    static bool TryParseTmmPair(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData,
        out TmmFile tmm, out TmmDataFile dataFile)
    {
        tmm = new TmmFile(tmmData);
        dataFile = default!;
        if (!tmm.Parsed) 
            return false;

        dataFile = new TmmDataFile(tmmDataData, tmm.NumVertices, tmm.NumTriangleVerts, tmm.NumBones > 0);
        return dataFile.Parsed && dataFile.Vertices != null && dataFile.Indices != null;
    }

    public static byte[]? ConvertTmmToObjBytes(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData, string? mtlFileName = null)
    {
        if (!TryParseTmmPair(tmmData, tmmDataData, out var tmm, out var dataFile)) return null;
        var vertices = dataFile.Vertices!;
        var indices = dataFile.Indices!;
        var meshGroups = tmm.MeshGroups!;
        var materials = tmm.Materials!;

        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(vertices.Length * 80); // rough pre-allocation
        sb.AppendLine("# Exported from CryBarEditor");
        sb.AppendLine($"# Vertices: {tmm.NumVertices}, Triangles: {tmm.NumTriangleVerts / 3}");
        if (mtlFileName != null)
            sb.AppendLine($"mtllib {mtlFileName}");
        sb.AppendLine();

        // Write positions, UVs, and normals in separate OBJ sections (single data pass)
        var uvSection = new StringBuilder(vertices.Length * 30);
        var normalSection = new StringBuilder(vertices.Length * 40);

        foreach (var v in vertices)
        {
            float px = (float)v.PosX, py = (float)v.PosY, pz = (float)v.PosZ;
            sb.AppendLine($"v {px.ToString(ic)} {py.ToString(ic)} {pz.ToString(ic)}");

            float u = (float)v.U, vFlipped = 1.0f - (float)v.V;
            uvSection.AppendLine($"vt {u.ToString(ic)} {vFlipped.ToString(ic)}");

            var (nx, ny, nz) = TbnDecoder.DecodeNormal(v.TbnX, v.TbnY, v.TbnZ);
            normalSection.AppendLine($"vn {nx.ToString(ic)} {ny.ToString(ic)} {nz.ToString(ic)}");
        }

        sb.AppendLine();
        sb.Append(uvSection);
        sb.AppendLine();
        sb.Append(normalSection);
        sb.AppendLine();

        // Write faces grouped by mesh group
        int globalVertexOffset = 0;
        for (int g = 0; g < meshGroups.Length; g++)
        {
            var mg = meshGroups[g];
            var matName = mg.MaterialIndex < materials.Length
                ? materials[mg.MaterialIndex] : $"material_{mg.MaterialIndex}";

            sb.AppendLine($"g mesh_group_{g}");
            sb.AppendLine($"usemtl {matName}");

            var triCount = mg.IndexCount / 3;
            for (uint t = 0; t < triCount; t++)
            {
                var baseIdx = mg.IndexStart + t * 3;
                if (baseIdx + 2 >= indices.Length) break;

                // OBJ indices are 1-based; add global vertex offset for this mesh group
                var a = indices[baseIdx] + globalVertexOffset + 1;
                var b = indices[baseIdx + 1] + globalVertexOffset + 1;
                var c = indices[baseIdx + 2] + globalVertexOffset + 1;

                sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            sb.AppendLine();

            globalVertexOffset += (int)mg.VertexCount;
        }
        

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Converts TMM+TMM.DATA pair to GLB (glTF binary) format. Geometry only.
    /// </summary>
    public static byte[]? ConvertTmmToGlbBytes(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData)
    {
        if (!TryParseTmmPair(tmmData, tmmDataData, out var tmm, out var dataFile)) return null;
        return GlbExporter.ExportGlb(tmm, dataFile);
    }

    /// <summary>
    /// Converts TMM+TMM.DATA pair to GLB (glTF binary) format with materials.
    /// </summary>
    public static byte[]? ConvertTmmToGlbBytes(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData,
        IReadOnlyList<GlbExporter.GlbMaterial>? materials)
    {
        if (!TryParseTmmPair(tmmData, tmmDataData, out var tmm, out var dataFile)) return null;
        return GlbExporter.ExportGlb(tmm, dataFile, materials);
    }

    /// <summary>
    /// Converts decompressed file data to text, handling XMB-to-XML conversion when needed.
    /// </summary>
    public static string GetTextContent(ReadOnlySpan<byte> data, string filePath)
    {
        if (Path.GetExtension(filePath).Equals(".xmb", StringComparison.OrdinalIgnoreCase))
            return ConvertXmbToXmlText(data) ?? Encoding.UTF8.GetString(data);
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>
    /// Determines the converted file extension for a given source extension.
    /// Returns null if no conversion is applicable.
    /// </summary>
    public static string? GetConvertedExtension(string extension, bool tmmToGltf = false)
    {
        return extension.ToLower() switch
        {
            ".xmb" => null, // XMB extension is removed, revealing the underlying extension (e.g. .xml.xmb → .xml)
            ".ddt" => ".tga",
            ".tmm" => tmmToGltf ? ".glb" : ".obj",
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the file extension supports format conversion during export.
    /// </summary>
    public static bool IsConvertibleExtension(string extension)
    {
        return extension.ToLower() is ".xmb" or ".ddt" or ".tmm";
    }
}
