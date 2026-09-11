using System.CommandLine;
using System.Text;
using System.Xml;

using CryBar.Bar;
using CryBar.BCnEncoder.Shared;
using CryBar.Cli.Config;
using CryBar.Cli.Helpers;
using CryBar.Export;
using CryBar.Scenario;
using CryBar.Utilities;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class ConvertCommands
{
    public static Command Create()
    {
        var convertCommand = new Command("convert", "Format conversions");

        convertCommand.Add(BuildAsync("xmb-to-xml", "Convert XMB to XML", "Path to .xmb file", ".xml",
            async (input, output) =>
            {
                using var raw = await PooledBuffer.FromFile(input);
                using var data = BarCompression.EnsureDecompressedPooled(raw, out _);
                var xml = ConversionHelper.ConvertXmbToXmlBytes(data.Span);
                if (xml == null) { OutputHelper.Error("Failed to convert XMB to XML."); return false; }
                File.WriteAllBytes(output, xml);
                return true;
            }, stripExtension: true));

        convertCommand.Add(Build("xml-to-xmb", "Convert XML to XMB", "Path to XML file",
            (input, output) => !string.IsNullOrEmpty(output) ? Path.GetFullPath(output) : input + ".xmb",
            (input, output) =>
            {
                var xmlDoc = new XmlDocument();
                xmlDoc.LoadXml(File.ReadAllText(input));
                var xmb = BarFormatConverter.XMLtoXMB(xmlDoc);
                using var f = File.Create(output);
                f.Write(xmb.Span);
                return true;
            }));

        convertCommand.Add(BuildAsync("ddt-to-png", "Convert DDT texture to PNG", "Path to .ddt file", ".png",
            async (input, output) =>
            {
                using var raw = await PooledBuffer.FromFile(input);
                using var data = BarCompression.EnsureDecompressedPooled(raw, out _);
                var png = await ConversionHelper.ConvertDdtToPngBytes(data.Memory);
                if (png == null) { OutputHelper.Error("Failed to convert DDT to PNG."); return false; }
                await File.WriteAllBytesAsync(output, png);
                return true;
            }));

        convertCommand.Add(BuildAsync("ddt-to-tga", "Convert DDT texture to TGA", "Path to .ddt file", ".tga",
            async (input, output) =>
            {
                using var raw = await PooledBuffer.FromFile(input);
                using var data = BarCompression.EnsureDecompressedPooled(raw, out _);
                var tga = await ConversionHelper.ConvertDdtToTgaBytes(data.Memory);
                if (tga == null) { OutputHelper.Error("Failed to convert DDT to TGA."); return false; }
                await File.WriteAllBytesAsync(output, tga);
                return true;
            }));

        // DDS in this game is never L33t/Alz4-wrapped, so the compression check is skipped.
        convertCommand.Add(BuildAsync("dds-to-png", "Convert DDS texture to PNG", "Path to .dds file", ".png",
            async (input, output) =>
            {
                var data = await File.ReadAllBytesAsync(input);
                var png = await ConversionHelper.ConvertDdsToPngBytes(data);
                if (png == null) { OutputHelper.Error("Failed to convert DDS to PNG."); return false; }
                await File.WriteAllBytesAsync(output, png);
                return true;
            }));

        convertCommand.Add(CreatePngToDds());

        convertCommand.Add(CreatePngToDdt());

        convertCommand.Add(BuildAsync("tmm-to-obj", "Convert TMM model to OBJ", "Path to .tmm file", ".obj",
            async (input, output) =>
            {
                var pair = await LoadTmmPairAsync(input);
                if (pair is null) return false;
                using var tmmBuf = pair.Value.tmm;
                using var dataBuf = pair.Value.data;
                var obj = ConversionHelper.ConvertTmmToObjBytes(tmmBuf.Memory, dataBuf.Memory);
                if (obj == null) { OutputHelper.Error("Failed to convert TMM to OBJ."); return false; }
                File.WriteAllBytes(output, obj);
                return true;
            }));

        convertCommand.Add(BuildAsync("tmm-to-glb", "Convert TMM model to GLB (binary glTF)", "Path to .tmm file", ".glb",
            async (input, output) =>
            {
                var pair = await LoadTmmPairAsync(input);
                if (pair is null) return false;
                using var tmmBuf = pair.Value.tmm;
                using var dataBuf = pair.Value.data;
                var glb = ConversionHelper.ConvertTmmToGlbBytes(tmmBuf.Memory, dataBuf.Memory);
                if (glb == null) { OutputHelper.Error("Failed to convert TMM to GLB."); return false; }
                File.WriteAllBytes(output, glb);
                return true;
            }));

        convertCommand.Add(CreateGlbToTmm());

        convertCommand.Add(BuildAsync("scenario-to-xml", "Convert .mythscn scenario to XML", "Path to .mythscn file", ".xml",
            async (input, output) =>
            {
                using var raw = await PooledBuffer.FromFile(input);
                using var data = BarCompression.EnsureDecompressedPooled(raw, out _);
                var scenario = new ScenarioFile(data.Memory);
                if (!scenario.Parsed) { OutputHelper.Error("Failed to parse scenario file."); return false; }
                File.WriteAllText(output, scenario.ToXml(), Encoding.UTF8);
                return true;
            }));

        convertCommand.Add(Build("xml-to-scenario", "Convert XML to .mythscn scenario", "Path to XML file", ".mythscn",
            (input, output) =>
            {
                var scenario = ScenarioFile.FromXml(File.ReadAllText(input));
                if (!scenario.Parsed) { OutputHelper.Error("Failed to parse scenario XML."); return false; }
                var compressed = BarCompression.CompressL33t(scenario.ToBytes());
                using var f = File.Create(output);
                f.Write(compressed.Span);
                return true;
            }));

        convertCommand.Add(BuildAsync("trg-to-xml", "Convert .trg trigger file to XML", "Path to .trg file", ".xml",
            async (input, output) =>
            {
                using var raw = await PooledBuffer.FromFile(input);
                using var data = BarCompression.EnsureDecompressedPooled(raw, out _);
                var trg = new TriggerFile(data.Memory);
                if (!trg.Parsed) { OutputHelper.Error("Failed to parse trigger file."); return false; }
                File.WriteAllText(output, trg.ToXml(), Encoding.UTF8);
                return true;
            }));

        convertCommand.Add(Build("xml-to-trg", "Convert XML to .trg trigger file", "Path to XML file", ".trg",
            (input, output) =>
            {
                var trg = TriggerFile.FromXml(File.ReadAllText(input));
                if (!trg.Parsed) { OutputHelper.Error("Failed to parse trigger XML."); return false; }
                File.WriteAllBytes(output, trg.ToBytes());
                return true;
            }));

        convertCommand.Add(BuildWithLossless("trg-to-xs", "Convert .trg trigger file to XS trigger script", "Path to .trg file",
            async (input, output, lossless) =>
            {
                using var raw = await PooledBuffer.FromFile(input);
                using var data = BarCompression.EnsureDecompressedPooled(raw, out _);

                var trg = new TriggerFile(data.Memory);
                if (!trg.Parsed) { OutputHelper.Error("Failed to parse trigger file."); return false; }

                var xs = ScenarioFile.ConvertTriggersXmlToXs(trg.ToXml(), lossless);
                await File.WriteAllTextAsync(output, xs);
                return true;
            }));

        convertCommand.Add(Build("xs-to-trg", "Convert XS trigger script to .trg trigger file", "Path to .xs file", ".trg",
            (input, output) =>
            {
                var xs = File.ReadAllText(input);
                var xml = ScenarioFile.ParseXsToTriggersXml(xs, LoadTriggerDataFromRoot(), Path.GetDirectoryName(input));
                var trg = TriggerFile.FromXml(xml);
                if (!trg.Parsed) { OutputHelper.Error("Failed to convert XS to TRG."); return false; }
                File.WriteAllBytes(output, trg.ToBytes());
                return true;
            }));

        convertCommand.Add(Build("xs-to-xml", "Convert XS trigger script to triggers XML", "Path to .xs file", ".xml",
            (input, output) =>
            {
                var xs = File.ReadAllText(input);
                var xml = ScenarioFile.ParseXsToTriggersXml(xs, LoadTriggerDataFromRoot(), Path.GetDirectoryName(input));
                File.WriteAllText(output, xml, Encoding.UTF8);
                return true;
            }));

        convertCommand.Add(BuildWithLossless("xml-to-xs", "Convert triggers XML to XS trigger script", "Path to triggers XML file",
            (input, output, lossless) =>
            {
                var xml = File.ReadAllText(input);
                var xs = ScenarioFile.ConvertTriggersXmlToXs(xml, lossless);
                File.WriteAllText(output, xs);
                return Task.FromResult(true);
            }));

        convertCommand.Add(CreateXsToRm());

        return convertCommand;
    }

    #region Builder

    static Command BuildAsync(
        string name, string description, string inputDesc,
        Func<string, string?, string> resolveOutput,
        Func<string, string, Task<bool>> convert)
    {
        var inputArg = new Argument<FileInfo>("input") { Description = inputDesc };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output file path" };

        var cmd = new Command(name, description) { inputArg, outputOption };

        cmd.SetAction(async parseResult =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var inputFile = parseResult.GetValue(inputArg);
            if (inputFile == null || !inputFile.Exists)
            {
                OutputHelper.Error($"Input file not found: {inputFile?.FullName ?? "(null)"}");
                return 1;
            }

            var inputPath = inputFile.FullName;
            var output = parseResult.GetValue(outputOption);
            var outputPath = resolveOutput(inputPath, output);

            OutputHelper.EnsureDir(outputPath);

            try
            {
                if (!await convert(inputPath, outputPath))
                    return 1;
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Conversion failed: {ex.Message}");
                return 1;
            }

            ReportSuccess(inputPath, outputPath);
            return 0;
        });

        return cmd;
    }

    static Command BuildAsync(string name, string description, string inputDesc, string outputExt,
        Func<string, string, Task<bool>> convert, bool stripExtension = false) =>
        BuildAsync(name, description, inputDesc,
            (input, output) => ResolveOutputPath(input, output, outputExt, stripExtension), convert);

    static Command Build(string name, string description, string inputDesc,
        Func<string, string?, string> resolveOutput, Func<string, string, bool> convert) =>
        BuildAsync(name, description, inputDesc, resolveOutput,
            (input, output) => Task.FromResult(convert(input, output)));

    static Command Build(string name, string description, string inputDesc, string outputExt,
        Func<string, string, bool> convert, bool stripExtension = false) =>
        Build(name, description, inputDesc,
            (input, output) => ResolveOutputPath(input, output, outputExt, stripExtension), convert);

    #endregion

    static string ResolveOutputPath(string inputPath, string? explicitOutput, string newExtension, bool stripExtension = false)
    {
        if (!string.IsNullOrEmpty(explicitOutput))
            return Path.GetFullPath(explicitOutput);

        if (stripExtension)
        {
            var dir = Path.GetDirectoryName(inputPath) ?? ".";
            var nameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
            if (string.IsNullOrEmpty(Path.GetExtension(nameWithoutExt)))
                nameWithoutExt += newExtension;
            return Path.Combine(dir, nameWithoutExt);
        }

        return Path.ChangeExtension(inputPath, newExtension);
    }

    static void ReportSuccess(string inputPath, string outputPath)
    {
        OutputHelper.Success($"{Markup.Escape(Path.GetFileName(inputPath))} -> {Markup.Escape(outputPath)}");
    }

    static async Task<(PooledBuffer tmm, PooledBuffer data)?> LoadTmmPairAsync(string inputPath)
    {
        var dataPath = inputPath + ".data";
        if (!File.Exists(dataPath))
        {
            OutputHelper.Error($"Companion file not found: {dataPath}");
            return null;
        }

        using var rawTmm = await PooledBuffer.FromFile(inputPath);
        var tmmBytes = BarCompression.EnsureDecompressedPooled(rawTmm, out _);

        PooledBuffer tmmDataBytes;
        try
        {
            using var rawData = await PooledBuffer.FromFile(dataPath);
            tmmDataBytes = BarCompression.EnsureDecompressedPooled(rawData, out _);
        }
        catch
        {
            tmmBytes.Dispose();
            throw;
        }

        return (tmmBytes, tmmDataBytes);
    }

    static Command CreatePngToDds()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Path to image file (.png/.tga/.jpg/.bmp)" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output .dds path (default: same name with .dds extension)" };
        var formatOption = new Option<string>("--format") { Description = "BCn format: bc1 | bc3 | bc7 (default bc7)", DefaultValueFactory = _ => "bc7" };
        var mipsOption = new Option<string>("--mipmaps") { Description = "auto | N (default auto = full chain)", DefaultValueFactory = _ => "auto" };
        var srgbOption = new Option<bool>("--srgb") { Description = "Tag output as sRGB (DX10 header)" };

        var cmd = new Command("png-to-dds", "Convert image to DDS texture") { inputArg, outputOption, formatOption, mipsOption, srgbOption };

        cmd.SetAction(async parseResult =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var inputFile = parseResult.GetValue(inputArg);
            if (inputFile == null || !inputFile.Exists)
            {
                OutputHelper.Error($"Input file not found: {inputFile?.FullName ?? "(null)"}");
                return 1;
            }

            var inputPath = inputFile.FullName;
            var output = parseResult.GetValue(outputOption);
            var outputPath = ResolveOutputPath(inputPath, output, ".dds");

            var format = (parseResult.GetValue(formatOption) ?? "bc7").ToLowerInvariant() switch
            {
                "bc1" => CompressionFormat.Bc1,
                "bc3" => CompressionFormat.Bc3,
                _ => CompressionFormat.Bc7
            };

            var mipsStr = parseResult.GetValue(mipsOption) ?? "auto";
            byte mipmaps;
            if (mipsStr.Equals("auto", StringComparison.OrdinalIgnoreCase)) mipmaps = 0;
            else if (!byte.TryParse(mipsStr, out mipmaps)) mipmaps = 0;

            var srgb = parseResult.GetValue(srgbOption);

            OutputHelper.EnsureDir(outputPath);

            try
            {
                using var image = Image.Load<Rgba32>(inputPath);
                var bytes = await ConversionHelper.EncodeImageToDdsBytes(image, format, srgb, mipmaps);
                await File.WriteAllBytesAsync(outputPath, bytes);
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Conversion failed: {ex.Message}");
                return 1;
            }

            ReportSuccess(inputPath, outputPath);
            return 0;
        });

        return cmd;
    }

    static TriggerDataIndex? LoadTriggerDataFromRoot()
    {
        var root = CliConfig.GetRoot();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
        return TriggerDataIndex.Load(root);
    }

    // Like BuildAsync but with a --lossless flag and a fixed .xs output extension.
    static Command BuildWithLossless(string name, string description, string inputDesc,
        Func<string, string, bool, Task<bool>> convert)
    {
        var inputArg = new Argument<FileInfo>("input") { Description = inputDesc };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output file path" };
        var losslessOption = new Option<bool>("--lossless") { Description = "Embed round-trip metadata in the XS output (lossless re-import)" };

        var cmd = new Command(name, description) { inputArg, outputOption, losslessOption };

        cmd.SetAction(async parseResult =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var inputFile = parseResult.GetValue(inputArg);
            if (inputFile == null || !inputFile.Exists)
            {
                OutputHelper.Error($"Input file not found: {inputFile?.FullName ?? "(null)"}");
                return 1;
            }

            var inputPath = inputFile.FullName;
            var outputPath = ResolveOutputPath(inputPath, parseResult.GetValue(outputOption), ".xs");
            var lossless = parseResult.GetValue(losslessOption);

            OutputHelper.EnsureDir(outputPath);

            try
            {
                if (!await convert(inputPath, outputPath, lossless))
                    return 1;
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Conversion failed: {ex.Message}");
                return 1;
            }

            ReportSuccess(inputPath, outputPath);
            return 0;
        });

        return cmd;
    }

    static Command CreatePngToDdt()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Path to image file (.png/.tga/.jpg/.bmp)" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output .ddt path (default: same name with .ddt extension)" };
        var versionOption = new Option<string>("--version") { Description = "DDT version", DefaultValueFactory = _ => "rts4" };
        var usageOption = new Option<string>("--usage") { Description = "Usage flags (comma-separated)", DefaultValueFactory = _ => "none" };
        var alphaOption = new Option<string>("--alpha") { Description = "Alpha flags (comma-separated)", DefaultValueFactory = _ => "none" };
        var formatOption = new Option<string>("--format") { Description = "Encoded texture format", DefaultValueFactory = _ => "dxt1" };
        var mipsOption = new Option<string>("--mipmaps") { Description = "auto = full chain, or explicit level count N", DefaultValueFactory = _ => "auto" };
        var tableOption = new Option<string?>("--color-table") { Description = "Copy the RTS4 color table from an existing .ddt file (or a raw table dump)" };

        AddDdtCompletions(versionOption, usageOption, alphaOption, formatOption);

        var cmd = new Command("png-to-ddt", "Convert image to DDT texture")
            { inputArg, outputOption, versionOption, usageOption, alphaOption, formatOption, mipsOption, tableOption };

        cmd.SetAction(async parseResult =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var inputFile = parseResult.GetValue(inputArg);
            if (inputFile == null || !inputFile.Exists)
            {
                OutputHelper.Error($"Input file not found: {inputFile?.FullName ?? "(null)"}");
                return 1;
            }

            var inputPath = inputFile.FullName;
            var outputPath = ResolveOutputPath(inputPath, parseResult.GetValue(outputOption), ".ddt");

            var p = await TryBuildDdtParams(
                parseResult.GetValue(versionOption) ?? "rts4",
                parseResult.GetValue(usageOption) ?? "none",
                parseResult.GetValue(alphaOption) ?? "none",
                parseResult.GetValue(formatOption) ?? "dxt1",
                parseResult.GetValue(mipsOption) ?? "auto",
                parseResult.GetValue(tableOption));

            if (p == null) return 1;

            OutputHelper.EnsureDir(outputPath);

            try
            {
                using var image = Image.Load<Rgba32>(inputPath);
                var data = await DDTImage.EncodeImageToDDT(image, p.Version, p.Usage, p.Alpha, p.Format, p.MipLevels, p.ColorTable);
                await File.WriteAllBytesAsync(outputPath, data);
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Conversion failed: {ex.Message}");
                return 1;
            }

            ReportSuccess(inputPath, outputPath);
            return 0;
        });

        return cmd;
    }

    static Command CreateGlbToTmm()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Path to .glb file" };
        var outputOption = new Option<string?>("-o", "--output-dir") { Description = "Output directory (default: directory of the input GLB)" };
        var nameOption = new Option<string?>("--name") { Description = "Base name for .tmm / .tmm.data (default: GLB file name)" };
        var noDdtOption = new Option<bool>("--no-ddt") { Description = "Skip writing .ddt textures" };
        var noFbxOption = new Option<bool>("--no-fbximport") { Description = "Do not auto-link <anim>.fbximport files next to the GLB" };
        var versionOption = new Option<string?>("--ddt-version") { Description = "DDT version for materials without embedded params" };
        var usageOption = new Option<string?>("--ddt-usage") { Description = "DDT usage flags (comma-separated)" };
        var alphaOption = new Option<string?>("--ddt-alpha") { Description = "DDT alpha flags (comma-separated)" };
        var formatOption = new Option<string?>("--ddt-format") { Description = "DDT encoded texture format" };
        var mipsOption = new Option<string?>("--ddt-mipmaps") { Description = "auto = full chain, or explicit level count N" };
        var tableOption = new Option<string?>("--ddt-color-table") { Description = "Copy the RTS4 color table from an existing .ddt file (or a raw table dump)" };

        AddDdtCompletions(versionOption, usageOption, alphaOption, formatOption);

        var cmd = new Command("glb-to-tmm", "Convert GLB (binary glTF) to TMM/TMA/DDT")
        {
            inputArg, outputOption, nameOption, noDdtOption, noFbxOption,
            versionOption, usageOption, alphaOption, formatOption, mipsOption, tableOption
        };

        cmd.SetAction(async parseResult =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var inputFile = parseResult.GetValue(inputArg);
            if (inputFile == null || !inputFile.Exists)
            {
                OutputHelper.Error($"Input file not found: {inputFile?.FullName ?? "(null)"}");
                return 1;
            }

            var inputPath = inputFile.FullName;
            var inputDir = Path.GetDirectoryName(inputPath) ?? ".";
            var outputDir = parseResult.GetValue(outputOption) is { Length: > 0 } dir ? Path.GetFullPath(dir) : inputDir;
            var baseName = parseResult.GetValue(nameOption) is { Length: > 0 } customName
                ? customName
                : Path.GetFileNameWithoutExtension(inputPath);

            GlbModel model;
            try
            {
                using var glb = await PooledBuffer.FromFile(inputPath);
                model = GlbReader.Parse(glb.Memory);
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Failed to parse GLB: {ex.Message}");
                return 1;
            }

            var overrides = new Dictionary<string, GlbConverter.DdtMaterialParams>(StringComparer.Ordinal);
            var ddtArgs = new[]
            {
                parseResult.GetValue(versionOption),
                parseResult.GetValue(usageOption),
                parseResult.GetValue(alphaOption),
                parseResult.GetValue(formatOption),
                parseResult.GetValue(mipsOption),
                parseResult.GetValue(tableOption),
            };

            if (ddtArgs.Any(a => a is { Length: > 0 }))
            {
                var p = await TryBuildDdtParams(
                    ddtArgs[0] ?? "rts4", ddtArgs[1] ?? "none", ddtArgs[2] ?? "none",
                    ddtArgs[3] ?? "dxt1", ddtArgs[4] ?? "auto", ddtArgs[5]);

                if (p == null) return 1;

                foreach (var material in GlbConverter.Inspect(model, baseName).MaterialsNeedingDdtParams)
                    overrides[material] = p;
            }

            Dictionary<string, byte[]>? fbxByAnim = null;
            if (!parseResult.GetValue(noFbxOption) && model.Animations is { Length: > 0 })
            {
                foreach (var anim in model.Animations)
                {
                    var candidate = Path.Combine(inputDir, anim.Name + ".fbximport");
                    if (!File.Exists(candidate)) continue;

                    try
                    {
                        fbxByAnim ??= new Dictionary<string, byte[]>(StringComparer.Ordinal);
                        fbxByAnim[anim.Name] = await File.ReadAllBytesAsync(candidate);
                        if (OutputHelper.Verbose)
                            OutputHelper.Info($"Linked {Markup.Escape(Path.GetFileName(candidate))}");
                    }
                    catch (Exception ex)
                    {
                        OutputHelper.Warn($"Failed to read {Markup.Escape(candidate)}: {Markup.Escape(ex.Message)}");
                    }
                }
            }

            GlbConverter.ConversionResult result;
            try
            {
                var progress = OutputHelper.Verbose
                    ? new Progress<string>(msg => OutputHelper.Info(Markup.Escape(msg)))
                    : null;

                result = await GlbConverter.ConvertAsync(model, baseName, overrides, progress,
                    fbximportByAnimName: fbxByAnim, includeTextures: !parseResult.GetValue(noDdtOption));
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Conversion failed: {ex.Message}");
                return 1;
            }

            try
            {
                await GlbConverter.WriteFilesAsync(result.Files, outputDir);
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Failed to write output: {ex.Message}");
                return 1;
            }

            foreach (var warning in result.Warnings)
                OutputHelper.Warn(Markup.Escape(warning));

            foreach (var f in result.Files)
                ReportSuccess(inputPath, Path.Combine(outputDir, f.Name));

            return 0;
        });

        return cmd;
    }

    static async Task<GlbConverter.DdtMaterialParams?> TryBuildDdtParams(
        string versionStr, string usageStr, string alphaStr, string formatStr, string mipsStr, string? colorTablePath)
    {
        DDTVersion version;
        switch (versionStr.ToLowerInvariant())
        {
            case "rts3": version = DDTVersion.RTS3; break;
            case "rts4": version = DDTVersion.RTS4; break;
            default:
                OutputHelper.Error($"Unknown DDT version: {versionStr} (expected {string.Join(" | ", DdtVersions)})");
                return null;
        }

        DDTFormat format;
        switch (formatStr.ToLowerInvariant())
        {
            case "bgra": format = DDTFormat.Bgra; break;
            case "dxt1": format = DDTFormat.DXT1; break;
            case "dxt1a":
            case "dxt1alpha": format = DDTFormat.DXT1Alpha; break;
            case "grey":
            case "gray": format = DDTFormat.Grey; break;
            case "dxt3": format = DDTFormat.DXT3; break;
            case "dxt5": format = DDTFormat.DXT5; break;
            default:
                OutputHelper.Error($"Unknown DDT format: {formatStr} (expected {string.Join(" | ", DdtFormats)})");
                return null;
        }

        var usage = DDTUsage.None;
        foreach (var flag in SplitFlags(usageStr))
        {
            switch (flag)
            {
                case "none": break;
                case "alphatest": usage |= DDTUsage.AlphaTest; break;
                case "lowdetail": usage |= DDTUsage.LowDetail; break;
                case "bump": usage |= DDTUsage.Bump; break;
                case "cube": usage |= DDTUsage.Cube; break;
                default:
                    OutputHelper.Error($"Unknown DDT usage flag: {flag} (expected {string.Join(" | ", DdtUsages)})");
                    return null;
            }
        }

        var alpha = DDTAlpha.None;
        foreach (var flag in SplitFlags(alphaStr))
        {
            switch (flag)
            {
                case "none": break;
                case "player": alpha |= DDTAlpha.Player; break;
                case "transparent": alpha |= DDTAlpha.Transparent; break;
                case "blend": alpha |= DDTAlpha.Blend; break;
                default:
                    OutputHelper.Error($"Unknown DDT alpha flag: {flag} (expected {string.Join(" | ", DdtAlphas)})");
                    return null;
            }
        }

        byte mipmaps;
        if (mipsStr.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            mipmaps = 0;
        }
        else if (!byte.TryParse(mipsStr, out mipmaps))
        {
            OutputHelper.Error($"Invalid mipmap count: {mipsStr} (expected auto | N)");
            return null;
        }

        ReadOnlyMemory<byte>? colorTable = null;
        if (colorTablePath is { Length: > 0 })
        {
            if (version == DDTVersion.RTS3)
            {
                OutputHelper.Error("Color tables exist only in RTS4 DDT files; drop --color-table or use --version rts4.");
                return null;
            }

            colorTable = await LoadColorTable(colorTablePath);
            if (colorTable == null) return null;
        }

        return new GlbConverter.DdtMaterialParams(version, usage, alpha, format, mipmaps, colorTable);
    }

    /// A file that does not parse as a DDT is taken as a raw color table dump.
    static async Task<ReadOnlyMemory<byte>?> LoadColorTable(string path)
    {
        PooledBuffer data;
        try
        {
            using var raw = await PooledBuffer.FromFile(path);
            data = BarCompression.EnsureDecompressedPooled(raw, out _);
        }
        catch (Exception ex)
        {
            OutputHelper.Error($"Failed to read color table source: {ex.Message}");
            return null;
        }

        using (data)
        {
            var ddt = new DDTImage(data.Memory, copyData: false);
            if (!ddt.ParseHeader())
                return data.Memory.ToArray();

            if (ddt.ColorTable is not { Length: > 0 } table)
            {
                OutputHelper.Error($"Source DDT has no color table: {path}");
                return null;
            }

            return table.ToArray();
        }
    }

    static readonly string[] DdtVersions = ["rts3", "rts4"];
    static readonly string[] DdtFormats = ["bgra", "dxt1", "dxt1a", "grey", "dxt3", "dxt5"];
    static readonly string[] DdtUsages = ["none", "alphatest", "lowdetail", "bump", "cube"];
    static readonly string[] DdtAlphas = ["none", "player", "transparent", "blend"];

    static void AddDdtCompletions(Option version, Option usage, Option alpha, Option format)
    {
        version.CompletionSources.Add(DdtVersions);
        usage.CompletionSources.Add(DdtUsages);
        alpha.CompletionSources.Add(DdtAlphas);
        format.CompletionSources.Add(DdtFormats);
    }

    static IEnumerable<string> SplitFlags(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .Select(s => s.ToLowerInvariant());

    static Command CreateXsToRm()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Path to .xs file" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output file path (default: same name with _RMFriendly suffix)" };
        var classOption = new Option<string?>("--class") { Description = "Class name for RM wrapper (default: derived from filename)" };

        var cmd = new Command("xs-to-rm", "Convert XS script to RM-friendly format") { inputArg, outputOption, classOption };

        cmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var inputFile = parseResult.GetValue(inputArg);
            if (inputFile == null || !inputFile.Exists)
            {
                OutputHelper.Error($"Input file not found: {inputFile?.FullName ?? "(null)"}");
                return 1;
            }

            var inputPath = inputFile.FullName;
            var output = parseResult.GetValue(outputOption);
            var className = parseResult.GetValue(classOption);

            if (string.IsNullOrEmpty(className))
                className = XStoRM.GetSafeClassNameRgx().Replace(Path.GetFileNameWithoutExtension(inputPath), "");

            string outputPath;
            if (!string.IsNullOrEmpty(output))
            {
                outputPath = Path.GetFullPath(output);
            }
            else
            {
                var dir = Path.GetDirectoryName(inputPath) ?? ".";
                var name = Path.GetFileNameWithoutExtension(inputPath);
                var ext = Path.GetExtension(inputPath);
                outputPath = Path.Combine(dir, name + "_RMFriendly" + ext);
            }

            OutputHelper.EnsureDir(outputPath);

            var success = XStoRM.Convert(inputPath, outputPath, className);
            if (!success)
            {
                OutputHelper.Error("Failed to convert XS to RM format.");
                return 1;
            }

            ReportSuccess(inputPath, outputPath);
            return 0;
        });

        return cmd;
    }
}
