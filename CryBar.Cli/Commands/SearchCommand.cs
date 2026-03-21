using System.CommandLine;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

using CryBar.Bar;
using CryBar.Cli.Config;
using CryBar.Cli.Helpers;
using CryBar.Cli.Models;

using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class SearchCommand
{
    const int MAX_FILE_SIZE = 100_000_000; // 100 MB
    const int LEFT_CONTEXT_SIZE = 15;
    const int RIGHT_CONTEXT_SIZE = 25;

    static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".tga", ".ddt", ".png", ".gif", ".jpx", ".webp",
        ".wav", ".mp3", ".wmv", ".opus", ".vorbis", ".ogg", ".m4a",
        ".mp4", ".mov", ".webm", ".avi", ".mkv",
        ".data", ".hkt", ".tma", ".tmm"
    };

    public static Command Create()
    {
        var queryArg = new Argument<string>("query") { Description = "Text or regex pattern to search for" };
        var barOption = new Option<FileInfo?>("--bar") { Description = "Search within a specific BAR archive (no root needed)" };
        var regexOption = new Option<bool>("--regex") { Description = "Interpret query as a regular expression" };
        var caseSensitiveOption = new Option<bool>("--case-sensitive") { Description = "Case-sensitive search (default is case-insensitive)" };
        var filesOnlyOption = new Option<bool>("--files-only") { Description = "Only match against file/entry names, skip content" };
        var excludeOption = new Option<string?>("--exclude") { Description = "Comma-separated glob patterns to exclude (e.g. \"*.ddt,*.wav\")" };

        var cmd = new Command("search", "Search across root/BARs")
        {
            queryArg, barOption, regexOption, caseSensitiveOption, filesOnlyOption, excludeOption
        };

        cmd.SetAction(async (parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var query = parseResult.GetValue(queryArg);
            if (string.IsNullOrEmpty(query))
            {
                OutputHelper.Error("Query argument is required.");
                return 1;
            }

            var barFile = parseResult.GetValue(barOption);
            var useRegex = parseResult.GetValue(regexOption);
            var caseSensitive = parseResult.GetValue(caseSensitiveOption);
            var filesOnly = parseResult.GetValue(filesOnlyOption);
            var exclude = parseResult.GetValue(excludeOption);

            // Build exclusion patterns
            Regex[]? excludePatterns = null;
            if (!string.IsNullOrWhiteSpace(exclude))
            {
                excludePatterns = exclude
                    .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => OutputHelper.GlobToRegex(p))
                    .ToArray();
            }

            // Build search function
            var comparer = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            Regex? searchRegex = null;

            if (useRegex)
            {
                var regexOptions = RegexOptions.None;
                if (!caseSensitive)
                    regexOptions |= RegexOptions.IgnoreCase;

                try
                {
                    searchRegex = new Regex(query, regexOptions, TimeSpan.FromMilliseconds(800));
                }
                catch (RegexParseException ex)
                {
                    OutputHelper.Error($"Invalid regex: {ex.Message}");
                    return 1;
                }
            }

            Func<string, int, (int index, int length)> searcher = searchRegex == null
                ? (text, startIndex) =>
                {
                    var i = text.IndexOf(query, startIndex, comparer);
                    return (i, query.Length);
                }
                : (text, startIndex) =>
                {
                    try
                    {
                        var m = searchRegex.Match(text, startIndex);
                        if (!m.Success) return (-1, 0);
                        return (m.Index, m.Length);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return (-1, 0);
                    }
                };

            // For JSON mode: collect all results then serialize
            // For interactive mode: stream results as they're found
            var results = new List<SearchResultJson>();
            int matchCount = 0;

            Action<SearchResultJson> onResult;
            if (OutputHelper.Json)
            {
                onResult = result => results.Add(result);
            }
            else
            {
                onResult = result =>
                {
                    matchCount++;
                    var pathDisplay = OutputHelper.FormatPath(result.Path);
                    var sourceDisplay = result.Source != null ? $" [grey]({Markup.Escape(Path.GetFileName(result.Source))})[/]" : "";
                    var contextDisplay = FormatContextMarkup(result.Context, result.Index, searcher);

                    AnsiConsole.MarkupLine($"    {pathDisplay}{sourceDisplay}");
                    if (!string.IsNullOrEmpty(contextDisplay))
                        AnsiConsole.MarkupLine($"      {contextDisplay}");
                };
            }

            if (barFile != null)
            {
                // Search within a specific BAR
                if (!barFile.Exists)
                {
                    OutputHelper.Error($"BAR archive not found: {barFile.FullName}");
                    return 1;
                }

                using var stream = File.OpenRead(barFile.FullName);
                var bar = new BarFile(stream);
                if (!bar.Load(out var error))
                {
                    OutputHelper.Error($"Failed to load BAR archive: {error}");
                    return 1;
                }

                if (bar.Entries != null)
                    SearchBarEntries(stream, bar, barFile.FullName, searcher, excludePatterns, filesOnly, onResult);
            }
            else
            {
                // Search all BAR files in root (parallel)
                var root = CliConfig.RequireRoot();
                if (root == null) return 1;

                var barFiles = Directory.GetFiles(root, "*.bar", SearchOption.AllDirectories);
                if (barFiles.Length == 0)
                {
                    OutputHelper.Info("No .bar files found in root directory.");
                    return 0;
                }

                var channel = Channel.CreateUnbounded<SearchResultJson>(
                    new UnboundedChannelOptions { SingleReader = true });

                // Single consumer ensures output is never interleaved
                var consumer = Task.Run(async () =>
                {
                    await foreach (var result in channel.Reader.ReadAllAsync())
                        onResult(result);
                });

                await Parallel.ForEachAsync(barFiles,
                    new ParallelOptions { MaxDegreeOfParallelism = 4 },
                    (barPath, ct) =>
                    {
                        try
                        {
                            using var stream = File.OpenRead(barPath);
                            var bar = new BarFile(stream);
                            if (!bar.Load(out _)) return ValueTask.CompletedTask;
                            if (bar.Entries == null) return ValueTask.CompletedTask;

                            SearchBarEntries(stream, bar, barPath, searcher, excludePatterns, filesOnly,
                                result => channel.Writer.TryWrite(result));
                        }
                        catch { }

                        return ValueTask.CompletedTask;
                    });

                channel.Writer.Complete();
                await consumer;
            }

            // Output
            if (OutputHelper.Json)
            {
                var json = JsonSerializer.Serialize(results.ToArray(), CliJsonContext.Default.SearchResultJsonArray);
                Console.WriteLine(json);
                return 0;
            }

            if (matchCount == 0)
            {
                OutputHelper.Info("No matches found.");
                return 0;
            }

            if (!OutputHelper.Quiet)
                OutputHelper.Info($"{matchCount} match{(matchCount == 1 ? "" : "es")} found.");

            return 0;
        });

        return cmd;
    }

    static void SearchBarEntries(
        Stream stream,
        BarFile bar,
        string barPath,
        Func<string, int, (int index, int length)> searcher,
        Regex[]? excludePatterns,
        bool filesOnly,
        Action<SearchResultJson> onResult)
    {
        if (bar.Entries == null) return;

        foreach (var entry in bar.Entries)
        {
            if (excludePatterns != null && excludePatterns.Any(r => r.IsMatch(entry.Name)))
                continue;

            var entryPath = entry.RelativePath.Replace("\\", "/");

            // Check filename match
            var (nameIndex, nameLength) = searcher(entryPath, 0);
            if (nameIndex >= 0)
            {
                var context = MakeContext(nameIndex, nameLength, entryPath);
                onResult(new SearchResultJson
                {
                    Path = entryPath,
                    Source = barPath,
                    Index = nameIndex,
                    Context = context,
                    InContent = false
                });
            }

            if (filesOnly) continue;

            // Check content match
            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (BinaryExtensions.Contains(ext)) continue;
            if (entry.SizeUncompressed > MAX_FILE_SIZE) continue;

            try
            {
                var decompressed = entry.ReadDataDecompressed(stream);
                if (decompressed.Length == 0) continue;

                string? text = null;
                if (ext == ".xmb")
                {
                    text = ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
                    if (text == null) continue;
                }
                else
                {
                    var isUnicode = DetectIfUnicode(decompressed.Span);
                    var encoding = isUnicode ? Encoding.Unicode : Encoding.UTF8;
                    text = encoding.GetString(decompressed.Span);
                }

                // Find all matches in content
                var startIndex = 0;
                while (true)
                {
                    var (foundIndex, matchLength) = searcher(text, startIndex);
                    if (foundIndex == -1) break;
                    startIndex = foundIndex + 1;

                    var context = MakeContext(foundIndex, matchLength, text);
                    onResult(new SearchResultJson
                    {
                        Path = entryPath,
                        Source = barPath,
                        Index = foundIndex,
                        Context = context,
                        InContent = true
                    });
                }
            }
            catch (Exception ex)
            {
                if (OutputHelper.Verbose)
                    OutputHelper.Warn($"Skipped entry {Markup.Escape(entryPath)}: {Markup.Escape(ex.Message)}");
            }
        }
    }

    static string MakeContext(int index, int matchLength, string text)
    {
        var matchEnd = index + matchLength;
        var from = Math.Max(0, index - LEFT_CONTEXT_SIZE);
        var to = Math.Min(text.Length, matchEnd + RIGHT_CONTEXT_SIZE);

        var left = MakeItSafe(text[from..index]);
        var mid = MakeItSafe(text[index..matchEnd]);
        var right = MakeItSafe(text[matchEnd..to]);

        return left + mid + right;
    }

    static string FormatContextMarkup(string context, int index, Func<string, int, (int index, int length)> searcher)
    {
        // Re-find the match within the context string to highlight it
        var (matchIndex, matchLength) = searcher(context, 0);
        if (matchIndex >= 0 && matchIndex + matchLength <= context.Length)
        {
            var left = Markup.Escape(context[..matchIndex]);
            var mid = Markup.Escape(context[matchIndex..(matchIndex + matchLength)]);
            var right = Markup.Escape(context[(matchIndex + matchLength)..]);
            return $"{left}[bold yellow]{mid}[/]{right}";
        }

        return Markup.Escape(context);
    }

    static string MakeItSafe(string text)
    {
        // Replace control characters and non-printable characters with spaces
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\n' || c == '\r' || (c < '\u0020' && c != '\t') || c > '\uFFFD')
                sb.Append(' ');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }

    static bool DetectIfUnicode(ReadOnlySpan<byte> data)
    {
        var length = Math.Min(data.Length, 1000);

        int emptyPair = 0;
        int nonemptyPair = 0;
        for (int i = 0; i < length - 1; i++)
        {
            byte b1 = data[i];
            byte b2 = data[i + 1];
            if ((b1 > 0 && b2 == 0) || (b2 > 0 && b1 == 0))
                emptyPair++;
            else
                nonemptyPair++;
        }

        return emptyPair > (length / 2) && emptyPair > nonemptyPair;
    }
}
