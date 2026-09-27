using CryBar.BCnEncoder.Decoder;
using CryBar.BCnEncoder.Encoder;
using CryBar.BCnEncoder.Shared;
using CryBar.BCnEncoder.Shared.ImageFiles;
using CommunityToolkit.HighPerformance;
using CryBar.Export;
using CryBar.TMM;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace CryBar.Bar;

/// <summary>
/// Common file conversion operations (XMB->XML, DDT->TGA, DDT->PNG, TMM->OBJ/GLB).
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

    public readonly record struct DdsSummary(int Width, int Height, int Mips, string FormatName);

    /// <summary>
    /// Reads dimensions, mip count, and DXGI format name from a DDS file header.
    /// Returns null when the header is unparseable.
    /// </summary>
    public static DdsSummary? GetDdsSummary(ReadOnlyMemory<byte> data)
    {
        DdsFile dds;
        try
        {
            using var ms = new MemoryStream(data.ToArray(), writable: false);
            dds = DdsFile.Load(ms);
        }
        catch { return null; }

        var dxgi = dds.header.ddsPixelFormat.IsDxt10Format
            ? dds.dx10Header.dxgiFormat
            : dds.header.ddsPixelFormat.DxgiFormat;

        int mips = dds.Faces.Count > 0 ? dds.Faces[0].MipMaps.Length : 1;
        return new DdsSummary((int)dds.header.dwWidth, (int)dds.header.dwHeight, mips, dxgi.ToString());
    }

    /// <summary>
    /// Decodes a DDS file to an ImageSharp Rgba32 image.
    /// Returns null for unparseable input, cubemap/volume DDS, or HDR (BC6H) variants.
    /// </summary>
    public static async Task<Image<Rgba32>?> DecodeDdsToImage(
        ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        DdsFile dds;
        try
        {
            using var ms = new MemoryStream(data.ToArray(), writable: false);
            dds = DdsFile.Load(ms);
        }
        catch { return null; }

        if (dds.Faces.Count != 1) return null;

        var dxgi = dds.header.ddsPixelFormat.IsDxt10Format
            ? dds.dx10Header.dxgiFormat
            : dds.header.ddsPixelFormat.DxgiFormat;
        if (dxgi == DxgiFormat.DxgiFormatBc6HUf16 || dxgi == DxgiFormat.DxgiFormatBc6HSf16)
            return null;

        int w = (int)dds.header.dwWidth;
        int h = (int)dds.header.dwHeight;

        ColorRgba32[] pixels;
        try { pixels = await new BcDecoder().DecodeAsync(dds, token); }
        catch { return null; }

        var pixelBytes = MemoryMarshal.AsBytes(pixels.AsSpan());
        return Image.LoadPixelData<Rgba32>(pixelBytes, w, h);
    }

    /// <summary>
    /// Encodes an Rgba32 image to DDS bytes using BCn compression.
    /// sRGB is expressed via the DX10 header DXGI format; legacy FourCC fallback is non-sRGB.
    /// </summary>
    public static async Task<byte[]> EncodeImageToDdsBytes(
        Image<Rgba32> image,
        CompressionFormat format,
        bool sRgb,
        byte mipmapLevels,
        CancellationToken token = default)
    {
        int w = image.Width;
        int h = image.Height;

        var pixels = new ColorRgba32[w * h];
        image.CopyPixelDataTo(MemoryMarshal.AsBytes(pixels.AsSpan()));
        var mem2D = pixels.AsMemory().AsMemory2D(h, w);

        var encoder = new BcEncoder(format);
        encoder.OutputOptions.FileFormat = OutputFileFormat.Dds;
        encoder.OutputOptions.GenerateMipMaps = mipmapLevels != 1;
        encoder.OutputOptions.MaxMipMapLevel = mipmapLevels <= 0 ? -1 : mipmapLevels;
        // sRGB DXGI variants are only addressable via the DX10 header path.
        encoder.OutputOptions.DdsPreferDxt10Header = sRgb;

        var dds = await encoder.EncodeToDdsAsync(mem2D, token);

        if (sRgb)
        {
            dds.dx10Header.dxgiFormat = format switch
            {
                CompressionFormat.Bc1 or CompressionFormat.Bc1WithAlpha => DxgiFormat.DxgiFormatBc1UnormSrgb,
                CompressionFormat.Bc2 => DxgiFormat.DxgiFormatBc2UnormSrgb,
                CompressionFormat.Bc3 => DxgiFormat.DxgiFormatBc3UnormSrgb,
                CompressionFormat.Bc7 => DxgiFormat.DxgiFormatBc7UnormSrgb,
                _ => dds.dx10Header.dxgiFormat
            };
        }

        using var ms = new MemoryStream();
        dds.Write(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Converts DDS image data to PNG bytes.
    /// Returns null when the DDS is unparseable or an unsupported variant.
    /// </summary>
    public static async Task<byte[]?> ConvertDdsToPngBytes(
        ReadOnlyMemory<byte> data, CancellationToken token = default)
    {
        using var image = await DecodeDdsToImage(data, token);
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

        dataFile = new TmmDataFile(tmmDataData, tmm);
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
        for (int g = 0; g < meshGroups.Length; g++)
        {
            var mg = meshGroups[g];
            var matName = mg.MaterialIndex < materials.Length
                ? materials[mg.MaterialIndex] : $"material_{mg.MaterialIndex}";

            sb.AppendLine($"g mesh_group_{g}");
            sb.AppendLine($"usemtl {matName}");

            // OBJ indices are 1-based
            long vertexBase = (long)mg.VertexStart + 1;
            var triCount = mg.IndexCount / 3;
            for (uint t = 0; t < triCount; t++)
            {
                var baseIdx = mg.IndexStart + t * 3;
                if (baseIdx + 2 >= indices.Length) break;

                var a = indices[baseIdx] + vertexBase;
                var b = indices[baseIdx + 1] + vertexBase;
                var c = indices[baseIdx + 2] + vertexBase;

                sb.AppendLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
            sb.AppendLine();
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Converts TMM+TMM.DATA pair to GLB (glTF binary) format with optional materials and animations.
    /// Extras metadata (TMM bone collisions, attachments, autoburn, etc., plus TMA/DDT info when supplied)
    /// is always embedded so re-importers can recover non-mesh fields.
    /// </summary>
    public static byte[]? ConvertTmmToGlbBytes(ReadOnlyMemory<byte> tmmData, ReadOnlyMemory<byte> tmmDataData,
        IReadOnlyList<GlbExporter.GlbMaterial>? materials = null,
        IReadOnlyList<GlbExporter.GlbAnimation>? animations = null,
        IReadOnlyList<(string Name, TmaFile Tma)>? sourceTmas = null,
        IReadOnlyList<(string Material, DDTImage Ddt)>? sourceDdts = null)
    {
        if (!TryParseTmmPair(tmmData, tmmDataData, out var tmm, out var dataFile)) return null;

        // Always surface TMM material names so re-import preserves per-primitive material indices
        // even when no DDT textures are provided. Caller-supplied materials win when present.
        if (materials == null && tmm.Materials is { Length: > 0 })
        {
            materials = tmm.Materials
                .Select(name => new GlbExporter.GlbMaterial { Name = name })
                .ToArray();
        }
        else if (materials != null && tmm.Materials is { Length: > 0 })
        {
            // .material XML can list submaterials in any order; mesh groups index materials
            // by TMM order, so we must align the caller's list to tmm.Materials by name.
            // Without this, a mesh group with MaterialIndex=0 would receive whichever submaterial
            // happened to be first in the XML.
            var byName = new Dictionary<string, GlbExporter.GlbMaterial>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in materials) byName[m.Name] = m;

            var aligned = new GlbExporter.GlbMaterial[tmm.Materials.Length];
            for (int i = 0; i < tmm.Materials.Length; i++)
            {
                aligned[i] = byName.TryGetValue(tmm.Materials[i], out var m)
                    ? m
                    : new GlbExporter.GlbMaterial { Name = tmm.Materials[i] };
            }
            materials = aligned;
        }

        var extras = GlbExtras.From(tmm,
            sourceTmas ?? Array.Empty<(string, TmaFile)>(),
            sourceDdts ?? Array.Empty<(string, DDTImage)>());

        return GlbExporter.ExportGlb(tmm, dataFile, materials, animations, extras);
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
        if (extension.Equals(".xmb", StringComparison.OrdinalIgnoreCase)) return null;
        if (extension.Equals(".ddt", StringComparison.OrdinalIgnoreCase)) return ".tga";
        if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) return ".png";
        if (extension.Equals(".tmm", StringComparison.OrdinalIgnoreCase)) return tmmToGltf ? ".glb" : ".obj";
        return null;
    }

    /// <summary>
    /// Returns true if the file extension supports format conversion during export.
    /// </summary>
    public static bool IsConvertibleExtension(string extension)
    {
        return extension.Equals(".xmb", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ddt", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".dds", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tmm", StringComparison.OrdinalIgnoreCase);
    }
}
