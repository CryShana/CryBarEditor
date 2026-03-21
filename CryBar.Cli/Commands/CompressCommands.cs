using System.CommandLine;

using CryBar.Bar;
using CryBar.Cli.Helpers;

using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class CompressCommands
{
    public static (Command compress, Command decompress) Create()
    {
        var compressCommand = new Command("compress", "Compress files");
        compressCommand.Add(CreateAlz4());
        compressCommand.Add(CreateL33t());

        var decompressCommand = CreateDecompress();

        return (compressCommand, decompressCommand);
    }

    static string ResolveOutputPath(string inputPath, string? explicitOutput)
    {
        if (!string.IsNullOrEmpty(explicitOutput))
            return Path.GetFullPath(explicitOutput);

        // Default: overwrite input
        return inputPath;
    }

    /// <summary>
    /// Writes data to the output path safely. When overwriting the input file,
    /// writes to a temp file first then renames, to prevent data loss on failure.
    /// </summary>
    static void SafeWrite(string outputPath, string inputPath, ReadOnlySpan<byte> data)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (string.Equals(Path.GetFullPath(outputPath), Path.GetFullPath(inputPath), StringComparison.OrdinalIgnoreCase))
        {
            // Overwriting input: write to temp file first, then rename
            var tempPath = outputPath + ".tmp";
            try
            {
                using (var f = File.Create(tempPath))
                    f.Write(data);
                File.Move(tempPath, outputPath, overwrite: true);
            }
            catch
            {
                // Clean up temp file on failure
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
        else
        {
            using var f = File.Create(outputPath);
            f.Write(data);
        }
    }

    static Command CreateAlz4() =>
        CreateCompressCommand("alz4", "Compress using ALZ4 (LZ4)", data => BarCompression.CompressAlz4(data));

    static Command CreateL33t() =>
        CreateCompressCommand("l33t", "Compress using L33t (zlib + CRC32)", data => BarCompression.CompressL33t(data));

    delegate Memory<byte> CompressFunc(Span<byte> data);

    static Command CreateCompressCommand(string name, string description, CompressFunc compressor)
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Path to file to compress" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output file path (default: overwrite input)" };

        var cmd = new Command(name, description) { inputArg, outputOption };

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
            var outputPath = ResolveOutputPath(inputPath, output);

            var rawBytes = File.ReadAllBytes(inputPath);
            var originalSize = rawBytes.Length;

            Memory<byte> compressed;
            try
            {
                compressed = compressor(rawBytes);
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Compression failed: {ex.Message}");
                return 1;
            }

            var compressedSize = compressed.Length;
            SafeWrite(outputPath, inputPath, compressed.Span);

            var ratio = originalSize > 0 ? (double)compressedSize / originalSize * 100 : 0;
            OutputHelper.Success(
                $"{Markup.Escape(Path.GetFileName(inputPath))}: {OutputHelper.FormatBytes(originalSize)} -> {OutputHelper.FormatBytes(compressedSize)} ({ratio:F1}%)");

            return 0;
        });

        return cmd;
    }

    static Command CreateDecompress()
    {
        var inputArg = new Argument<FileInfo>("input") { Description = "Path to compressed file" };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output file path (default: overwrite input)" };

        var cmd = new Command("decompress", "Auto-detect and decompress files") { inputArg, outputOption };

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
            var outputPath = ResolveOutputPath(inputPath, output);

            var rawBytes = File.ReadAllBytes(inputPath);
            var originalSize = rawBytes.Length;

            Memory<byte> decompressed;
            CompressionType type;
            try
            {
                decompressed = BarCompression.EnsureDecompressed(rawBytes, out type);
            }
            catch (Exception ex)
            {
                OutputHelper.Error($"Decompression failed: {ex.Message}");
                return 1;
            }

            if (type == CompressionType.None)
            {
                if (!string.Equals(outputPath, inputPath, StringComparison.OrdinalIgnoreCase))
                {
                    SafeWrite(outputPath, inputPath, decompressed.Span);
                    OutputHelper.Info($"File is not compressed - copied to {Markup.Escape(outputPath)}");
                }
                else
                {
                    OutputHelper.Info("File is not compressed.");
                }
                return 0;
            }

            var decompressedSize = decompressed.Length;

            SafeWrite(outputPath, inputPath, decompressed.Span);

            OutputHelper.Success(
                $"{Markup.Escape(Path.GetFileName(inputPath))}: {type} detected, {OutputHelper.FormatBytes(originalSize)} -> {OutputHelper.FormatBytes(decompressedSize)}");

            return 0;
        });

        return cmd;
    }
}
