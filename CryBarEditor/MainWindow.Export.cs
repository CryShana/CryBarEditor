using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using CryBar;
using CryBar.Bar;
using CryBar.Dependencies;
using CryBar.Export;
using CryBar.Sound;
using CryBar.TMM;
using CryBar.Indexing;
using CryBar.Utilities;
using CryBarEditor.Classes;
using CryBarEditor.Windows;
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

        var prompt = ShowProgress($"Exporting {files.Count} files", progress);

        p.Report("Starting export...");

        // animfile lookup is now on-demand, no pre-indexing needed

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

                        if (ext == ".dds")
                        {
                            var ddsPngBytes = await ConversionHelper.ConvertDdsToPngBytes(data.Memory);
                            if (ddsPngBytes == null)
                                throw new InvalidDataException("Unsupported DDS variant (cubemap/HDR)");
                            file.Write(ddsPngBytes);
                            continue;
                        }

                        // TMM->OBJ/glTF: find companion .tmm.data
                        if (ext == ".tmm")
                        {
                            var tmmFullRelPath = getFullRelPath(f);
                            var tmmFileName = Path.GetFileName(tmmFullRelPath);
                            var tmmRelativeDir = Path.GetDirectoryName(tmmFullRelPath);
                            using var companionData = await ResolveCompanionDataAsync(tmmFileName + ".data", tmmRelativeDir);
                            if (companionData != null)
                            {
                                byte[]? convertedBytes;
                                List<(string Name, TmaFile Tma)>? sourceTmasForFbx = null;
                                if (options?.TmmToGltf == true)
                                {
                                    var sourceDdts = new List<(string Material, DDTImage Ddt)>();
                                    var sourceTmas = new List<(string Name, TmaFile Tma)>();
                                    sourceTmasForFbx = sourceTmas;

                                    var glbMaterials = (options.ExportMaterials && _fileIndex != null)
                                        ? await BuildGlbMaterials(tmmFileName, sourceDdts) : null;
                                    var glbAnimations = (options.ExportAnimations && _fileIndex != null)
                                        ? await BuildGlbAnimations(tmmFileName, sourceTmas) : null;

                                    convertedBytes = ConversionHelper.ConvertTmmToGlbBytes(
                                        data.Memory, companionData.Memory,
                                        glbMaterials, glbAnimations,
                                        sourceTmas: sourceTmas,
                                        sourceDdts: sourceDdts);
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

                                    if (options?.EmitFbximport == true && sourceTmasForFbx != null)
                                        await EmitFbximportSidecars(exported_path, data.Memory, sourceTmasForFbx);

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
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(relative_path)}: {ex.Message}");
            }
        }

        sw.Stop();

        if (failed.Count > 0)
        {
            p.Report($"Finished in {sw.Elapsed.TotalSeconds:0.00} seconds with {failed.Count} failed exports:\n"
                + string.Join("\n", failed));
        }
        else
        {
            p.Report($"Finished in {sw.Elapsed.TotalSeconds:0.00} seconds");
        }

        prompt.OpenFolderPath = exportBaseDir;

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
    /// When preferredRelativeDir is provided, disambiguates among multiple name matches.
    /// </summary>
    async ValueTask<PooledBuffer?> ResolveCompanionDataAsync(string dataFileName,
        string? preferredRelativeDir = null)
    {
        // Check current BAR first
        if (_barFile?.Entries != null && _barStream != null)
        {
            var candidates = _barFile.Entries
                .Where(e => e.Name.Equals(dataFileName, StringComparison.OrdinalIgnoreCase));
            var barDataEntry = BestMatchByDirectorySuffix(candidates, preferredRelativeDir);

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
                async (entry, cachedBar) =>
                {
                    using var data = await cachedBar.ReadEntryRawPooledAsync(entry);
                    return data != null ? BarCompression.EnsureDecompressedPooled(data, out _) : null;
                },
                preferredRelativeDir
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

    static async Task EmitFbximportSidecars(
        string glbExportedPath,
        ReadOnlyMemory<byte> tmmData,
        IReadOnlyList<(string Name, TmaFile Tma)> sourceTmas)
    {
        var dir = Path.GetDirectoryName(glbExportedPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(glbExportedPath);

        var tmm = new TmmFile(tmmData);
        if (!tmm.Parsed) return;

        var hasSkin = tmm.Bones is { Length: > 0 };
        var extras = GlbExtras.From(tmm, sourceTmas, []);

        var tmmFbx = FbximportEmitter.EmitForTmm(extras.Tmm, hasSkin);
        await File.WriteAllBytesAsync(Path.Combine(dir, stem + ".fbximport"), tmmFbx);

        foreach (var (name, tma) in sourceTmas)
        {
            if (!tma.Parsed) continue;
            extras.Tma.TryGetValue(name, out var section);
            var tmaFbx = FbximportEmitter.EmitForTma(section, tma.Duration);
            var safe = SanitizeFileName(name);
            await File.WriteAllBytesAsync(Path.Combine(dir, safe + ".fbximport"), tmaFbx);
        }
    }

    static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// <summary>
    /// Builds glTF material list with embedded PNG textures for a TMM model.
    /// </summary>
    async ValueTask<IReadOnlyList<GlbExporter.GlbMaterial>?> BuildGlbMaterials(
        string tmmFileName,
        List<(string Material, DDTImage Ddt)>? sourceDdtsOut = null)
    {
        var resolver = GetMaterialResolver();
        if (resolver == null) return null;
        var resolved = await resolver.ResolveAsync(tmmFileName);
        if (resolved == null) return null;

        var textureTasks = new Dictionary<string, Task<byte[]?>>();
        foreach (var mat in resolved.Value.Materials)
        {
            foreach (var (texName, texPath) in mat.Textures)
            {
                var role = MaterialExporter.ClassifyRole(texName);
                if (role is null) continue;

                if (!textureTasks.ContainsKey(texPath) &&
                    resolved.Value.Textures.TryGetValue(texPath, out var texInfo))
                {
                    textureTasks[texPath] = ConversionHelper.ConvertDdtToPngBytes(texInfo.DdtData);

                    if (sourceDdtsOut != null)
                    {
                        var img = new DDTImage(texInfo.DdtData);
                        if (img.ParseHeader())
                            sourceDdtsOut.Add((MaterialExporter.GetDdtKey(mat.Name, role.Value), img));
                    }
                }
            }
        }
        await Task.WhenAll(textureTasks.Values);

        var matList = new List<GlbExporter.GlbMaterial>();
        foreach (var mat in resolved.Value.Materials)
        {
            byte[]? baseColorPng = null, normalPng = null, mask1Png = null, mask2Png = null;
            foreach (var (texName, texPath) in mat.Textures)
            {
                if (!textureTasks.TryGetValue(texPath, out var task)) continue;
                var pngBytes = task.Result;
                if (pngBytes == null) continue;

                switch (MaterialExporter.ClassifyRole(texName))
                {
                    case TextureRole.BaseColor: baseColorPng = pngBytes; break;
                    case TextureRole.Normal:    normalPng    = pngBytes; break;
                    case TextureRole.Masks1:    mask1Png     = pngBytes; break;
                    case TextureRole.Masks2:    mask2Png     = pngBytes; break;
                }
            }
            matList.Add(new GlbExporter.GlbMaterial
            {
                Name = mat.Name,
                BaseColorPng = baseColorPng,
                NormalMapPng = normalPng,
                Mask1Png = mask1Png,
                Mask2Png = mask2Png,
            });
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
            var resolver = GetMaterialResolver();
            if (resolver == null) return;
            var resolved = await resolver.ResolveAsync(tmmFileName);
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
    /// Discovers and decodes TMA animations for a TMM model via on-demand animfile search.
    /// </summary>
    async ValueTask<IReadOnlyList<GlbExporter.GlbAnimation>?> BuildGlbAnimations(
        string tmmFileName,
        List<(string Name, TmaFile Tma)>? sourceTmasOut = null)
    {
        if (_fileIndex == null) return null;

        try
        {
            var tmmStem = Path.GetFileNameWithoutExtension(tmmFileName);
            var animfileEntry = await DependencyFinder.FindAnimfileForTmmAsync(
                tmmStem, _fileIndex, ReadFromIndexEntryPooledAsync);
            if (animfileEntry is not { } animfile) return null;

            // read and parse the animfile XML
            using var data = await ReadFromIndexEntryPooledAsync(animfile);
            if (data == null) return null;

            using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
            var xmlText = ConversionHelper.GetTextContent(decompressed.Span, animfile.FileName.ToString());

            var animRefs = AnimationDiscovery.FindAnimationsFromAnimXml(xmlText);
            if (animRefs.Count == 0) return null;

            var animations = new List<GlbExporter.GlbAnimation>();
            foreach (var animRef in animRefs)
            {
                var tmaFileName = Path.GetFileName(animRef.TmaPath.Replace('\\', '/'));
                if (string.IsNullOrEmpty(tmaFileName)) continue;

                var tmaEntries = _fileIndex.Find(tmaFileName + ".tma");
                if (tmaEntries.Count == 0)
                    tmaEntries = _fileIndex.Find(tmaFileName);
                if (tmaEntries.Count == 0) continue;

                using var tmaData = await ReadFromIndexEntryPooledAsync(tmaEntries[0]);
                if (tmaData == null) continue;

                using var tmaDecompressed = BarCompression.EnsureDecompressedPooled(tmaData, out _);
                var tma = new TmaFile(tmaDecompressed.Memory);
                if (!tma.Parsed) continue;

                var decoded = TmaDecoder.DecodeAllTracks(tma);
                if (decoded == null || decoded.Length == 0) continue;

                var baseName = !string.IsNullOrEmpty(animRef.AnimName) ? animRef.AnimName : tmaFileName;
                // sourceTmasOut must stay in lockstep with animations (same index, same order)
                // so the dedup-rename pass below can update both lists by index.
                animations.Add(new GlbExporter.GlbAnimation
                {
                    Name = baseName,
                    Tracks = decoded,
                    Duration = tma.Duration,
                    FrameCount = tma.FrameCount,
                });
                sourceTmasOut?.Add((baseName, tma));
            }

            // deduplicate names: "Attack" stays if unique, becomes "Attack 1", "Attack 2" if not
            var nameCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var anim in animations)
                nameCounts[anim.Name] = nameCounts.GetValueOrDefault(anim.Name) + 1;

            var nameCounters = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < animations.Count; i++)
            {
                var anim = animations[i];
                if (nameCounts[anim.Name] <= 1) continue;
                int idx = nameCounters.GetValueOrDefault(anim.Name) + 1;
                nameCounters[anim.Name] = idx;
                var newName = $"{anim.Name} {idx}";
                anim.Name = newName;
                if (sourceTmasOut != null && i < sourceTmasOut.Count)
                    sourceTmasOut[i] = (newName, sourceTmasOut[i].Tma);
            }

            return animations.Count > 0 ? animations : null;
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
            PooledBuffer? pooledData = null;
            if (IsContextFromBAR(list))
            {
                if (SelectedBarEntry == null || _barStream == null)
                    return;

                relative_path_full = GetBARFullRelativePath(SelectedBarEntry);
                pooledData = SelectedBarEntry.ReadDataDecompressedPooled(_barStream);
                data = pooledData?.Memory ?? Memory<byte>.Empty;
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
            pooledData?.Dispose();
            if (!ddt.ParseHeader())
            {
                _ = ShowError("Failed to parse DDT header");
                return;
            }

            var file = await PickFile(sender, title, [new("Image") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.tga", "*.bmp", "*.webp"] }]);
            if (file == null) return;

            var progress = new Progress<string?>();
            IProgress<string?> p = progress;
            var prompt = ShowProgress($"Exporting with new image", progress);

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
                prompt.OpenFolderPath = dir;
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

    /// <summary>
    /// "Export (copy)": writes the selected bank items into the configured Export Root Directory,
    /// always recreating each sound's resolved relative folder tree (like BAR-entry copy export).
    /// </summary>
    async void BankItem_ExportCopy(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (FmodBank == null || !Directory.Exists(_exportRootDirectory)) return;

        var items = SelectedBankEntries.ToArray();
        if (items.Length == 0) return;

        var jobs = await BuildBankExportJobsAsync(items, _exportRootDirectory, retainSubfolders: true, allVariants: true);
        var label = items.Length == 1 ? "entry" : "entries";
        await RunBankJobsExport($"Exporting {items.Length} FMOD {label} (copy)", _exportRootDirectory, jobs);
    }

    /// <summary>
    /// "Export to...": pick a folder, then the advanced window (retain-subfolders + all-variants),
    /// and export the selected bank items there.
    /// </summary>
    async void BankItem_ExportTo(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (FmodBank == null) return;

        var items = SelectedBankEntries.ToArray();
        if (items.Length == 0) return;

        var label = items.Length == 1 ? "entry" : "entries";
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Export {items.Length} FMOD {label} to...",
            AllowMultiple = false
        });
        if (folders.Count == 0) return;
        var outputDir = folders[0].Path.LocalPath;

        bool anyEvents = items.Any(i => i.IsEvent);
        var window = new AdvancedExportWindow(items.Length, anyEvents, outputDir, _lastConfiguration);
        await window.ShowDialog(this);

        var options = window.GetResult();
        if (options == null) return;

        // Persist only the bank toggles - the full SaveExportConfiguration would clobber the
        // (inapplicable here) conversion preferences.
        _lastConfiguration ??= new Configuration();
        _lastConfiguration.ExportAllEventVariants = options.ExportAllEventVariants;
        _lastConfiguration.ExportRetainSubfolders = options.RetainSubfolders;
        SaveConfiguration();

        var jobs = await BuildBankExportJobsAsync(items, outputDir,
            retainSubfolders: options.RetainSubfolders, allVariants: options.ExportAllEventVariants);
        await RunBankJobsExport($"Exporting {items.Length} FMOD {label}", outputDir, jobs);
    }

    /// <summary>Copies the resolved file path(s) of the selected bank item to the clipboard.</summary>
    async void BankItem_CopyFilePath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var entry = SelectedBankEntry;
        if (entry == null) return;

        await ResolveBankItemPathsAsync();

        string? text = null;
        if (entry is FMODSubsound s)
        {
            // ResolvedPath/FullRelativePath are already duration-disambiguated to a single best path.
            text = s.FullRelativePath;
        }
        else if (entry is FMODEvent ev)
        {
            var subs = await ResolveEventSubsoundsAsync(ev);
            var paths = subs.Where(x => x.FullRelativePath != null)
                            .Select(x => x.FullRelativePath!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
            if (paths.Count > 0) text = string.Join("\n", paths);
        }

        if (!string.IsNullOrEmpty(text))
            Clipboard?.SetTextAsync(text);
    }

    /// <summary>A single planned bank WAV export - its <see cref="Run"/> writes the file(s).</summary>
    sealed class BankExportJob
    {
        public required string Label;
        public required Action<IProgress<string?>, CancellationToken> Run;
    }

    /// <summary>
    /// Plans bank export jobs (async, needs the file index). Subsounds export directly to their
    /// resolved path; events fan out to their matching in-bank subsounds, falling back to the
    /// render-retry path only when no subsounds back the event's variants.
    /// </summary>
    async Task<List<BankExportJob>> BuildBankExportJobsAsync(
        IReadOnlyList<IBankItem> items, string baseDir, bool retainSubfolders, bool allVariants)
    {
        await ResolveBankItemPathsAsync(); // ensure subsounds carry resolved paths

        var jobs = new List<BankExportJob>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddSubsoundJob(FMODSubsound sub)
        {
            var target = ComputeSubsoundTarget(sub, baseDir, retainSubfolders, used);
            jobs.Add(new BankExportJob
            {
                Label = sub.Name,
                Run = (p, token) => ExportWavTo(target, p, t => sub.Export(t, token)),
            });
        }

        void AddRenderSingleJob(FMODEvent ev, string evBase)
        {
            var target = Path.Combine(baseDir, UniqueName(used, evBase, ".wav"));
            jobs.Add(new BankExportJob
            {
                Label = evBase,
                Run = (p, token) => ExportWavTo(target, p, t => { ev.Export(t, token); FMODEvent.TrimSilence(t); }),
            });
        }

        foreach (var item in items)
        {
            if (item is FMODSubsound s)
            {
                AddSubsoundJob(s);
                continue;
            }
            if (item is not FMODEvent ev) continue;

            var resolution = await ResolveFmodEventSoundsAsync(ev);
            var matched = resolution != null ? MatchEventSubsounds(resolution) : [];

            var evBase = SanitizeFileName(SoundsetParser.ExtractEventName(ev.Path)
                ?? Path.GetFileNameWithoutExtension(ev.DisplayName));
            if (string.IsNullOrWhiteSpace(evBase)) evBase = "event";

            if (matched.Count > 0)
            {
                // Deterministic: export the backing subsound(s) directly.
                foreach (var sub in allVariants ? matched : matched.Take(1)) AddSubsoundJob(sub);
            }
            else if (allVariants && resolution is { Soundset.Sounds.Count: > 0 })
            {
                // Fallback: render every variant. Names are flat into baseDir; prefix keeps two
                // same-named events from clobbering each other's {prefix}-{n}.wav.
                var prefix = UniqueName(used, evBase, "");
                var targetNames = SortedVariantFilenames(resolution.Soundset.Sounds)
                    .Select((_, i) => $"{prefix}-{i + 1}.wav").ToList();
                jobs.Add(new BankExportJob
                {
                    Label = evBase,
                    Run = (p, token) =>
                    {
                        p.Report($"Rendering variants of {evBase}...");
                        ExportEventVariants(ev, targetNames, baseDir, p, token, fallbackPrefix: prefix);
                    },
                });
            }
            else
            {
                // Fallback: render a single representative take.
                AddRenderSingleJob(ev, evBase);
            }
        }

        return jobs;
    }

    /// <summary>
    /// Output path for a subsound: its resolved relative tree under <paramref name="baseDir"/>
    /// (extension forced to .wav since we export decoded PCM) when retaining subfolders and a
    /// path is known; otherwise a unique flat <c>name.wav</c>.
    /// </summary>
    string ComputeSubsoundTarget(FMODSubsound s, string baseDir, bool retainSubfolders, HashSet<string> used)
    {
        if (retainSubfolders && !string.IsNullOrEmpty(s.FullRelativePath))
            return Path.Combine(baseDir, Path.ChangeExtension(s.FullRelativePath, ".wav"));

        var baseName = SanitizeFileName(s.Name);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "sound";
        return Path.Combine(baseDir, UniqueName(used, baseName, ".wav"));
    }

    /// <summary>Creates the target's directory, reports progress, then runs <paramref name="write"/>.</summary>
    static void ExportWavTo(string target, IProgress<string?> p, Action<string> write)
    {
        var dir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        p.Report($"Exporting {Path.GetFileName(target)}...");
        write(target);
    }

    /// <summary>Runs a planned set of bank export jobs through the modal progress scaffolding.</summary>
    async Task RunBankJobsExport(string title, string baseDir, List<BankExportJob> jobs)
    {
        await RunBankExport(title, baseDir, (p, v, token) =>
        {
            var sw = Stopwatch.StartNew();
            int ok = 0, failed = 0, done = 0;

            foreach (var job in jobs)
            {
                token.ThrowIfCancellationRequested();
                try { job.Run(p, token); ok++; }
                catch (OperationCanceledException) { throw; }
                catch { failed++; }

                v.Report(jobs.Count == 0 ? 1 : (double)++done / jobs.Count);
            }

            sw.Stop();
            var msg = $"Exported {ok}/{jobs.Count} item(s) in {sw.Elapsed.TotalSeconds:0.00}s";
            if (failed > 0) msg += $" ({failed} failed)";
            p.Report(msg);
        });
    }

    /// <summary>
    /// Shared scaffolding for bank WAV exports: opens a modal progress dialog (disabling the main
    /// window), stops any current playback, then runs <paramref name="work"/> on a background thread
    /// with cancellation and uniform cancelled/failed reporting.
    /// </summary>
    async Task RunBankExport(string title, string outputDir, Action<IProgress<string?>, IProgress<double>, CancellationToken> work)
    {
        var progress = new Progress<string?>();
        var progressValue = new Progress<double>();
        IProgress<string?> p = progress;
        IProgress<double> v = progressValue;
        var prompt = ShowProgress(title, progress, progressValue);
        prompt.OpenFolderPath = outputDir;

        StopBankPlayback();
        bank_play_csc = new();
        var token = bank_play_csc.Token;

        await Task.Run(() =>
        {
            try { work(p, v, token); }
            catch (OperationCanceledException) { p.Report("Export cancelled."); }
            catch (Exception ex) { p.Report($"Export failed: {ex.Message}"); }
            finally { p.Report(null); }
        }, token);
    }

    /// <summary>Returns baseName+ext, suffixing _2, _3, ... until unique within <paramref name="used"/>.</summary>
    static string UniqueName(HashSet<string> used, string baseName, string ext)
    {
        var name = baseName + ext;
        int n = 2;
        while (!used.Add(name))
            name = $"{baseName}_{n++}{ext}";

        return name;
    }
    #endregion
}
