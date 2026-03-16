namespace CryBar;

/// <summary>
/// Discovers supplemental BAR files in parent directories and indexes their entries.
/// Used when the user selects a subdirectory (e.g., game\modelcache\) as their root,
/// so that materials/textures from sibling BAR archives are still resolvable.
/// </summary>
public static class FileIndexBuilder
{
    /// <summary>
    /// Walks up parent directories from <paramref name="rootDirectory"/>, scanning for .bar files.
    /// Returns paths to BAR files found outside the root directory.
    /// </summary>
    /// <param name="rootDirectory">The user's selected root directory.</param>
    /// <param name="maxDepth">Maximum number of parent levels to climb.</param>
    /// <param name="maxFiles">Maximum number of BAR file paths to return.</param>
    /// <param name="maxSubdirectories">Skip a parent level if it has more than this many child directories.</param>
    public static List<string> FindSupplementalBarFiles(
        string rootDirectory,
        int maxDepth = 2,
        int maxFiles = 50,
        int maxSubdirectories = 50)
    {
        var normalizedRoot = Path.GetFullPath(rootDirectory);
        if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
            normalizedRoot += Path.DirectorySeparatorChar;

        var parent = normalizedRoot.TrimEnd(Path.DirectorySeparatorChar);
        for (int level = 1; level <= maxDepth; level++)
        {
            var next = Path.GetDirectoryName(parent);
            if (next == null || next == parent)
                break;
            parent = next;

            // Skip drive roots
            if (Path.GetPathRoot(parent) == parent)
                break;

            // Check subdirectory count
            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(parent);
            }
            catch
            {
                continue;
            }

            if (subdirs.Length > maxSubdirectories)
                continue;

            var collected = new List<string>(maxFiles);

            // Collect *.bar in the parent directory itself (excluding files under rootDirectory)
            try
            {
                foreach (var barFile in Directory.GetFiles(parent, "*.bar"))
                {
                    if (!IsUnderRoot(barFile, normalizedRoot))
                        collected.Add(barFile);
                    if (collected.Count >= maxFiles)
                        return collected;
                }
            }
            catch { /* ignore enumeration errors */ }

            // Collect *.bar in each immediate subdirectory (excluding rootDirectory itself)
            foreach (var subdir in subdirs)
            {
                if (IsOrContainsRoot(subdir, normalizedRoot))
                    continue;

                try
                {
                    foreach (var barFile in Directory.GetFiles(subdir, "*.bar"))
                    {
                        collected.Add(barFile);
                        if (collected.Count >= maxFiles)
                            return collected;
                    }
                }
                catch { /* ignore enumeration errors */ }
            }

            // BAR-presence short-circuit: if BARs found at this level, return immediately
            if (collected.Count > 0)
                return collected;
        }

        return [];
    }

    /// <summary>
    /// Opens each BAR file, loads it, and adds all entries to the given index.
    /// Uses Parallel.ForEach for performance. Unreadable BAR files are silently skipped.
    /// </summary>
    public static void IndexBarFiles(FileIndex index, IReadOnlyList<string> barFilePaths)
    {
        Parallel.ForEach(barFilePaths, barFilePath =>
        {
            try
            {
                using var stream = File.OpenRead(barFilePath);
                var bar = new BarFile(stream);
                if (!bar.Load(out _)) return;

                var barRootPath = bar.RootPath;
                var entries = bar.Entries;
                if (entries == null) return;

                foreach (var entry in entries)
                {
                    var fullRelPath = string.IsNullOrEmpty(barRootPath)
                        ? entry.RelativePath
                        : Path.Combine(barRootPath, entry.RelativePath);

                    index.Add(new FileIndexEntry
                    {
                        FullRelativePath = fullRelPath,
                        FileName = entry.Name,
                        Source = FileIndexSource.BarEntry,
                        BarFilePath = barFilePath,
                        EntryRelativePath = entry.RelativePath,
                    });
                }
            }
            catch { /* skip unreadable BAR files */ }
        });
    }

    /// <summary>
    /// Checks if a path is under the root directory.
    /// normalizedRoot must end with a directory separator to avoid prefix false-positives
    /// (e.g., "C:\game" matching "C:\gameplay\foo.bar").
    /// </summary>
    static bool IsUnderRoot(string path, string normalizedRoot)
    {
        return path.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks if the directory IS the root or if the root is under this directory.
    /// normalizedRoot must end with a directory separator.
    /// </summary>
    static bool IsOrContainsRoot(string directory, string normalizedRoot)
    {
        var dirWithSep = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        return normalizedRoot.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
