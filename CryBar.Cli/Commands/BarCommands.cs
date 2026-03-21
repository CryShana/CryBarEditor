using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;

using CryBar.Bar;
using CryBar.Cli.Helpers;
using CryBar.Cli.Models;

using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class BarCommands
{
    public static Command Create()
    {
        var barCommand = new Command("bar", "BAR archive operations");

        barCommand.Add(CreateListCommand());
        barCommand.Add(CreateInfoCommand());
        barCommand.Add(CreateExportCommand());

        return barCommand;
    }

    static Command CreateListCommand()
    {
        var archiveArg = new Argument<FileInfo>("archive") { Description = "Path to .bar archive" };
        var filterOption = new Option<string?>("--filter") { Description = "Glob pattern to filter entries (e.g. \"*.ddt\", \"art/**/*.xmb\")" };

        var cmd = new Command("list", "List entries in a BAR archive") { archiveArg, filterOption };

        cmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var archiveFile = parseResult.GetValue(archiveArg);
            if (archiveFile == null || !archiveFile.Exists)
            {
                OutputHelper.Error($"Archive not found: {archiveFile?.FullName ?? "(null)"}");
                return 1;
            }

            var filter = parseResult.GetValue(filterOption);
            Regex? filterRegex = null;
            if (!string.IsNullOrEmpty(filter))
                filterRegex = OutputHelper.GlobToRegex(filter);

            using var stream = File.OpenRead(archiveFile.FullName);
            var bar = new BarFile(stream);
            if (!bar.Load(out var error))
            {
                OutputHelper.Error($"Failed to load BAR archive: {error}");
                return 1;
            }

            var entries = bar.Entries;
            if (entries == null || entries.Count == 0)
            {
                OutputHelper.Info("Archive contains no entries.");
                return 0;
            }

            var filtered = new List<BarFileEntry>();
            foreach (var entry in entries)
            {
                var normalizedPath = entry.RelativePath.Replace("\\", "/");
                if (filterRegex != null && !filterRegex.IsMatch(normalizedPath))
                    continue;
                filtered.Add(entry);
            }

            if (filtered.Count == 0)
            {
                OutputHelper.Info("No entries match the filter.");
                return 0;
            }

            var compressions = new CompressionType[filtered.Count];
            for (int i = 0; i < filtered.Count; i++)
                compressions[i] = DetectCompression(stream, filtered[i]);

            if (OutputHelper.Json)
            {
                var results = new BarListResult[filtered.Count];
                for (int i = 0; i < filtered.Count; i++)
                {
                    var entry = filtered[i];
                    results[i] = new BarListResult
                    {
                        Path = entry.RelativePath.Replace("\\", "/"),
                        Size = entry.SizeUncompressed,
                        Compression = compressions[i].ToString(),
                        Convert = GetConvertLabel(entry.RelativePath)
                    };
                }

                var json = JsonSerializer.Serialize(results, CliJsonContext.Default.BarListResultArray);
                Console.WriteLine(json);
                return 0;
            }

            var table = new Table();
            table.AddColumn("Entry");
            table.AddColumn(new TableColumn("Size").RightAligned());
            table.AddColumn("Compression");
            table.AddColumn("Convert");

            for (int i = 0; i < filtered.Count; i++)
            {
                var entry = filtered[i];
                var convertLabel = GetConvertLabel(entry.RelativePath);
                var formattedPath = OutputHelper.FormatPath(entry.RelativePath.Replace("\\", "/"));

                var compressionMarkup = compressions[i] == CompressionType.None ? "[grey]-[/]" : compressions[i].ToString();

                table.AddRow(
                    new Markup(formattedPath),
                    new Text(OutputHelper.FormatBytes(entry.SizeUncompressed)),
                    new Markup(compressionMarkup),
                    new Text(convertLabel)
                );
            }

            AnsiConsole.Write(table);

            if (!OutputHelper.Quiet)
                OutputHelper.Info($"{filtered.Count} entries{(filterRegex != null ? $" (filtered from {entries.Count})" : "")}");

            return 0;
        });

        return cmd;
    }

    static Command CreateInfoCommand()
    {
        var archiveArg = new Argument<FileInfo>("archive") { Description = "Path to .bar archive" };

        var cmd = new Command("info", "Show archive summary and statistics") { archiveArg };

        cmd.SetAction((parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var archiveFile = parseResult.GetValue(archiveArg);
            if (archiveFile == null || !archiveFile.Exists)
            {
                OutputHelper.Error($"Archive not found: {archiveFile?.FullName ?? "(null)"}");
                return 1;
            }

            using var stream = File.OpenRead(archiveFile.FullName);
            var bar = new BarFile(stream);
            if (!bar.Load(out var error))
            {
                OutputHelper.Error($"Failed to load BAR archive: {error}");
                return 1;
            }

            var entries = bar.Entries;
            if (entries == null)
            {
                OutputHelper.Info("Archive contains no entries.");
                return 0;
            }

            long totalSize = 0;
            long compressedSize = 0;
            var compressionCounts = new Dictionary<CompressionType, int>();
            var extensionCounts = new Dictionary<string, int>();

            foreach (var entry in entries)
            {
                totalSize += entry.SizeUncompressed;
                compressedSize += entry.SizeInArchive;

                var compression = DetectCompression(stream, entry);

                compressionCounts.TryGetValue(compression, out var cc);
                compressionCounts[compression] = cc + 1;

                var ext = Path.GetExtension(entry.RelativePath).ToLowerInvariant();
                if (string.IsNullOrEmpty(ext)) ext = "(none)";
                extensionCounts.TryGetValue(ext, out var ec);
                extensionCounts[ext] = ec + 1;
            }

            if (OutputHelper.Json)
            {
                var result = new BarInfoResult
                {
                    Archive = archiveFile.FullName,
                    Version = (int)bar.Version,
                    EntryCount = entries.Count,
                    TotalSize = totalSize,
                    CompressedSize = compressedSize,
                    CompressionBreakdown = compressionCounts.ToDictionary(x => x.Key.ToString(), x => x.Value),
                    ExtensionBreakdown = extensionCounts
                };
                var json = JsonSerializer.Serialize(result, CliJsonContext.Default.BarInfoResult);
                Console.WriteLine(json);
                return 0;
            }

            // Render as formatted output
            AnsiConsole.MarkupLine($"[bold]Archive:[/] {Markup.Escape(archiveFile.FullName)}");
            AnsiConsole.MarkupLine($"[bold]Version:[/] {bar.Version}");
            AnsiConsole.MarkupLine($"[bold]Entries:[/] {entries.Count}");
            AnsiConsole.MarkupLine($"[bold]Total size:[/] {OutputHelper.FormatBytes(totalSize)} (uncompressed)");
            AnsiConsole.MarkupLine($"[bold]Archive size:[/] {OutputHelper.FormatBytes(compressedSize)} (in archive)");
            AnsiConsole.WriteLine();

            // Compression breakdown table
            var compTable = new Table().Title("Compression");
            compTable.AddColumn("Type");
            compTable.AddColumn(new TableColumn("Count").RightAligned());
            foreach (var kvp in compressionCounts.OrderByDescending(x => x.Value))
                compTable.AddRow(kvp.Key.ToString(), kvp.Value.ToString());
            AnsiConsole.Write(compTable);

            // Extension breakdown table
            var extTable = new Table().Title("File Types");
            extTable.AddColumn("Extension");
            extTable.AddColumn(new TableColumn("Count").RightAligned());
            foreach (var kvp in extensionCounts.OrderByDescending(x => x.Value))
                extTable.AddRow(kvp.Key, kvp.Value.ToString());
            AnsiConsole.Write(extTable);

            return 0;
        });

        return cmd;
    }

    static Command CreateExportCommand()
    {
        var archiveArg = new Argument<FileInfo>("archive") { Description = "Path to .bar archive" };
        var entriesArg = new Argument<string[]>("entries") { Description = "Entry names to export (optional if --all or --filter used)", Arity = ArgumentArity.ZeroOrMore };
        var outputOption = new Option<string?>("-o", "--output") { Description = "Output file or directory path" };
        var filterOption = new Option<string?>("--filter") { Description = "Glob pattern to filter entries" };
        var allOption = new Option<bool>("--all") { Description = "Export all entries" };
        var decompressOption = new Option<bool>("--decompress") { Description = "Decompress entries before writing" };
        var convertOption = new Option<bool>("--convert") { Description = "Convert formats (XMB->XML, DDT->TGA, etc.)" };
        var flatOption = new Option<bool>("--flat") { Description = "Flatten directory structure (all files in one directory)" };

        var cmd = new Command("export", "Export entries from a BAR archive")
        {
            archiveArg, entriesArg, outputOption, filterOption, allOption,
            decompressOption, convertOption, flatOption
        };

        cmd.SetAction(async (parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var archiveFile = parseResult.GetValue(archiveArg);
            if (archiveFile == null || !archiveFile.Exists)
            {
                OutputHelper.Error($"Archive not found: {archiveFile?.FullName ?? "(null)"}");
                return 1;
            }

            var entryNames = parseResult.GetValue(entriesArg) ?? [];
            var output = parseResult.GetValue(outputOption);
            var filter = parseResult.GetValue(filterOption);
            var all = parseResult.GetValue(allOption);
            var decompress = parseResult.GetValue(decompressOption);
            var convert = parseResult.GetValue(convertOption);
            var flat = parseResult.GetValue(flatOption);

            if (entryNames.Length == 0 && string.IsNullOrEmpty(filter) && !all)
            {
                OutputHelper.Error("Specify entry names, --filter, or --all to select entries for export.");
                return 1;
            }

            using var stream = File.OpenRead(archiveFile.FullName);
            var bar = new BarFile(stream);
            if (!bar.Load(out var error))
            {
                OutputHelper.Error($"Failed to load BAR archive: {error}");
                return 1;
            }

            var entries = bar.Entries;
            if (entries == null || entries.Count == 0)
            {
                OutputHelper.Info("Archive contains no entries.");
                return 0;
            }

            // Select entries to export
            var toExport = new List<BarFileEntry>();

            if (all)
            {
                toExport.AddRange(entries);
            }
            else if (entryNames.Length > 0)
            {
                var nameSet = new HashSet<string>(entryNames, StringComparer.OrdinalIgnoreCase);
                foreach (var entry in entries)
                {
                    if (nameSet.Contains(entry.Name) || nameSet.Contains(entry.RelativePath.Replace("\\", "/")))
                        toExport.Add(entry);
                }

                if (toExport.Count == 0)
                {
                    OutputHelper.Error("No matching entries found in archive.");
                    return 1;
                }
            }

            // Apply glob filter (additive to entry names, or standalone)
            if (!string.IsNullOrEmpty(filter))
            {
                var filterRegex = OutputHelper.GlobToRegex(filter);
                if (all || entryNames.Length > 0)
                {
                    // Filter the already selected entries
                    toExport = toExport.Where(e => filterRegex.IsMatch(e.RelativePath.Replace("\\", "/"))).ToList();
                }
                else
                {
                    // Filter from all entries
                    foreach (var entry in entries)
                    {
                        if (filterRegex.IsMatch(entry.RelativePath.Replace("\\", "/")))
                            toExport.Add(entry);
                    }
                }
            }

            if (toExport.Count == 0)
            {
                OutputHelper.Info("No entries match the selection criteria.");
                return 0;
            }

            bool singleEntry = toExport.Count == 1;

            string outputPath;
            if (!string.IsNullOrEmpty(output))
            {
                outputPath = Path.GetFullPath(output);
            }
            else
            {
                outputPath = Directory.GetCurrentDirectory();
            }

            long totalBytes = 0;
            int exportedCount = 0;

            if (singleEntry && !string.IsNullOrEmpty(output) && !Directory.Exists(output))
            {
                // Single entry, -o is a file path
                var entry = toExport[0];
                var bytes = await ExportEntryData(stream, entry, decompress, convert);
                if (bytes == null)
                {
                    OutputHelper.Error($"Failed to convert: {entry.RelativePath}");
                    return 1;
                }

                OutputHelper.EnsureDir(outputPath);
                await File.WriteAllBytesAsync(outputPath, bytes);
                totalBytes = bytes.Length;
                exportedCount = 1;

                if (!OutputHelper.Quiet)
                    OutputHelper.Success($"Exported {Markup.Escape(entry.Name)} -> {Markup.Escape(outputPath)} ({OutputHelper.FormatBytes(totalBytes)})");
            }
            else
            {
                // Multiple entries or -o is a directory
                var outDir = outputPath;
                Directory.CreateDirectory(outDir);

                await AnsiConsole.Progress()
                    .AutoClear(true)
                    .HideCompleted(true)
                    .Columns(
                        new TaskDescriptionColumn(),
                        new ProgressBarColumn(),
                        new PercentageColumn(),
                        new SpinnerColumn()
                    )
                    .StartAsync(async ctx =>
                    {
                        var task = ctx.AddTask("Exporting entries", maxValue: toExport.Count);

                        foreach (var entry in toExport)
                        {
                            var bytes = await ExportEntryData(stream, entry, decompress, convert);
                            if (bytes == null)
                            {
                                OutputHelper.Warn($"Skipped (conversion failed): {entry.RelativePath}");
                                task.Increment(1);
                                continue;
                            }

                            var relativePath = entry.RelativePath.Replace("\\", "/");

                            // If converting, adjust the output filename extension
                            if (convert)
                                relativePath = GetConvertedPath(relativePath);

                            string filePath;
                            if (flat)
                            {
                                filePath = Path.Combine(outDir, Path.GetFileName(relativePath));
                            }
                            else
                            {
                                filePath = Path.Combine(outDir, relativePath);
                            }

                            OutputHelper.EnsureDir(filePath);
                            await File.WriteAllBytesAsync(filePath, bytes);
                            totalBytes += bytes.Length;
                            exportedCount++;

                            if (OutputHelper.Verbose)
                                OutputHelper.Success(Markup.Escape(relativePath));

                            task.Increment(1);
                        }
                    });

                if (!OutputHelper.Quiet)
                    OutputHelper.Success($"Exported {exportedCount} files ({OutputHelper.FormatBytes(totalBytes)})");
            }

            return 0;
        });

        return cmd;
    }

    static async Task<byte[]?> ExportEntryData(Stream stream, BarFileEntry entry, bool decompress, bool convert)
    {
        if (convert)
        {
            // For conversion, we need decompressed data
            var decompressed = entry.ReadDataDecompressed(stream);
            var ext = Path.GetExtension(entry.RelativePath).ToLowerInvariant();

            if (ext == ".xmb")
            {
                var xmlBytes = ConversionHelper.ConvertXmbToXmlBytes(decompressed.Span);
                return xmlBytes ?? decompressed.ToArray();
            }
            else if (ext == ".ddt")
            {
                var tgaBytes = await ConversionHelper.ConvertDdtToTgaBytes(decompressed);
                return tgaBytes;
            }
            // For other convertible extensions or non-convertible, return decompressed data
            return decompressed.ToArray();
        }

        if (decompress)
        {
            var decompressed = entry.ReadDataDecompressed(stream);
            return decompressed.ToArray();
        }

        return entry.ReadDataRaw(stream);
    }

    static string GetConvertedPath(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        if (ext == ".xmb")
        {
            // Remove .xmb extension to reveal underlying extension (e.g. foo.xml.xmb -> foo.xml)
            var withoutXmb = Path.GetFileNameWithoutExtension(relativePath);
            var dir = Path.GetDirectoryName(relativePath);
            return string.IsNullOrEmpty(dir) ? withoutXmb : Path.Combine(dir, withoutXmb);
        }

        var convertedExt = ConversionHelper.GetConvertedExtension(ext);
        if (convertedExt != null)
        {
            return Path.ChangeExtension(relativePath, convertedExt);
        }

        return relativePath;
    }

    static string GetConvertLabel(string relativePath)
    {
        var ext = Path.GetExtension(relativePath).ToLowerInvariant();
        if (ext == ".xmb")
            return "XML";

        var converted = ConversionHelper.GetConvertedExtension(ext);
        return converted?.TrimStart('.').ToUpperInvariant() ?? "-";
    }

    static CompressionType DetectCompression(Stream stream, BarFileEntry entry)
    {
        if (entry.SizeInArchive < 4) return CompressionType.None;
        Span<byte> header = stackalloc byte[4];
        stream.Seek(entry.ContentOffset, SeekOrigin.Begin);
        stream.ReadExactly(header);
        if (BarCompression.IsAlz4(header)) return CompressionType.Alz4;
        if (BarCompression.IsL33t(header)) return CompressionType.L33t;
        return CompressionType.None;
    }

}
