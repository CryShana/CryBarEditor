using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tga;
using SixLabors.ImageSharp.PixelFormats;

using System.Text;

namespace CryBar;

/// <summary>
/// Centralizes common file conversion operations (XMB→XML, DDT→TGA)
/// that were previously duplicated across preview, export, and standalone tool code paths.
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
        var xml = BarFormatConverter.XMBtoXML(data);
        if (xml == null) return null;
        return BarFormatConverter.FormatXML(xml);
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
    /// Determines the converted file extension for a given source extension.
    /// Returns null if no conversion is applicable.
    /// </summary>
    public static string? GetConvertedExtension(string extension)
    {
        return extension.ToLower() switch
        {
            ".xmb" => null, // XMB extension is removed, revealing the underlying extension (e.g. .xml.xmb → .xml)
            ".ddt" => ".tga",
            _ => null
        };
    }

    /// <summary>
    /// Returns true if the file extension supports format conversion during export.
    /// </summary>
    public static bool IsConvertibleExtension(string extension)
    {
        return extension.ToLower() is ".xmb" or ".ddt";
    }
}
