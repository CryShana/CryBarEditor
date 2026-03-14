using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CryBar;
using CryBar.Classes;
using CryBarEditor.Classes;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    #region Export core
    public async Task Export<T>(IList<T> files,
        bool should_convert,
        Func<T, string> getFullRelPath,
        Action<T, FileStream> copy,
        Func<T, CancellationToken, ValueTask<PooledBuffer>> read,
        ExportOptions? options = null,
        CancellationToken token = default)
    {
        // TOKEN:  token is not used to cancel export in progress, may want to check this out in future if needed

        var progress = new Progress<string?>();
        IProgress<string?> p = progress;

        _ = ShowProgress($"Exporting {files.Count} files", progress);

        p.Report("Starting export...");

        var sw = Stopwatch.StartNew();
        var isDirectExport = options?.DirectExport == true && !string.IsNullOrEmpty(options.DirectExportPath);
        var shouldDecompress = options?.Decompress == true;
        var exportBaseDir = isDirectExport ? options!.DirectExportPath! : _exportRootDirectory;

        List<string> failed = new();
        List<string> exportedPaths = new();
        foreach (var f in files)
        {
            var relative_path = getFullRelPath(f);
            p.Report($"Exporting '{Path.GetFileName(relative_path)}'");

            try
            {
                // FINALIZE RELATIVE PATH
                var ext = Path.GetExtension(relative_path).ToLower();
                bool isConvertible = should_convert && ConversionHelper.IsConvertibleExtension(ext);
                if (isConvertible)
                {
                    if (ext == ".xmb")
                        relative_path = relative_path[..^4];    // remove .XMB extension, revealing underlying (e.g. .xml)
                    else
                        relative_path = relative_path[..^ext.Length] + ConversionHelper.GetConvertedExtension(ext, options?.TmmToGltf == true);
                }

                // DETERMINE EXPORT PATH
                string exported_path;
                if (isDirectExport)
                {
                    // Direct export: flat, just the filename in the chosen directory
                    exported_path = Path.Combine(exportBaseDir, Path.GetFileName(relative_path));
                }
                else
                {
                    exported_path = Path.Combine(exportBaseDir, relative_path);
                }

                // Apply override base name (single-file export)
                if (!string.IsNullOrEmpty(options?.OverrideBaseName))
                {
                    var dir = Path.GetDirectoryName(exported_path);
                    var finalExt = Path.GetExtension(exported_path);
                    exported_path = Path.Combine(dir ?? "", options.OverrideBaseName + finalExt);
                }

                // CREATE MISSING DIRECTORIES
                var dirs = Path.GetDirectoryName(exported_path);
                if (dirs != null) Directory.CreateDirectory(dirs);

                // CREATE FILE
                exportedPaths.Add(exported_path);
                using var file = File.Create(exported_path);

                // EXPORT DATA
                if (isConvertible || shouldDecompress)
                {
                    using var rawData = await read(f, token);
                    using var data = BarCompression.EnsureDecompressedPooled(rawData, out _);

                    if (isConvertible)
                    {
                        var xmlBytes = ext == ".xmb" ? ConversionHelper.ConvertXmbToXmlBytes(data.Span) : null;
                        if (xmlBytes != null)
                        {
                            file.Write(xmlBytes);
                            continue;
                        }

                        var tgaBytes = ext == ".ddt" ? await ConversionHelper.ConvertDdtToTgaBytes(data.Memory) : null;
                        if (tgaBytes != null)
                        {
                            file.Write(tgaBytes);
                            continue;
                        }

                        // TMM->OBJ/glTF: find companion .tmm.data
                        if (ext == ".tmm")
                        {
                            var tmmFileName = Path.GetFileName(getFullRelPath(f));
                            using var companionData = await ResolveCompanionDataAsync(tmmFileName + ".data");
                            if (companionData != null)
                            {
                                byte[]? convertedBytes;
                                if (options?.TmmToGltf == true)
                                {
                                    var glbMaterials = (options.ExportMaterials && _fileIndex != null)
                                        ? await BuildGlbMaterials(tmmFileName) : null;
                                    convertedBytes = ConversionHelper.ConvertTmmToGlbBytes(data.Memory, companionData.Memory, glbMaterials);
                                }
                                else
                                {
                                    var mtlName = (options?.ExportMaterials == true) ? Path.GetFileNameWithoutExtension(tmmFileName) + ".mtl" : null;
                                    convertedBytes = ConversionHelper.ConvertTmmToObjBytes(data.Memory, companionData.Memory, mtlName);
                                    if (convertedBytes != null && options?.ExportMaterials == true && _fileIndex != null)
                                        await ExportObjMaterials(tmmFileName, exported_path);
                                }

                                if (convertedBytes != null)
                                {
                                    file.Write(convertedBytes);
                                    continue;
                                }
                            }
                        }
                    }

                    // Conversion didn't apply or failed - write decompressed data
                    file.Write(data.Span);
                }
                else
                {
                    copy(f, file);
                }
            }
            catch
            {
                failed.Add(relative_path);
            }
        }

        sw.Stop();

        if (failed.Count > 0)
        {
            p.Report($"Finished in {sw.Elapsed.TotalSeconds:0.00} seconds with {failed.Count} failed exports:\n"
                + string.Join(",", failed.Select(x => Path.GetFileName(x))));
        }
        else
        {
            p.Report($"Finished in {sw.Elapsed.TotalSeconds:0.00} seconds");
        }

        // Open in editor if requested
        if (options?.OpenInEditor == true && !string.IsNullOrWhiteSpace(_editorCommand))
        {
            foreach (var ep in exportedPaths)
            {
                if (!TryLaunchEditorForFile(ep))
                    break;
            }
        }

        p.Report(null);
    }
    #endregion

    #region Companion/Material resolution
    /// <summary>
    /// Finds and decompresses a companion file (e.g. .tmm.data) by searching
    /// the current BAR, sibling BARs, and the file index.
    /// </summary>
    async ValueTask<PooledBuffer?> ResolveCompanionDataAsync(string dataFileName)
    {
        // Check current BAR first
        if (_barFile?.Entries != null && _barStream != null)
        {
            var barDataEntry = _barFile.Entries.FirstOrDefault(e =>
                e.Name.Equals(dataFileName, StringComparison.OrdinalIgnoreCase));

            if (barDataEntry != null)
            {
                using var dataRaw = await barDataEntry.ReadDataRawPooledAsync(_barStream);
                return BarCompression.EnsureDecompressedPooled(dataRaw, out _);
            }
        }

        // Search sibling .bar files
        if (_barStream != null)
        {
            var found = await FindCompanionInSiblingBars(
                _barStream.Name, dataFileName,
                async (entry, stream) =>
                {
                    using var data = await entry.ReadDataRawPooledAsync(stream);
                    return BarCompression.EnsureDecompressedPooled(data, out _);
                }
            );

            if (found?.Length > 0) 
                return found;
        }

        // Fallback: file index
        if (_fileIndex != null)
        {
            foreach (var ie in _fileIndex.Find(dataFileName))
            {
                var indexData = await ReadFromIndexEntryPooledAsync(ie);
                if (indexData != null) return indexData;
            }
        }

        return null;
    }

    /// <summary>
    /// Builds glTF material list with embedded PNG textures for a TMM model.
    /// </summary>
    async ValueTask<IReadOnlyList<GlbExporter.GlbMaterial>?> BuildGlbMaterials(string tmmFileName)
    {
        var resolved = await ResolveTmmMaterialsAsync(tmmFileName);
        if (resolved == null) return null;

        // Launch all texture conversions in parallel
        var textureTasks = new Dictionary<string, Task<byte[]?>>();
        foreach (var mat in resolved.Value.Materials)
        {
            foreach (var (_, texPath) in mat.Textures)
            {
                if (!textureTasks.ContainsKey(texPath) &&
                    resolved.Value.Textures.TryGetValue(texPath, out var texInfo))
                {
                    textureTasks[texPath] = ConversionHelper.ConvertDdtToPngBytes(texInfo.DdtData);
                }
            }
        }
        await Task.WhenAll(textureTasks.Values);

        // Build material list using completed results
        var matList = new List<GlbExporter.GlbMaterial>();
        foreach (var mat in resolved.Value.Materials)
        {
            byte[]? baseColorPng = null, normalPng = null;
            foreach (var (texName, texPath) in mat.Textures)
            {
                if (textureTasks.TryGetValue(texPath, out var task))
                {
                    var pngBytes = task.Result;
                    if (pngBytes != null)
                    {
                        switch (texName.ToLowerInvariant())
                        {
                            case "basecolor":
                            case "diffuse":
                                baseColorPng = pngBytes;
                                break;
                            case "normals":
                            case "normal":
                                normalPng = pngBytes;
                                break;
                        }
                    }
                }
            }
            matList.Add(new GlbExporter.GlbMaterial { Name = mat.Name, BaseColorPng = baseColorPng, NormalMapPng = normalPng });
        }
        return matList;
    }

    /// <summary>
    /// Exports .mtl file and textures as TGA alongside an OBJ file (best-effort).
    /// </summary>
    async ValueTask ExportObjMaterials(string tmmFileName, string exportedObjPath)
    {
        try
        {
            var resolved = await ResolveTmmMaterialsAsync(tmmFileName);
            if (resolved == null) return;

            var exportDir = Path.GetDirectoryName(exportedObjPath)!;

            // Launch all texture conversions in parallel
            var textureTasks = resolved.Value.Textures.ToDictionary(
                kvp => kvp.Key,
                kvp => ConversionHelper.ConvertDdtToTgaBytes(kvp.Value.DdtData));
            await Task.WhenAll(textureTasks.Values);

            var resolvedTextures = new Dictionary<string, string>();
            foreach (var (texPath, (texFileName, _)) in resolved.Value.Textures)
            {
                var texTgaBytes = textureTasks[texPath].Result;
                if (texTgaBytes != null)
                {
                    var tgaFileName = texFileName + ".tga";
                    File.WriteAllBytes(Path.Combine(exportDir, tgaFileName), texTgaBytes);
                    resolvedTextures[texPath] = tgaFileName;
                }
            }

            var mtlContent = MaterialExporter.GenerateMtl(resolved.Value.Materials, resolvedTextures);
            var mtlPath = Path.Combine(exportDir, Path.GetFileNameWithoutExtension(exportedObjPath) + ".mtl");
            File.WriteAllText(mtlPath, mtlContent);
        }
        catch { /* material export is best-effort */ }
    }

    /// <summary>
    /// Resolves materials and textures for a TMM model via FileIndex.
    /// Returns parsed materials + raw decompressed DDT bytes per texture path.
    /// </summary>
    async ValueTask<(List<MaterialInfo> Materials, Dictionary<string, (string FileName, Memory<byte> DdtData)> Textures)?> 
        ResolveTmmMaterialsAsync(string tmmFileName)
    {
        if (_fileIndex == null) 
            return null;

        try
        {
            var tmmName = Path.GetFileNameWithoutExtension(tmmFileName);
            var materialName = tmmName + ".material";

            // Try .material.XMB first, then .material
            var materialEntries = _fileIndex.Find(materialName + ".XMB");
            if (materialEntries.Count == 0)
                materialEntries = _fileIndex.Find(materialName);

            if (materialEntries.Count == 0) return null;

            var matEntry = materialEntries[0];
            using var matData = await ReadFromIndexEntryPooledAsync(matEntry);
            if (matData == null) return null;

            // Convert XMB to XML if needed
            using var matBytes = BarCompression.EnsureDecompressedPooled(matData, out _);

            string? xmlText;
            if (matEntry.FileName.EndsWith(".XMB", StringComparison.OrdinalIgnoreCase))
            {
                xmlText = ConversionHelper.ConvertXmbToXmlText(matBytes.Span);
            }
            else
            {
                xmlText = Encoding.UTF8.GetString(matBytes.Span);
            }

            if (xmlText == null) return null;

            var materials = MaterialExporter.ParseMaterialXml(xmlText);
            var texturePaths = MaterialExporter.GetAllTexturePaths(materials);
            var textures = new Dictionary<string, (string FileName, Memory<byte> DdtData)>();

            // Find each texture
            foreach (var texPath in texturePaths)
            {
                var texFileName = Path.GetFileName(texPath.Replace('\\', '/'));

                var texEntries = _fileIndex.Find(texFileName + ".ddt");
                if (texEntries.Count == 0)
                    texEntries = _fileIndex.Find(texFileName);

                if (texEntries.Count > 0)
                {
                    using var texData = await ReadFromIndexEntryPooledAsync(texEntries[0]);
                    if (texData != null)
                    {
                        using var decompressedTex = BarCompression.EnsureDecompressedPooled(texData, out _);
                        // Must copy: dictionary outlives the pooled buffer
                        textures[texPath] = (texFileName, decompressedTex.Span.ToArray());
                    }
                }
            }

            return (materials, textures);
        }
        catch
        {
            return null;
        }
    }
    #endregion

    #region Export menu events
    async void MenuItem_ExportSelectedRaw(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => MenuItem_Export(sender, copy: true, convert: false);

    async void MenuItem_ExportSelectedConverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => MenuItem_Export(sender, copy: false, convert: true);

    async void MenuItem_ExportSelectedRawConverted(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => MenuItem_Export(sender, copy: true, convert: true);

    async void MenuItem_AdvancedExport(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        var files = GetContextSelectedExportFiles(list);
        if (files.Count == 0) return;

        var window = new AdvancedExportWindow(files, _exportRootDirectory, isDirectExport: false, directExportPath: null, _lastConfiguration);
        await window.ShowDialog(this);

        var options = window.GetResult();
        if (options == null) return;

        SaveExportConfiguration(options);
        await RunExportWithOptions(list, options);
    }

    async void MenuItem_ExportTo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        var files = GetContextSelectedExportFiles(list);
        if (files.Count == 0) return;

        // Pick a destination folder
        var folders = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
        {
            Title = "Export files to...",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var directPath = folders[0].Path.LocalPath;

        var window = new AdvancedExportWindow(files, _exportRootDirectory, isDirectExport: true, directExportPath: directPath, _lastConfiguration);
        await window.ShowDialog(this);

        var options = window.GetResult();
        if (options == null) return;

        SaveExportConfiguration(options);
        await RunExportWithOptions(list, options);
    }

    async Task RunExportWithOptions(ListBox list, ExportOptions options)
    {
        bool withinBAR = IsContextFromBAR(list);

        if (withinBAR)
        {
            var to_export = SelectedBarFileEntries.ToArray();
            if (options.Copy)
                await Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR, options);
            if (options.Convert)
                await Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR, options);
        }
        else
        {
            var to_export = SelectedRootFileEntries.ToArray();
            if (options.Copy)
                await Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot, options);
            if (options.Convert)
                await Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot, options);
        }
    }

    async void MenuItem_ReplaceImageAndExportDDT(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var list = GetContextListBox(sender);
        if (list == null) return;

        var item = (MenuItem)sender!;
        item.IsEnabled = false;

        try
        {
            string? relative_path_full;
            string title;
            Memory<byte> data;
            if (IsContextFromBAR(list))
            {
                if (SelectedBarEntry == null || _barStream == null)
                    return;

                relative_path_full = GetBARFullRelativePath(SelectedBarEntry);
                data = SelectedBarEntry.ReadDataDecompressed(_barStream);
                title = $"Pick image to replace {Path.GetFileName(SelectedBarEntry.RelativePath)}";
            }
            else
            {
                if (SelectedRootFileEntry == null || !Directory.Exists(_rootDirectory))
                    return;

                relative_path_full = GetRootFullRelativePath(SelectedRootFileEntry);
                data = BarCompression.EnsureDecompressed(File.ReadAllBytes(Path.Combine(_rootDirectory, SelectedRootFileEntry.RelativePath)), out _);
                title = $"Pick image to replace {Path.GetFileName(SelectedRootFileEntry.RelativePath)}";
            }

            var ddt = new DDTImage(data);
            if (!ddt.ParseHeader())
            {
                _ = ShowError("Failed to parse DDT header");
                return;
            }

            var file = await PickFile(sender, title, [new("Image") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.webp"] }]);
            if (file == null) return;

            var progress = new Progress<string?>();
            IProgress<string?> p = progress;
            _ = ShowProgress($"Exporting with new image", progress);

            try
            {
                var sw = Stopwatch.StartNew();

                p.Report("Loading target image");
                using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(file);

                p.Report("Encoding image into DDT");
                var modified_ddt_data = await DDTImage.EncodeImageToDDT(image, ddt.Version, ddt.UsageFlag, ddt.AlphaFlag, ddt.FormatFlag, ddt.MipmapLevels, ddt.ColorTable);

                p.Report("Exporting final DDT");
                var output_path = Path.Combine(_exportRootDirectory, relative_path_full);
                var dir = Path.GetDirectoryName(output_path);
                if (dir != null) Directory.CreateDirectory(dir);

                using (var out_file = File.Create(output_path))
                    out_file.Write(modified_ddt_data.Span);

                sw.Stop();
                p.Report($"Exported in {sw.Elapsed.TotalSeconds:0.00} seconds");
            }
            catch (Exception ex)
            {
                p.Report("Failed to export: " + ex.Message);
            }
            finally
            {
                p.Report(null);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    async void MenuItem_Export(object? sender, bool copy, bool convert)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var list = GetContextListBox(sender);
        if (list == null) return;

        var item = (MenuItem)sender!;
        item.IsEnabled = false;

        bool withinBAR = IsContextFromBAR(list);
        try
        {
            if (withinBAR)
            {
                var to_export = SelectedBarFileEntries.ToArray();
                if (copy)
                    await Export(to_export, false, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
                if (convert)
                    await Export(to_export, true, F_GetFullRelativePathBAR, F_CopyBAR, F_ReadBAR);
            }
            else
            {
                var to_export = SelectedRootFileEntries.ToArray();
                if (copy)
                    await Export(to_export, false, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
                if (convert)
                    await Export(to_export, true, F_GetFullRelativePathRoot, F_CopyRoot, F_ReadRoot);
            }
        }
        finally
        {
            item.IsEnabled = true;
        }
    }

    async void BankItem_Export(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedBankEntry == null || FmodBank == null)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            DefaultExtension = ".wav",
            SuggestedFileName = Path.GetFileNameWithoutExtension(SelectedBankEntry.Path) + ".wav",
            Title = "Export FMOD event"
        });

        if (file == null)
            return;

        bank_play_csc?.Cancel();
        bank_play_csc = new();

        try
        {
            var outputPath = file.Path.LocalPath;
            SelectedBankEntry.Export(outputPath, bank_play_csc.Token);
            FMODEvent.TrimSilence(outputPath);

            _ = ShowSuccess("FMOD event export completed.\n");
        }
        catch (Exception ex)
        {
            _ = ShowError("FMOD event export failed.\n" + ex.Message);
        }
        finally
        {

        }
    }
    #endregion
}
