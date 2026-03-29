using System.CommandLine;
using System.Text;
using System.Xml;

using CryBar.Bar;
using CryBar.Cli.Helpers;
using CryBar.Scenario;
using CryBar.Utilities;

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
