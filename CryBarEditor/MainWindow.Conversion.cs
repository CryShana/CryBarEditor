using Avalonia.Controls;
using CryBar;
using CryBar.Classes;
using CryBarEditor.Classes;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Xml;

namespace CryBarEditor;

public partial class MainWindow
{
    #region Conversion menu events
    async void MenuItem_ConvertXMLtoXMB(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert XML to XMB");
        if (file == null) return;

        // when converting to XMB, the original extension is retained!
        // so "something.xml" becomes "something.xml.XMB"

        var out_file = PickOutFile(file, suffix: Path.GetExtension(file), new_extension: ".XMB", overwrite: true);

        try
        {
            var xml_text = File.ReadAllText(file);
            var xml = new XmlDocument();
            xml.LoadXml(xml_text);

            var data = BarFormatConverter.XMLtoXMB(xml, CompressionType.Alz4);
            using (var f = File.Create(out_file)) f.Write(data.Span);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert to XMB:\n" + ex.Message);
        }
    }

    async void MenuItem_ConvertXMBtoXML(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert XMB to XML");
        if (file == null) return;

        var ext = Path.GetExtension(file).ToLower();

        // when converting XMB to XML, the .XMB extension is removed! But this only happens if it was there in the first place!
        var out_file = PickOutFile(file, suffix: (ext == ".xmb" ? "" : ext), new_extension: (ext == ".xmb" ? "" : ".xml"), overwrite: true);
        try
        {
            using var xmb_data = await PooledBuffer.FromFile(file);
            using var decompressed = BarCompression.EnsureDecompressedPooled(xmb_data, out _);
            var xmlText = ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
            if (xmlText == null) throw new Exception("Failed to parse XMB file");

            File.WriteAllText(out_file, xmlText);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert to XML:\n" + ex.Message);
        }
    }

    async void MenuItem_ConvertDDTtoTGA(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ConvertDdtToFormat(sender, "TGA", ".tga", d => ConversionHelper.ConvertDdtToTgaBytes(d));

    async void MenuItem_ConvertDDTtoPNG(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ConvertDdtToFormat(sender, "PNG", ".png", d => ConversionHelper.ConvertDdtToPngBytes(d));

    async Task ConvertDdtToFormat(object? sender, string formatName, string extension,
        Func<ReadOnlyMemory<byte>, Task<byte[]?>> converter)
    {
        var file = await PickFile(sender, $"Convert DDT to {formatName}", [new("DDT Image") { Patterns = ["*.ddt"] }]);
        if (file == null) return;

        var out_file = PickOutFile(file, new_extension: extension, overwrite: true);
        try
        {
            using var data = await PooledBuffer.FromFile(file);
            using var ddt_data = BarCompression.EnsureDecompressedPooled(data, out _);
            var convertedBytes = await converter(ddt_data.Memory);
            if (convertedBytes == null) throw new InvalidDataException("Failed to convert DDT file");

            File.WriteAllBytes(out_file, convertedBytes);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to convert to {formatName}:\n" + ex.Message);
        }
    }

    /// <summary>
    /// Resolves the .tmm.data companion path for a given .tmm file.
    /// If the default path (tmmFile + ".data") doesn't exist, prompts the user to pick it.
    /// Returns null if the user cancels.
    /// </summary>
    async Task<string?> PickTmmDataFile(object? sender, string tmmFile)
    {
        var dataPath = tmmFile + ".data";
        if (File.Exists(dataPath)) return dataPath;
        return await PickFile(sender, "Select companion .tmm.data file",
            [new("TMM Data") { Patterns = ["*.data"] }]);
    }

    async void MenuItem_ConvertTMMtoOBJ(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ConvertTmmToFormat(sender, "OBJ", ".obj", (a, b) => ConversionHelper.ConvertTmmToObjBytes(a, b));

    async void MenuItem_ConvertTMMtoGLTF(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await ConvertTmmToFormat(sender, "glTF", ".glb", (a, b) => ConversionHelper.ConvertTmmToGlbBytes(a, b));

    async Task ConvertTmmToFormat(object? sender, string formatName, string extension,
        Func<ReadOnlyMemory<byte>, ReadOnlyMemory<byte>, byte[]?> converter, string? errorNote = null)
    {
        var file = await PickFile(sender, $"Convert TMM to {formatName}", [new("TMM Model") { Patterns = ["*.tmm"] }]);
        if (file == null) return;

        var dataFilePath = await PickTmmDataFile(sender, file);
        if (dataFilePath == null) return;

        var out_file = PickOutFile(file, new_extension: extension, overwrite: true);
        try
        {
            using var tmmData = await PooledBuffer.FromFile(file);
            using var tmmBytes = BarCompression.EnsureDecompressedPooled(tmmData, out _);
            
            using var tmmDatadata = await PooledBuffer.FromFile(dataFilePath);
            using var tmmDataBytes = BarCompression.EnsureDecompressedPooled(tmmDatadata, out _);

            var convertedBytes = converter(tmmBytes.Memory, tmmDataBytes.Memory);
            if (convertedBytes == null)
                throw new InvalidDataException($"Failed to convert TMM file{errorNote}");

            File.WriteAllBytes(out_file, convertedBytes);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError($"Failed to convert to {formatName}:\n" + ex.Message);
        }
    }

    async void MenuItem_ConvertToDDT(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var in_file = await PickFile(sender, "Create DDT from image", [new("Image file") {
            Patterns = ["*.tga", "*.png", "*.jpg", "*.jpeg", "*.webm", "*.bmp"] }]);

        if (in_file == null) return;

        var out_file = PickOutFile(in_file, new_extension: ".ddt", overwrite: true);
        try
        {
            var dialogue = new DDTCreateDialogue(in_file, out_file);
            await dialogue.ShowDialog(this);
        }
        catch (Exception ex)
        {
            _ = ShowError(ex.Message);
        }
    }

    async void MenuItem_ConvertScenarioToXML(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert Scenario to XML", [new("AoM Scenario") { Patterns = ["*.mythscn"] }]);
        if (file == null) return;

        var out_file = PickOutFile(file, new_extension: ".xml", overwrite: true);
        try
        {
            using var data = await PooledBuffer.FromFile(file);
            using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);

            var scenario = new ScenarioFile(decompressed.Memory);
            if (!scenario.Parsed) throw new InvalidDataException("Failed to parse scenario file");

            var xml = scenario.ToXml();
            await File.WriteAllTextAsync(out_file, xml);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert scenario to XML:\n" + ex.Message);
        }
    }

    async void MenuItem_ConvertXMLtoScenario(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Convert XML to Scenario", [new("XML file") { Patterns = ["*.xml"] }]);
        if (file == null) return;

        var out_file = PickOutFile(file, new_extension: ".mythscn", overwrite: true);
        try
        {
            var xml = await File.ReadAllTextAsync(file);
            var scenario = ScenarioFile.FromXml(xml);
            if (!scenario.Parsed) throw new InvalidDataException("Failed to parse scenario XML");

            var bytes = scenario.ToBytes();
            var compressed = BarCompression.CompressL33t(bytes);
            using (var f = File.Create(out_file)) f.Write(compressed.Span);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert XML to scenario:\n" + ex.Message);
        }
    }

    async void MenuItem_CompressAlz4(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Compress using Alz4");
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_alz4");
        try
        {
            var data = File.ReadAllBytes(file);
            var compressed = BarCompression.CompressAlz4(data);
            using (var f = File.Create(out_file)) f.Write(compressed.Span);

            _ = ShowSuccess("Compression completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to compress using Alz4:\n" + ex.Message);
        }
    }

    async void MenuItem_CompressL33t(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Compress using L33t");
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_l33t");
        try
        {
            var data = File.ReadAllBytes(file);
            var compressed = BarCompression.CompressL33t(data);
            using (var f = File.Create(out_file)) f.Write(compressed.Span);

            _ = ShowSuccess("Compression completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to compress using L33t:\n" + ex.Message);
        }
    }

    async void MenuItem_Decompress(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Decompress (Alz4, L33t L66t)");
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_decompressed");
        try
        {
            using var data = await PooledBuffer.FromFile(file);
            using var decompressed = BarCompression.EnsureDecompressedPooled(data, out var type);
            using (var f = File.Create(out_file)) f.Write(decompressed.Span);

            var typeName = type switch
            {
                CompressionType.Alz4 => "Alz4",
                CompressionType.L33t => "L33t",
                _ => "None (file was not compressed)"
            };
            _ = ShowSuccess($"Decompression completed ({typeName}), new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to decompress:\n" + ex.Message);
        }
    }

    async void MenuItem_XStoRM(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var file = await PickFile(sender, "Pick XS script to make RM friendly", [new("XS script") { Patterns = ["*.xs"] }]);
        if (file == null) return;

        var out_file = PickOutFile(file, suffix: "_RMFriendly");
        try
        {
            var class_name = XStoRM.GetSafeClassNameRgx().Replace(Path.GetFileNameWithoutExtension(file), "");
            XStoRM.Convert(file, out_file, class_name);

            _ = ShowSuccess("Conversion completed, new file:\n" + Path.GetFileName(out_file), Path.GetDirectoryName(out_file));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to convert script:\n" + ex.Message);
        }
    }

    SearchWindow? _activeSearchWindow;
    void MenuItem_Search(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_activeSearchWindow != null)
        {
            _activeSearchWindow.Focus();
            return;
        }

        if (_barFile == null && _barStream == null && string.IsNullOrEmpty(_rootDirectory) &&
            !Directory.Exists(_rootDirectory))
        {
            _ = ShowError("No BAR archive or Root directory loaded");
            return;
        }

        _activeSearchWindow = new SearchWindow(this);
        _activeSearchWindow.Closed += (a, b) =>
        {
            _activeSearchWindow = null;
        };

        _activeSearchWindow.Show(this);
    }

    void MenuItem_CreateNewAdditiveMod(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var list = GetContextListBox(sender);
        if (list == null) return;

        var item = (MenuItem)sender!;
        item.IsEnabled = false;

        try
        {
            var relative_path_full = GetContextSelectedRelativePath(list);
            if (relative_path_full == null) return;

            if (!AdditiveModding.IsSupportedFor(relative_path_full, out var format))
                return;

            var output_dir = Path.Combine(_exportRootDirectory, Path.GetDirectoryName(relative_path_full) ?? "");
            Directory.CreateDirectory(output_dir);

            var output_path = Path.Combine(output_dir, format.FileName);
            File.WriteAllText(output_path, format.Content);

            _ = ShowSuccess("Additive mod created, new file:\n" + Path.GetFileName(output_path), Path.GetDirectoryName(output_path));
        }
        catch (Exception ex)
        {
            _ = ShowError("Failed to create additive mod:\n" + ex.Message);
        }
        finally
        {
            item.IsEnabled = true;
        }
    }
    #endregion
}
