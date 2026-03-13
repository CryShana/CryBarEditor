using Avalonia.Platform.Storage;
using CryBar;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
    /// Cache of parsed soundset definition files, keyed by source file identifier.
    /// </summary>
    readonly Dictionary<string, List<SoundsetDefinition>> _cachedSoundsetFiles = new(StringComparer.OrdinalIgnoreCase);

    #region Soundset Resolution

    /// <summary>
    /// Resolves an FMOD event to its underlying soundset and individual sound files.
    /// Returns null if resolution fails at any step.
    /// </summary>
    SoundsetResolution? ResolveFmodEventSounds(FMODEvent fmodEvent)
    {
        if (_fileIndex == null) return null;

        var eventName = SoundsetParser.ExtractEventName(fmodEvent.Path);
        if (eventName == null) return null;

        var bankName = SoundsetParser.ExtractBankName(fmodEvent.Path);

        // Try bank-specific soundset file first, then all soundset files
        var (soundset, sourceFile) = FindSoundsetForEvent(eventName, bankName);
        if (soundset == null || sourceFile == null) return null;

        var resolution = new SoundsetResolution
        {
            SoundsetName = eventName,
            SourceFile = sourceFile,
            Soundset = soundset,
        };

        // Try to enrich with soundmanifest data
        var manifest = GetOrLoadSoundManifest();
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
    (SoundsetDefinition? soundset, string? sourceFile) FindSoundsetForEvent(string eventName, string? bankName)
    {
        if (_fileIndex == null) return (null, null);

        // Strategy 1: Try bank-specific soundset file
        if (bankName != null)
        {
            var bankSpecificName = $"soundsets_{bankName.ToLowerInvariant()}.soundset.XMB";
            var result = TryFindInSoundsetFile(bankSpecificName, eventName);
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

            var result = TryFindInSoundsetFile(fileName, eventName);
            if (result.soundset != null) return result;
        }

        return (null, null);
    }

    /// <summary>
    /// Tries to find a soundset by name in a specific soundset file.
    /// Uses caching to avoid re-parsing the same file.
    /// </summary>
    (SoundsetDefinition? soundset, string? sourceFile) TryFindInSoundsetFile(string fileName, string eventName)
    {
        if (_fileIndex == null) return (null, null);

        // Check cache first
        if (_cachedSoundsetFiles.TryGetValue(fileName, out var cached))
        {
            var found = SoundsetParser.FindSoundset(cached, eventName);
            if (found != null) return (found, fileName);
            return (null, null);
        }

        // Load and parse the file
        var entries = _fileIndex.Find(fileName);
        if (entries.Count == 0) return (null, null);

        var data = ReadFromIndexEntry(entries[0]);
        if (data == null) return (null, null);

        var decompressed = BarCompression.EnsureDecompressed(data.Value, out _);
        string? xmlText;

        if (fileName.EndsWith(".XMB", StringComparison.OrdinalIgnoreCase))
        {
            xmlText = ConversionHelper.ConvertXmbToXmlText(decompressed.Span);
        }
        else
        {
            xmlText = Encoding.UTF8.GetString(decompressed.Span);
        }

        if (xmlText == null) return (null, null);

        try
        {
            var definitions = SoundsetParser.ParseSoundsetXml(xmlText);
            _cachedSoundsetFiles[fileName] = definitions;

            var soundset = SoundsetParser.FindSoundset(definitions, eventName);
            if (soundset != null) return (soundset, fileName);
        }
        catch { /* parsing failed, skip */ }

        return (null, null);
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
            try
            {
                using var stream = File.OpenRead(manifestEntry.BarFilePath);
                var bar = new BarFile(stream);
                if (!bar.Load(out _) || bar.Entries == null) return result;

                foreach (var entry in bar.Entries)
                {
                    if (entry.Name.StartsWith("soundsets_", StringComparison.OrdinalIgnoreCase) &&
                        entry.Name.Contains(".soundset", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(entry.Name);
                    }
                }
            }
            catch { /* skip */ }
        }

        return result;
    }

    /// <summary>
    /// Gets or lazily loads the sound manifest from Sound.bar.
    /// </summary>
    Dictionary<string, SoundManifestEntry>? GetOrLoadSoundManifest()
    {
        if (_cachedSoundManifest != null) return _cachedSoundManifest;
        if (_fileIndex == null) return null;

        var entries = _fileIndex.Find("soundmanifest.xml.XMB");
        if (entries.Count == 0) return null;

        var data = ReadFromIndexEntry(entries[0]);
        if (data == null) return null;

        var decompressed = BarCompression.EnsureDecompressed(data.Value, out _);
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
        _cachedSoundsetFiles.Clear();
    }

    #endregion

    #region Sound Preview Text

    /// <summary>
    /// Builds the "Contained Sounds" section for FMOD event preview.
    /// </summary>
    string BuildSoundsetPreviewText(FMODEvent fmodEvent)
    {
        if (_fileIndex == null)
            return "\nContained Sounds:\n  (file index not available — load a root directory first)";

        try
        {
            var resolution = ResolveFmodEventSounds(fmodEvent);
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
        catch (Exception ex)
        {
            return $"\nContained Sounds:\n  (error resolving: {ex.Message})";
        }
    }

    #endregion

    #region Export All Sounds

    async void BankItem_ExportAllSounds(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedBankEntry == null || _fmodBank == null)
            return;

        var resolution = ResolveFmodEventSounds(SelectedBankEntry);
        if (resolution == null)
        {
            _ = ShowError("Could not resolve soundset for this event.\nMake sure a root directory with Sound.bar is loaded.");
            return;
        }

        var sounds = resolution.Soundset.Sounds;
        if (sounds.Count == 0)
        {
            _ = ShowError("Soundset has no sound files.");
            return;
        }

        // Pick a destination folder
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = $"Export {sounds.Count} sounds from \"{resolution.SoundsetName}\"",
            AllowMultiple = false
        });

        if (folders.Count == 0) return;
        var outputDir = folders[0].Path.LocalPath;

        var progress = new Progress<string?>();
        IProgress<string?> p = progress;
        _ = ShowProgress($"Exporting {sounds.Count} sounds", progress);

        var targetCount = sounds.Count;
        var maxAttempts = targetCount * 20;
        var selectedEntry = SelectedBankEntry;

        bank_play_csc?.Cancel();
        bank_play_csc = new();
        var token = bank_play_csc.Token;

        // Sort soundset sounds by manifest duration for matching later
        var sortedSoundset = sounds
            .OrderBy(s => s.Length ?? double.MaxValue)
            .ToList();

        await Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                // Export via NRT multiple times, deduplicate by file size (proxy for duration) after trimming.
                var uniqueSounds = new Dictionary<long, byte[]>(); // key = trimmed file size

                for (int attempt = 0; attempt < maxAttempts && uniqueSounds.Count < targetCount; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    p.Report($"Attempt {attempt + 1}/{maxAttempts} — found {uniqueSounds.Count}/{targetCount} unique sounds...");

                    var tempPath = Path.Combine(Path.GetTempPath(), $"crybar_fmod_{Guid.NewGuid()}.wav");
                    try
                    {
                        selectedEntry.Export(tempPath, token);
                        if (!File.Exists(tempPath)) continue;

                        FMODEvent.TrimSilence(tempPath);

                        var fileData = File.ReadAllBytes(tempPath);

                        if (!uniqueSounds.ContainsKey(fileData.Length))
                            uniqueSounds[fileData.Length] = fileData;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* single attempt failed, continue */ }
                    finally
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                    }
                }

                // Match exported sounds to original filenames via duration sorting.
                // Trimmed durations differ from manifest durations in absolute value,
                // but their relative ordering is preserved — so sort both sides and match positionally.
                var sortedExports = uniqueSounds.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();

                int exported = 0;
                for (int i = 0; i < sortedExports.Count; i++)
                {
                    string outName;
                    if (i < sortedSoundset.Count)
                    {
                        outName = Path.GetFileName(sortedSoundset[i].Filename);
                    }
                    else
                    {
                        var eventName = SoundsetParser.ExtractEventName(selectedEntry.Path) ?? "sound";
                        outName = $"{eventName}_{i + 1}.wav";
                    }

                    var outPath = Path.Combine(outputDir, outName);
                    p.Report($"Writing {outName}...");
                    File.WriteAllBytes(outPath, sortedExports[i]);
                    exported++;
                }

                sw.Stop();
                var msg = $"Exported {exported}/{targetCount} unique sounds in {sw.Elapsed.TotalSeconds:0.00}s";
                if (uniqueSounds.Count < targetCount)
                    msg += $"\nNote: Found {uniqueSounds.Count} unique variants out of {targetCount} expected.";
                p.Report(msg);
            }
            catch (OperationCanceledException)
            {
                p.Report("Export cancelled.");
            }
            catch (Exception ex)
            {
                p.Report($"Export failed: {ex.Message}");
            }
            finally
            {
                p.Report(null);
            }
        }, token);
    }

    #endregion
}
