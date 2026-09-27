using Avalonia.Platform.Storage;
using CryBar.Bar;
using CryBar.Indexing;
using CryBar.Sound;
using CryBarEditor.Classes;
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
    /// <summary>
    /// Cached sound manifest (parsed from soundmanifest.xml.XMB in Sound.bar).
    /// Invalidated when the file index is rebuilt.
    /// </summary>
    Dictionary<string, SoundManifestEntry>? _cachedSoundManifest;

    /// <summary>
    /// Combined stem -> relative path index built from soundmanifest + all soundset files.
    /// Used to recover file paths for FMOD subsounds/events. Invalidated with the file index.
    /// </summary>
    SoundPathIndex? _cachedSoundPathIndex;

    /// <summary>
    /// Root path of the Sound.bar that holds the manifest (e.g. "game\sound"), prefixed onto
    /// resolved relative paths so exported subsounds mirror the BAR-entry directory layout.
    /// </summary>
    string? _soundRootPath;

    /// <summary>
    /// Cache of parsed soundset definition files, keyed by source file identifier.
    /// Access serialized by <see cref="_cachedSoundsetFilesLock"/> -- TryFindInSoundsetFileAsync
    /// and RebuildSoundsetIndexAsync can run concurrently (sound preview + dependency window),
    /// each reading and writing this map across await boundaries.
    /// </summary>
    readonly Dictionary<string, List<SoundsetDefinition>> _cachedSoundsetFiles = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock _cachedSoundsetFilesLock = new();

    #region Soundset Resolution

    /// <summary>
    /// Resolves an FMOD event to its underlying soundset and individual sound files.
    /// Returns null if resolution fails at any step.
    /// </summary>
    async ValueTask<SoundsetResolution?> ResolveFmodEventSoundsAsync(FMODEvent fmodEvent)
    {
        if (_fileIndex == null) return null;

        var eventName = SoundsetParser.ExtractEventName(fmodEvent.Path);
        if (eventName == null) return null;

        var bankName = SoundsetParser.ExtractBankName(fmodEvent.Path);

        // Try bank-specific soundset file first, then all soundset files
        var (soundset, sourceFile) = await FindSoundsetForEventAsync(eventName, bankName);
        if (soundset == null || sourceFile == null) return null;

        var resolution = new SoundsetResolution
        {
            SoundsetName = eventName,
            SourceFile = sourceFile,
            Soundset = soundset,
        };

        // Try to enrich with soundmanifest data
        var manifest = await GetOrLoadSoundManifestAsync();
        if (manifest != null)
        {
            resolution.HasManifestData = SoundsetParser.EnrichWithManifest(soundset, manifest);
        }

        return resolution;
    }

    /// <summary>
    /// Searches soundset files for a matching soundset name.
    /// Tries bank-specific file first (soundsets_[bankname].soundset.XMB),
    /// then iterates all soundsets_* files in the index.
    /// </summary>
    async ValueTask<(SoundsetDefinition? soundset, string? sourceFile)> FindSoundsetForEventAsync(string eventName, string? bankName)
    {
        if (_fileIndex == null) return (null, null);

        // Strategy 1: Try bank-specific soundset file
        if (bankName != null)
        {
            var bankSpecificName = $"soundsets_{bankName.ToLowerInvariant()}.soundset.XMB";
            var result = await TryFindInSoundsetFileAsync(bankSpecificName, eventName);
            if (result.soundset != null) return result;
        }

        // Strategy 2: Search all soundsets_* files via the file index
        // Find all indexed files that match the soundsets_*.soundset pattern
        var soundsetFiles = FindSoundsetFileNames();
        foreach (var fileName in soundsetFiles)
        {
            // Skip the bank-specific one we already tried
            if (bankName != null && fileName.Contains(bankName, StringComparison.OrdinalIgnoreCase))
                continue;

            var result = await TryFindInSoundsetFileAsync(fileName, eventName);
            if (result.soundset != null) return result;
        }

        return (null, null);
    }

    /// <summary>
    /// Tries to find a soundset by name in a specific soundset file.
    /// Uses caching to avoid re-parsing the same file.
    /// </summary>
    async ValueTask<(SoundsetDefinition? soundset, string? sourceFile)> TryFindInSoundsetFileAsync(string fileName, string eventName)
    {
        var definitions = await GetOrParseSoundsetDefinitionsAsync(fileName);
        if (definitions == null) return (null, null);

        var soundset = SoundsetParser.FindSoundset(definitions, eventName);
        return soundset != null ? (soundset, fileName) : (null, null);
    }

    /// <summary>
    /// Finds all soundset file names in the file index matching soundsets_*.soundset* pattern.
    /// </summary>
    List<string> FindSoundsetFileNames()
    {
        if (_fileIndex == null) return [];

        // We can't enumerate the file index directly, so we use known patterns.
        // Search for files that match "soundsets_" prefix via the index.
        // The FileIndex supports filename lookups, but not prefix searches.
        // We'll search for the soundmanifest first to find the Sound.bar,
        // then enumerate its entries for soundset files.
        var result = new List<string>();

        var manifestEntries = _fileIndex.Find("soundmanifest.xml.XMB");
        if (manifestEntries.Count == 0) return result;

        // The Sound.bar that contains the manifest also contains soundset files
        var manifestEntry = manifestEntries[0];
        if (manifestEntry.Source == FileIndexSource.BarEntry && manifestEntry.BarFilePath != null)
        {
            var cached = GetOrLoadBar(manifestEntry.BarFilePath);
            if (cached?.Bar.Entries == null) return result;

            foreach (var entry in cached.Bar.Entries)
            {
                if (entry.Name.StartsWith("soundsets_", StringComparison.OrdinalIgnoreCase) &&
                    entry.Name.Contains(".soundset", StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(entry.Name);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets or lazily loads the sound manifest from Sound.bar.
    /// </summary>
    async ValueTask<Dictionary<string, SoundManifestEntry>?> GetOrLoadSoundManifestAsync()
    {
        if (_cachedSoundManifest != null) return _cachedSoundManifest;
        if (_fileIndex == null) return null;

        var entries = _fileIndex.Find("soundmanifest.xml.XMB");
        if (entries.Count == 0) return null;

        using var data = await ReadFromIndexEntryPooledAsync(entries[0]);
        if (data == null) return null;

        using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
        var xmlText = ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
        if (xmlText == null) return null;

        try
        {
            _cachedSoundManifest = SoundsetParser.ParseSoundManifest(xmlText);
            return _cachedSoundManifest;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Clears sound-related caches. Call when the file index is rebuilt.
    /// </summary>
    void ClearSoundCaches()
    {
        _cachedSoundManifest = null;
        _cachedSoundPathIndex = null;
        _soundRootPath = null;
        _pathsResolvedForBank = null; // re-resolve subsound paths against the rebuilt index
        lock (_cachedSoundsetFilesLock)
            _cachedSoundsetFiles.Clear();
    }

    #endregion

    #region Sound Path Index

    /// <summary>
    /// Builds (once) and returns the combined sound path index from soundmanifest + every
    /// soundsets_*.soundset file, and captures <see cref="_soundRootPath"/> from Sound.bar.
    /// Returns null if no file index is loaded.
    /// </summary>
    async ValueTask<SoundPathIndex?> GetOrLoadSoundPathIndexAsync()
    {
        if (_cachedSoundPathIndex != null) return _cachedSoundPathIndex;
        if (_fileIndex == null) return null;

        // Capture the sound root from the Sound.bar that holds the manifest (mirrors how
        // GetBARFullRelativePath prefixes BarFile.RootPath onto entry relative paths).
        var manifestEntries = _fileIndex.Find("soundmanifest.xml.XMB");
        if (manifestEntries.Count > 0)
        {
            var me = manifestEntries[0];
            if (me.Source == FileIndexSource.BarEntry && me.BarFilePath != null)
                _soundRootPath = GetOrLoadBar(me.BarFilePath)?.Bar.RootPath;
        }

        var manifest = await GetOrLoadSoundManifestAsync();

        // Parse every soundset file and collect all definitions.
        var allDefs = new List<SoundsetDefinition>();
        foreach (var fileName in FindSoundsetFileNames())
        {
            var defs = await GetOrParseSoundsetDefinitionsAsync(fileName);
            if (defs != null) allDefs.AddRange(defs);
        }

        _cachedSoundPathIndex = SoundPathIndex.BuildFrom(manifest?.Values, allDefs);
        return _cachedSoundPathIndex;
    }

    /// <summary>
    /// Parses a soundset file (cache-aware) and returns all its definitions, or null on failure.
    /// Shares the <see cref="_cachedSoundsetFiles"/> cache with the soundset resolver.
    /// </summary>
    async ValueTask<List<SoundsetDefinition>?> GetOrParseSoundsetDefinitionsAsync(string fileName)
    {
        if (_fileIndex == null) return null;

        List<SoundsetDefinition>? cached;
        lock (_cachedSoundsetFilesLock)
            _cachedSoundsetFiles.TryGetValue(fileName, out cached);
        if (cached != null) return cached;

        var entries = _fileIndex.Find(fileName);
        if (entries.Count == 0) return null;

        using var data = await ReadFromIndexEntryPooledAsync(entries[0]);
        if (data == null) return null;

        using var decompressed = BarCompression.EnsureDecompressedPooled(data, out _);
        var xmlText = ConversionHelper.GetTextContent(decompressed.Span, fileName);

        try
        {
            var definitions = SoundsetParser.ParseSoundsetXml(xmlText);
            lock (_cachedSoundsetFilesLock)
                _cachedSoundsetFiles[fileName] = definitions;
            return definitions;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The bank whose subsounds currently carry resolved paths (skips redundant passes).</summary>
    FMODBank? _pathsResolvedForBank;

    /// <summary>Cached Name -> subsound lookup, rebuilt only when the loaded bank changes.</summary>
    Dictionary<string, FMODSubsound>? _subsoundByName;
    FMODBank? _subsoundByNameForBank;

    /// <summary>
    /// Drops per-bank caches. Must be called when the loaded bank is disposed - the caches hold
    /// FMODSubsound references that pin the (potentially huge) bank byte array alive.
    /// </summary>
    void ResetBankItemCaches()
    {
        _pathsResolvedForBank = null;
        _subsoundByName = null;
        _subsoundByNameForBank = null;
    }

    /// <summary>
    /// Resolves and assigns file paths to all subsounds of the loaded bank by matching their
    /// names against the sound path index. Best-effort: items with no match keep null paths.
    /// Idempotent per bank; cleared when the sound index is rebuilt (see <see cref="ClearSoundCaches"/>).
    /// </summary>
    async Task ResolveBankItemPathsAsync()
    {
        var bank = _fmodBank;
        if (bank == null || ReferenceEquals(_pathsResolvedForBank, bank)) return;

        var index = await GetOrLoadSoundPathIndexAsync();
        if (index == null) return; // no file index yet - retry on a later call

        var root = _soundRootPath ?? "";
        foreach (var sub in bank.Subsounds)
        {
            // ResolveBest disambiguates same-named clips by matching the subsound's duration
            // against the manifest's per-path length (samples differ - the FSB5 is re-encoded).
            var all = index.ResolveAll(sub.Name);
            var best = index.ResolveBest(sub.Name, sub.LengthMs);
            sub.ResolvedPaths = all;
            sub.ResolvedPath = best;
            sub.FullRelativePath = best != null ? Path.Combine(root, best) : null;
        }
        _pathsResolvedForBank = bank;
    }

    /// <summary>
    /// Resolves an FMOD event to the actual subsounds in the SAME bank that back its soundset
    /// variants. Returns the matched, de-duplicated subsounds (each with its resolved path).
    /// Empty when the event has no soundset or none of its variants are present as subsounds.
    /// </summary>
    async ValueTask<List<FMODSubsound>> ResolveEventSubsoundsAsync(FMODEvent ev)
    {
        if (_fmodBank == null) return [];

        var resolution = await ResolveFmodEventSoundsAsync(ev);
        if (resolution == null) return [];

        // Ensure subsound paths are resolved (for naming/export downstream).
        await ResolveBankItemPathsAsync();

        return MatchEventSubsounds(resolution);
    }

    /// <summary>
    /// Matches a resolved soundset's variant filenames to subsounds in the loaded bank (by name).
    /// Returns the matched, de-duplicated subsounds in soundset order.
    /// </summary>
    List<FMODSubsound> MatchEventSubsounds(SoundsetResolution resolution)
    {
        var result = new List<FMODSubsound>();
        var bank = _fmodBank;
        if (bank == null) return result;

        if (!ReferenceEquals(_subsoundByNameForBank, bank))
        {
            var map = new Dictionary<string, FMODSubsound>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in bank.Subsounds) map.TryAdd(s.Name, s);
            _subsoundByName = map;
            _subsoundByNameForBank = bank;
        }

        var seen = new HashSet<FMODSubsound>();
        foreach (var sound in resolution.Soundset.Sounds)
        {
            if (_subsoundByName!.TryGetValue(SoundPathIndex.GetStem(sound.Filename), out var sub) && seen.Add(sub))
                result.Add(sub);
        }

        return result;
    }

    #endregion

    #region Sound Preview Text

    /// <summary>
    /// Formats the "Contained Sounds" section for FMOD event preview from an already-resolved
    /// soundset (resolution == null means no match / no file index).
    /// </summary>
    string BuildSoundsetPreviewText(FMODEvent fmodEvent, SoundsetResolution? resolution)
    {
        if (_fileIndex == null)
            return "\nContained Sounds:\n  (file index not available - load a root directory first)";

        if (resolution == null)
        {
            var eventName = SoundsetParser.ExtractEventName(fmodEvent.Path);
            return $"\nContained Sounds:\n  (no matching soundset found for \"{eventName ?? fmodEvent.Path}\")";
        }

        var sb = new StringBuilder();
        sb.AppendLine();
        sb.Append($"Contained Sounds: (from {resolution.SourceFile})");
        if (resolution.HasManifestData)
            sb.Append(" [enriched with soundmanifest]");
        sb.AppendLine();

        var soundset = resolution.Soundset;
        sb.Append($"  Soundset: {soundset.Name}");
        if (soundset.Volume.HasValue)
            sb.Append($", Volume: {soundset.Volume.Value:F4}");
        if (soundset.MaxNum.HasValue)
            sb.Append($", MaxNum: {soundset.MaxNum.Value}");
        sb.AppendLine();
        sb.AppendLine($"  Sound files ({soundset.Sounds.Count}):");

        foreach (var sound in soundset.Sounds)
        {
            sb.Append($"    - {sound.Filename}");
            if (sound.Volume.HasValue)
                sb.Append($"  [vol: {sound.Volume.Value:F4}]");
            if (sound.Weight.HasValue)
                sb.Append($"  [weight: {sound.Weight.Value:F4}]");
            if (sound.Length.HasValue)
                sb.Append($"  [{sound.Length.Value:F3}s]");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    #endregion

    #region Event Variant Rendering (fallback)

    /// <summary>Soundset sound filenames sorted by manifest duration (variant match order).</summary>
    static List<string> SortedVariantFilenames(IEnumerable<SoundsetSound> sounds) => sounds
        .OrderBy(s => s.Length ?? double.MaxValue)
        .Select(s => Path.GetFileName(s.Filename))
        .ToList();

    /// <summary>
    /// Renders every distinct soundset variant of one event into <paramref name="outputDir"/> (best-effort).
    /// A variant is picked at random per play, so we render repeatedly and dedup by trimmed size, then map
    /// size-sorted renders to the length-sorted soundset filenames. Returns the number of variants written.
    /// Falls back to <c>{fallbackPrefix}-{n}.wav</c> names when fewer renders than names were found. Throws when every attempt failed.
    /// Runs synchronously (blocking FMOD work) - call from a background task.
    /// </summary>
    static int ExportEventVariants(FMODEvent ev, IReadOnlyList<string> sortedVariantNames,
        string outputDir, IProgress<string?> p, CancellationToken token, IProgress<double>? progress = null,
        string? fallbackPrefix = null)
    {
        var targetCount = sortedVariantNames.Count;
        var maxAttempts = targetCount * 20;

        int failedAttempts = 0;
        Exception? lastError = null;

        var uniqueSounds = new Dictionary<long, byte[]>(); // key = trimmed file size (duration proxy)
        for (int attempt = 0; attempt < maxAttempts && uniqueSounds.Count < targetCount; attempt++)
        {
            token.ThrowIfCancellationRequested();
            p.Report($"{ev.DisplayName}: attempt {attempt + 1}/{maxAttempts} - {uniqueSounds.Count}/{targetCount} variants...");

            var tempPath = Path.Combine(Path.GetTempPath(), $"crybar_fmod_{Guid.NewGuid()}.wav");
            try
            {
                ev.Export(tempPath, token, (uint)(attempt + 1));
                if (!File.Exists(tempPath)) continue;

                FMODEvent.TrimSilence(tempPath);

                var fileData = File.ReadAllBytes(tempPath);
                if (!uniqueSounds.ContainsKey(fileData.Length))
                {
                    uniqueSounds[fileData.Length] = fileData;
                    progress?.Report((double)uniqueSounds.Count / targetCount);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failedAttempts++;
                lastError = ex;
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }

        if (uniqueSounds.Count == 0 && lastError != null)
            throw new InvalidOperationException($"{ev.DisplayName}: all {failedAttempts} render attempt(s) failed: {lastError.Message}", lastError);

        if (failedAttempts > 0)
            p.Report($"{ev.DisplayName}: {failedAttempts} render attempt(s) failed (last: {lastError!.Message})");

        // Trimmed durations differ from manifest durations in absolute value, but their relative
        // ordering is preserved - so sort both sides and match positionally.
        var sortedExports = uniqueSounds.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

        bool namesMatch = sortedExports.Count == targetCount;
        if (!namesMatch)
            p.Report($"{ev.DisplayName}: found {sortedExports.Count}/{targetCount} distinct variants, using generic names");

        var genericPrefix = fallbackPrefix ?? SoundsetParser.ExtractEventName(ev.Path) ?? "sound";

        int exported = 0;
        for (int i = 0; i < sortedExports.Count; i++)
        {
            string outName = namesMatch
                ? sortedVariantNames[i]
                : $"{genericPrefix}-{i + 1}.wav";

            var outPath = Path.Combine(outputDir, outName);
            p.Report($"Writing {outName}...");
            File.WriteAllBytes(outPath, sortedExports[i]);
            exported++;
        }

        return exported;
    }

    #endregion
}
