using System.CommandLine;
using System.Text.Json;

using CryBar.Bar;
using CryBar.Cli.Config;
using CryBar.Cli.Helpers;
using CryBar.Cli.Models;
using CryBar.Dependencies;
using CryBar.Indexing;
using CryBar.Utilities;

using Spectre.Console;

namespace CryBar.Cli.Commands;

public static class DepsCommands
{
    public static Command Create()
    {
        var depsCommand = new Command("deps", "Dependency analysis (requires root dir set)");

        depsCommand.Add(CreateFindCommand());
        depsCommand.Add(CreateTreeCommand());

        return depsCommand;
    }

    static Command CreateFindCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Relative file path (to root directory) to analyze (e.g. \"art/units/greek/hoplite.xml\")" };

        var cmd = new Command("find", "List dependencies of a file") { fileArg };

        cmd.SetAction(async (parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var root = CliConfig.RequireRoot();
            if (root == null) return 1;

            var file = parseResult.GetValue(fileArg);
            if (string.IsNullOrEmpty(file))
            {
                OutputHelper.Error("File argument is required.");
                return 1;
            }

            // Build index
            var index = await BuildIndex(root);
            if (index == null) return 1;

            // Find the target file in the index
            var entries = index.Find(file);
            if (entries.Count == 0)
            {
                OutputHelper.Error($"File not found in index: {Markup.Escape(file)}");
                return 1;
            }

            var entry = entries[0];

            // Read file data
            using var fileData = await ReadFromIndexEntry(entry, root);
            if (fileData == null)
            {
                OutputHelper.Error($"Failed to read file data: {Markup.Escape(entry.FullRelativePath)}");
                return 1;
            }

            // Find dependencies
            var result = await DependencyFinder.FindDependenciesForFileAsync(
                entry.FullRelativePath,
                fileData,
                index);

            // Collect all unique dependency paths (resolved file paths only)
            var depPaths = CollectResolvedPaths(result);

            if (OutputHelper.Json)
            {
                var depsResult = new DepsResult
                {
                    File = entry.FullRelativePath.Replace("\\", "/"),
                    Dependencies = depPaths
                };
                var json = JsonSerializer.Serialize(depsResult, CliJsonContext.Default.DepsResult);
                Console.WriteLine(json);
                return 0;
            }

            if (depPaths.Length == 0)
            {
                OutputHelper.Info($"No dependencies found for {Markup.Escape(entry.FullRelativePath)}");
                return 0;
            }

            if (!OutputHelper.Quiet)
                OutputHelper.Info($"Dependencies of [bold]{Markup.Escape(entry.FullRelativePath)}[/]:");

            foreach (var dep in depPaths)
                AnsiConsole.MarkupLine($"  {OutputHelper.FormatPath(dep)}");

            if (!OutputHelper.Quiet)
                OutputHelper.Info($"{depPaths.Length} dependencies found.");

            return 0;
        });

        return cmd;
    }

    static Command CreateTreeCommand()
    {
        var fileArg = new Argument<string>("file") { Description = "Relative file path to analyze (e.g. \"art/units/greek/hoplite.xml\")" };

        var cmd = new Command("tree", "Show recursive dependency tree") { fileArg };

        cmd.SetAction(async (parseResult) =>
        {
            OutputHelper.ApplyGlobalOptions(parseResult);

            var root = CliConfig.RequireRoot();
            if (root == null) return 1;

            var file = parseResult.GetValue(fileArg);
            if (string.IsNullOrEmpty(file))
            {
                OutputHelper.Error("File argument is required.");
                return 1;
            }

            // Build index
            var index = await BuildIndex(root);
            if (index == null) return 1;

            // Find the target file in the index
            var entries = index.Find(file);
            if (entries.Count == 0)
            {
                OutputHelper.Error($"File not found in index: {Markup.Escape(file)}");
                return 1;
            }

            var entry = entries[0];

            using var barCache = new BarFileCache();

            if (OutputHelper.Json)
            {
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allDeps = new List<string>();
                await CollectTreeDepsRecursive(entry.FullRelativePath, entry, index, root, barCache, visited, allDeps, maxDepth: 10, currentDepth: 0);

                var depsResult = new DepsResult
                {
                    File = entry.FullRelativePath.Replace("\\", "/"),
                    Dependencies = allDeps.ToArray()
                };
                var json = JsonSerializer.Serialize(depsResult, CliJsonContext.Default.DepsResult);
                Console.WriteLine(json);
                return 0;
            }

            var tree = new Tree(Markup.Escape(entry.FullRelativePath));
            var visitedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            visitedSet.Add(entry.FullRelativePath.Replace("\\", "/"));

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Building dependency tree...", async _ =>
                {
                    await BuildTreeRecursive(tree, entry.FullRelativePath, entry, index, root, barCache, visitedSet, maxDepth: 10, currentDepth: 0);
                });

            AnsiConsole.Write(tree);
            return 0;
        });

        return cmd;
    }

    static async Task<FileIndex?> BuildIndex(string root)
    {
        FileIndex? index = null;

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Indexing files...", _ =>
            {
                index = new FileIndex();
                var barFiles = Directory.GetFiles(root, "*.bar", SearchOption.AllDirectories).ToList();
                var supplemental = FileIndexBuilder.FindSupplementalBarFiles(root);
                barFiles.AddRange(supplemental);
                FileIndexBuilder.IndexBarFiles(index, barFiles);
                return Task.CompletedTask;
            });

        if (index == null || index.Count == 0)
        {
            OutputHelper.Error("No files indexed. Is root set to a valid game directory?");
            return null;
        }

        if (OutputHelper.Verbose)
            OutputHelper.Info($"Indexed {index.Count} files.");

        return index;
    }

    /// <summary>
    /// Caches opened BAR files to avoid re-opening and re-parsing during recursive tree walks.
    /// </summary>
    sealed class BarFileCache : IDisposable
    {
        readonly Dictionary<string, (BarFile bar, FileStream stream)?> _cache = new(StringComparer.OrdinalIgnoreCase);

        public (BarFile bar, FileStream stream)? Get(string barFilePath)
        {
            if (_cache.TryGetValue(barFilePath, out var cached))
                return cached;

            var stream = File.OpenRead(barFilePath);
            var bar = new BarFile(stream);
            if (!bar.Load(out _))
            {
                stream.Dispose();
                _cache[barFilePath] = null;
                return null;
            }

            var entry = (bar, stream);
            _cache[barFilePath] = entry;
            return entry;
        }

        public void Dispose()
        {
            foreach (var kvp in _cache.Values)
                kvp?.stream.Dispose();
            _cache.Clear();
        }
    }

    static async ValueTask<PooledBuffer?> ReadFromIndexEntry(FileIndexEntry entry, string root, BarFileCache? cache = null, CancellationToken token = default)
    {
        if (entry.BarFilePath != null)
        {
            if (cache != null)
            {
                var cached = cache.Get(entry.BarFilePath);
                if (cached == null) return null;
                return await ReadFromBar(cached.Value.bar, cached.Value.stream, entry, token);
            }

            using var barStream = File.OpenRead(entry.BarFilePath);
            var barFile = new BarFile(barStream);
            if (!barFile.Load(out _)) return null;
            return await ReadFromBar(barFile, barStream, entry, token);
        }
        else
        {
            var diskPath = Path.Combine(root, entry.FullRelativePath);
            if (!File.Exists(diskPath)) return null;
            return await PooledBuffer.FromFile(diskPath, token);
        }
    }

    static async ValueTask<PooledBuffer?> ReadFromBar(BarFile barFile, Stream barStream, FileIndexEntry entry, CancellationToken token)
    {
        var barEntries = barFile.Entries;
        if (barEntries == null) return null;

        var barEntry = barEntries.FirstOrDefault(e =>
            e.RelativePath.Equals(entry.FullRelativePath.Replace("/", "\\"), StringComparison.OrdinalIgnoreCase));

        var entryRelPath = entry.EntryRelativePath;
        if (barEntry == null && entryRelPath.Length > 0)
        {
            var entryRelStr = entryRelPath.ToString().Replace("/", "\\");
            barEntry = barEntries.FirstOrDefault(e =>
                e.RelativePath.Equals(entryRelStr, StringComparison.OrdinalIgnoreCase));
        }

        if (barEntry == null) return null;
        return await barEntry.ReadDataRawPooledAsync(barStream, token);
    }

    static string[] CollectResolvedPaths(DependencyResult result)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in result.GetAllReferences())
        {
            if (reference.Type == DependencyRefType.FilePath)
            {
                foreach (var resolved in reference.Resolved)
                    paths.Add(resolved.FullRelativePath.Replace("\\", "/"));
            }
        }
        var sorted = paths.ToArray();
        Array.Sort(sorted, StringComparer.OrdinalIgnoreCase);
        return sorted;
    }

    static async Task BuildTreeRecursive(
        IHasTreeNodes parent,
        string entryPath,
        FileIndexEntry entry,
        FileIndex index,
        string root,
        BarFileCache barCache,
        HashSet<string> visited,
        int maxDepth,
        int currentDepth)
    {
        if (currentDepth >= maxDepth) return;

        using var fileData = await ReadFromIndexEntry(entry, root, barCache);
        if (fileData == null) return;

        var result = await DependencyFinder.FindDependenciesForFileAsync(
            entryPath,
            fileData,
            index);

        var depPaths = CollectResolvedPaths(result);

        foreach (var depPath in depPaths)
        {
            var normalizedPath = depPath.Replace("\\", "/");
            if (!visited.Add(normalizedPath))
            {
                parent.AddNode($"[grey]{Markup.Escape(depPath)} (already listed)[/]");
                continue;
            }

            var depEntries = index.Find(depPath);
            if (depEntries.Count == 0)
            {
                parent.AddNode(OutputHelper.FormatPath(depPath));
                continue;
            }

            var depEntry = depEntries[0];
            var childNode = parent.AddNode(OutputHelper.FormatPath(depPath));

            await BuildTreeRecursive(childNode, depEntry.FullRelativePath, depEntry, index, root, barCache, visited, maxDepth, currentDepth + 1);
        }
    }

    static async Task CollectTreeDepsRecursive(
        string entryPath,
        FileIndexEntry entry,
        FileIndex index,
        string root,
        BarFileCache barCache,
        HashSet<string> visited,
        List<string> allDeps,
        int maxDepth,
        int currentDepth)
    {
        if (currentDepth >= maxDepth) return;

        using var fileData = await ReadFromIndexEntry(entry, root, barCache);
        if (fileData == null) return;

        var result = await DependencyFinder.FindDependenciesForFileAsync(
            entryPath,
            fileData,
            index);

        var depPaths = CollectResolvedPaths(result);

        foreach (var depPath in depPaths)
        {
            var normalizedPath = depPath.Replace("\\", "/");
            if (!visited.Add(normalizedPath))
                continue;

            allDeps.Add(normalizedPath);

            var depEntries = index.Find(depPath);
            if (depEntries.Count == 0)
                continue;

            var depEntry = depEntries[0];
            await CollectTreeDepsRecursive(depEntry.FullRelativePath, depEntry, index, root, barCache, visited, allDeps, maxDepth, currentDepth + 1);
        }
    }
}
