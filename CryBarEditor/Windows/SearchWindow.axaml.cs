using Avalonia.Controls;
using Avalonia.Threading;

using CryBar.Bar;

using CryBarEditor.Classes;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Runtime;
using System.Text.RegularExpressions;

using System.Buffers;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Diagnostics;
using Avalonia.Interactivity;

namespace CryBarEditor.Windows;

public partial class SearchWindow : SimpleWindow
{
    string _status = "";
    string _filterWarning = "";
    string _query = "";
    string _exclusionFilter = "";
    bool _isRegex = false;
    bool _isCaseSensitive = true;
    bool _isFilesOnly = false;
    bool _searching = false;
    CancellationTokenSource? _csc;

    public bool CurrentlySearching { get => _searching; set { _searching = value; OnSelfChanged(); } }
    public bool CanSearch => _query.Length > 2;

    public string Query { get => _query; set { _query = value; OnSelfChanged(); OnPropertyChanged(nameof(CanSearch)); } }
    public string Status { get => _status; set { _status = value; OnSelfChanged(); } }
    public string FilterWarning { get => _filterWarning; set { _filterWarning = value; OnSelfChanged(); } }
    public bool HasFilterWarning => !string.IsNullOrEmpty(_filterWarning);
    public string ExclusionFilter { get => _exclusionFilter; set { _exclusionFilter = value; _ = RebuildExclusionRegex(); OnSelfChanged(); } }
    public bool IsRegex { get => _isRegex; set { _isRegex = value; OnSelfChanged(); } }
    public bool IsCaseSensitive { get => _isCaseSensitive; set { _isCaseSensitive = value; OnSelfChanged(); } }
    public bool IsFilesOnly { get => _isFilesOnly; set { _isFilesOnly = value; OnSelfChanged(); } }
    public ObservableCollectionExtended<SearchResult> SearchResults { get; } = new();

    private Regex? _fileExclusionRegex;
    private bool _rebuildPending;
    private readonly SemaphoreSlim _rebuildSemaphore = new(1, 1);

    readonly string? _rootDirectory;
    readonly List<RootFileEntry>? _rootEntries;
    readonly Action<BarFile?, FileStream?>? _barFileLoadedHandler;

    MainWindow? _owner;

    public SearchWindow()
    {
        DataContext = this;
        InitializeComponent();
    }

    public SearchWindow(MainWindow owner) : this()
    {
        _owner = owner;
        _rootDirectory = owner.RootDirectory;
        if (Directory.Exists(_rootDirectory))
            _rootEntries = owner.RootFileEntries.ToList();

        // Warn if root files are filtered
        var filteredCount = _rootEntries?.Count ?? 0;
        var totalCount = owner.TotalRootFileCount;
        if (filteredCount < totalCount)
        {
            FilterWarning = $"Searching {filteredCount} of {totalCount} files (filter active). Reopen search to refresh.";
            OnPropertyChanged(nameof(HasFilterWarning));
        }

        OnBarFileChanged();
        _barFileLoadedHandler = (_, _) => OnBarFileChanged();
        owner.OnBarFileLoaded += _barFileLoadedHandler;

        ExclusionFilter = _owner._searchExclusionFilter;
        IsCaseSensitive = _owner._searchCaseSensitive;
        IsRegex = _owner._searchRegex;
        IsFilesOnly = _owner._searchFilesOnly;
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        txtQuery.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        EnsureSettingsSaved();
    }
    void OnBarFileChanged()
    {
        var count = (_owner?.BarFile != null ? 1 : 0) + (_rootEntries?.Count ?? 0);
        Title = $"Search in [{count}] captured files";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _csc?.Cancel();
        if (_owner != null && _barFileLoadedHandler != null)
            _owner.OnBarFileLoaded -= _barFileLoadedHandler;
        _rebuildSemaphore.Dispose();
        base.OnClosing(e);
    }

    async Task RebuildExclusionRegex()
    {
        _rebuildPending = true;

        // If already rebuilding, just mark as pending and return
        if (!await _rebuildSemaphore.WaitAsync(0))
            return;

        try
        {
            while (_rebuildPending)
            {
                _rebuildPending = false;
                var filter = _exclusionFilter;

                var task = Task.Run(() =>
                {
                    if (string.IsNullOrWhiteSpace(filter))
                    {
                        _fileExclusionRegex = null;
                        return;
                    }

                    var patterns = filter
                        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Select(p => EscapeFilter(p));

                    var pattern = $"^({string.Join("|", patterns)})$";
                    _fileExclusionRegex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                });

                await task.ConfigureAwait(false);
            }
        }
        finally
        {
            _rebuildSemaphore.Release();
        }
    }

    static string EscapeFilter(string filter) => Regex.Escape(filter).Replace("\\*", ".*");

    bool IsFileExcluded(string filename) => _fileExclusionRegex?.IsMatch(filename) ?? false;

    void EnsureSettingsSaved()
    {
        // save used filter
        if (_owner != null &&
            (_owner._searchExclusionFilter != _exclusionFilter ||
             _owner._searchCaseSensitive != _isCaseSensitive ||
             _owner._searchRegex != _isRegex ||
             _owner._searchFilesOnly != _isFilesOnly))
        {
            _owner._searchExclusionFilter = _exclusionFilter;
            _owner._searchCaseSensitive = _isCaseSensitive;
            _owner._searchRegex = _isRegex;
            _owner._searchFilesOnly = _isFilesOnly;
            _owner.SaveConfiguration();
        }
    }

    async void Search_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        const int MAX_FILE_SIZE = 100_000_000; // 100 MB

        if (!CanSearch)
            return;

        if (CurrentlySearching)
        {
            // cancel previous search
            _csc?.Cancel();
            CurrentlySearching = false;
            return;
        }

        // wait for any in-progress rebuild to finish
        await _rebuildSemaphore.WaitAsync();
        _rebuildSemaphore.Release();

        EnsureSettingsSaved();

        _csc = new();
        var token = _csc.Token;
        var query = Query;
        var filesOnly = IsFilesOnly;
        CurrentlySearching = true;
        SearchResults.Clear();

        // we want to continue on different thread after this point - to not block UI
        await Task.Delay(10).ConfigureAwait(false);

        var time_started = Stopwatch.GetTimestamp();
        var comparer = IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        var regex_options = RegexOptions.Compiled;
        if (!IsCaseSensitive)
            regex_options |= RegexOptions.IgnoreCase;

        Regex? regex = null;
        if (IsRegex)
        {
            try
            {
                regex = await Task.Run(() => new Regex(query, regex_options, TimeSpan.FromMilliseconds(800)));
            }
            catch (RegexParseException ex)
            {
                Status = $"Invalid regex: {ex.Message}";
                CurrentlySearching = false;
                return;
            }
        }

        // THIS IS THE SEARCH FUNCTION
        Func<string, int, (int index, int length)> searcher = regex == null ?
            (txt, si) =>
            {
                var i = txt.IndexOf(query, si, comparer);
                return (i, query.Length);
            } :
            (txt, si) =>
            {
                try
                {
                    var m = regex.Match(txt, si);
                    if (!m.Success) return (-1, 0);
                    return (m.Index, m.Length);
                }
                catch (RegexMatchTimeoutException)
                {
                    return (-1, 0);
                }
            };

        // searching state
        var searched = new ConcurrentDictionary<string, byte>();
        var current_items = new ConcurrentDictionary<string, byte>();
        var channel = Channel.CreateUnbounded<SearchResult>();
        var search_finished = false;

        var root_files = _rootEntries?.Where(x => !IsFileExcluded(Path.GetFileName(x.RelativePath))).ToArray();
        var processed_root_files_count = 0;
        var total_root_files_count = root_files?.Length;

        // this channnel consumer will handle updating the UI more efficiently in batches
        var consumer = Task.Run(async () =>
        {
            const int BATCH_SIZE = 10;
            var batch = new List<SearchResult>(BATCH_SIZE);
            await foreach (var result in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                batch.Add(result);
                if (batch.Count >= BATCH_SIZE || !channel.Reader.TryPeek(out _))
                {
                    var toAdd = batch.ToArray();
                    batch.Clear();

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        foreach (var r in toAdd)
                            SearchResults.Add(r);
                    });
                }
            }
            // flush remaining items 
            if (batch.Count > 0)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var r in batch)
                        SearchResults.Add(r);
                });
            }
        }, token);

        var updater = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !search_finished)
            {
                Status = $"[{processed_root_files_count}/{total_root_files_count}] {string.Join(", ", current_items.Select(x => x.Key))}";
                await Task.Delay(100, token).ConfigureAwait(false);
            }
        }, token);


        try
        {
            // Search entries inside a BAR file (each call owns its stream — no shared cache)
            async ValueTask SearchBar(BarFile bar, Stream barStream, string file, CancellationToken token)
            {
                if (bar.Entries == null) return;
                foreach (var bar_entry in bar.Entries)
                {
                    if (token.IsCancellationRequested) break;
                    if (IsFileExcluded(bar_entry.Name))
                        continue;

                    var (name_index, matched_length) = searcher(bar_entry.RelativePath, 0);
                    if (name_index >= 0)
                    {
                        var context = MakeContext(name_index, bar_entry.RelativePath.AsSpan(name_index, matched_length), bar_entry.RelativePath);
                        var result = new SearchResult(file, bar_entry.RelativePath, name_index, context.left, context.mid, context.right, false);
                        await channel.Writer.WriteAsync(result, token).ConfigureAwait(false);
                    }

                    if (!filesOnly)
                    {
                        if (bar_entry.SizeUncompressed > MAX_FILE_SIZE) continue;
                        using var rawData = await bar_entry.ReadDataRawPooledAsync(barStream);
                        using var ddata = BarCompression.EnsureDecompressedPooled(rawData, out _);
                        await SearchData(ddata.Memory, file, bar_entry.RelativePath, channel.Writer, searcher, token).ConfigureAwait(false);
                    }
                }
            }

            // go through all root files (in parallel)
            if (root_files != null && _rootDirectory != null && _owner != null)
            {
                // Capture the currently-open BAR path now (before going parallel)
                // so a mid-search BAR switch can't cause ObjectDisposedException.
                var currentBarPath = _owner.BarFileStream?.Name;
                if (currentBarPath != null)
                    searched.TryAdd(currentBarPath, 0); // mark as searched below

                await Parallel.ForEachAsync(root_files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    async (entry, token) =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        Interlocked.Increment(ref processed_root_files_count);
                        var file = Path.Combine(_rootDirectory, entry.RelativePath);

                        if (!searched.TryAdd(file, 0))
                            return;

                        var file_name = Path.GetFileName(file);
                        current_items.TryAdd(file_name, 0);

                        try
                        {
                            var (name_index, matched_length) = searcher(file, 0);
                            if (name_index >= 0)
                            {
                                var context = MakeContext(name_index, file.AsSpan(name_index, matched_length), file);
                                var result = new SearchResult(file, null, name_index, context.left, context.mid, context.right, false);
                                await channel.Writer.WriteAsync(result, token).ConfigureAwait(false);
                            }

                            var ext = Path.GetExtension(file).ToLower();
                            if (ext == ".bar")
                            {
                                using var barStream = File.OpenRead(file);
                                var barFile = new BarFile(barStream);
                                if (barFile.Load(out _))
                                    await SearchBar(barFile, barStream, file, token);
                            }
                            else if (!filesOnly)
                            {
                                if (new FileInfo(file).Length > MAX_FILE_SIZE) return;

                                var data = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                                var ddata = BarCompression.EnsureDecompressed(data, out _);
                                await SearchData(ddata, file, null, channel.Writer, searcher, token).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            current_items.TryRemove(file_name, out _);
                        }
                    })
                    .ConfigureAwait(false);

                // search the currently open BAR (may not be in root files list)
                if (currentBarPath != null)
                {
                    var file_name = Path.GetFileName(currentBarPath);
                    if (!IsFileExcluded(file_name))
                    {
                        current_items.TryAdd(file_name, 0);
                        using var ownStream = File.OpenRead(currentBarPath);
                        var barFile = new BarFile(ownStream);
                        if (barFile.Load(out _))
                            await SearchBar(barFile, ownStream, currentBarPath, token);
                        current_items.TryRemove(file_name, out _);
                    }
                }
            }

            search_finished = true;
            await Task.Delay(50).ConfigureAwait(false);
            Status = "Done";
        }
        catch (OperationCanceledException)
        {
            Status = "Search cancelled";
        }
        catch (Exception ex)
        {
            Status = "Searching error: " + ex.Message;
        }
        finally
        {
            search_finished = true;
            channel.Writer.TryComplete();
            try { await consumer.ConfigureAwait(false); } catch { }
            try { await updater.ConfigureAwait(false); } catch { }
            _csc.Cancel();
            CurrentlySearching = false;
        }

        // Search allocates many short-lived buffers (BAR streams, decompressed data, char arrays).
        // Force a compacting GC to reclaim that memory now that the search is done.
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // show elapsed time at end
        var time_elapsed = Stopwatch.GetElapsedTime(time_started);
        if (time_elapsed.TotalSeconds < 60)
            Status += $" [{time_elapsed.TotalSeconds:0.0}s]";
        else
            Status += $" [{time_elapsed.TotalMinutes:0.0}min]";

        static async ValueTask SearchData(ReadOnlyMemory<byte> decompressed_data, string file_path, string? bar_entry_path,
                ChannelWriter<SearchResult> channel, Func<string, int, (int index, int matched_length)> searcher, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;

            ReadOnlySpan<byte> span = decompressed_data.Span;

            var text = "";

            var ext = Path.GetExtension(bar_entry_path ?? file_path).ToLower();
            if (InvalidSearchExtensions.Contains(ext)) return;

            if (ext == ".xmb")
            {
                var xmlText = ConversionHelper.ConvertXmbToXmlText(span);
                if (xmlText == null) return;

                text = xmlText;
            }
            else
            {
                var unicode = MainWindow.DetectIfUnicode(span);
                var encoding = unicode ? Encoding.Unicode : Encoding.UTF8;
                var charCount = encoding.GetCharCount(span);
                var chars = ArrayPool<char>.Shared.Rent(charCount);
                try
                {
                    var actualCount = encoding.GetChars(span, chars);
                    text = new string(chars, 0, actualCount);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(chars);
                }
            }

            // now search
            var start_index = 0;
            while (true)
            {
                var (found_index, matched_length) = searcher(text, start_index);
                if (found_index == -1) break;
                start_index = found_index + 1;

                var context = MakeContext(found_index, text.AsSpan(found_index, matched_length), text);

                if (token.IsCancellationRequested) break;

                var result = new SearchResult(file_path, bar_entry_path, found_index, context.left, context.mid, context.right, true);
                await channel.WriteAsync(result, token).ConfigureAwait(false);
            }
        }

        static (string left, string mid, string right) MakeContext(int index, ReadOnlySpan<char> matched_text, string text)
        {
            const int LEFT_CONTEXT_SIZE = 15;
            const int RIGHT_CONTEXT_SIZE = 25;

            var match_begin = index;
            var match_end = index + matched_text.Length;

            var from = Math.Max(0, match_begin - LEFT_CONTEXT_SIZE);
            var to = Math.Min(text.Length, match_end + RIGHT_CONTEXT_SIZE);
            var full_context = text[from..to];
            var left_context = MakeItSafe(full_context[..(match_begin - from)]);
            var right_context = MakeItSafe(full_context[(match_begin - from + matched_text.Length)..]);
            var mid_context = MakeItSafe(text[match_begin..match_end]);

            return (left_context, mid_context, right_context);
        }

        static string MakeItSafe(string text) => GetUnsafeCharsRgx().Replace(text, " ");
    }

    static readonly HashSet<string> InvalidSearchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".tga", ".ddt", ".png", ".gif", ".jpx", ".webp",
        ".wav", ".mp3", ".wmv", ".opus", ".vorbis", ".ogg", ".m4a",
        ".mp4", ".mov", ".webm", ".avi", ".mkv",
        // FOR NOW WE IGNORE THE FOLLOWING UNTIL WE CAN READ THEM:
        ".data", ".hkt", ".tma", ".tmm"
    };

    bool _selectionInProgress = false;
    async void SearchResultOpen_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectionInProgress)
            return;

        var result = (sender as Button)?.DataContext as SearchResult;
        if (result == null || _owner == null) return;

        _selectionInProgress = true;
        try
        {
            var file = Path.GetRelativePath(_owner.RootDirectory, result.RelevantFile);
            var bar_entry = result.EntryWithinBAR;

            // Use FindAndRevealRootFile to handle filtered-out entries
            var toSelect = _owner.FindAndRevealRootFile(file);
            if (toSelect == null)
                return;

            // set selection and wait for it to load
            _owner.SelectedRootFileEntry = toSelect;
            await Task.Delay(50);
            if (_owner.SelectedRootFileEntry != toSelect)
                return;

            if (!string.IsNullOrEmpty(bar_entry))
            {
                // BAR file
                var toSelectBarEntry = _owner.FindAndRevealBarFileEntry(bar_entry);
                if (toSelectBarEntry == null)
                    return;

                // set selected bar entry and wait it to load
                _owner.SelectedBarEntry = toSelectBarEntry;
                await Task.Delay(50);
            }
            else
            {
                // deselect any opened BAR entry
                _owner.SelectedBarEntry = null;
            }

            var text = _owner.PreviewText;
            if (!result.WithinContent || text.Length < 100)
                return;

            // Wait for the full document to finish loading (may be async for large files)
            await _owner._docReadyTask;
            await Task.Delay(100);

            // this is index of character within file
            var index = result.IndexWithinContent;

            // Cancel any pending scroll-to-top from SetEditorText
            _owner._scrollVersion++;

            var location = _owner._txtEditor.Document.GetLocation(index);
            _owner._txtEditor.ScrollTo(location.Line, location.Column);

            if (index >= 0 && index + result.ContextMain.Length < text.Length)
                _owner._txtEditor.Select(index, result.ContextMain.Length);
        }
        finally
        {
            _selectionInProgress = false;
        }
    }

    [GeneratedRegex(@"\n|\r|[^\u0020-\u007E\u00A1-]")]
    private static partial Regex GetUnsafeCharsRgx();
}

public class SearchResult
{
    public string ShortenedFilePath { get; }
    public string RelevantFile { get; }
    public string? EntryWithinBARDisplay { get; }
    public string? EntryWithinBAR { get; }
    public int IndexWithinContent { get; }
    public string ContextLeft { get; }
    public string ContextMain { get; }
    public string ContextRight { get; }
    public bool WithinContent { get; }

    public SearchResult(string file, string? bar_entry, int index, string context_left, string context_main, string context_right, bool within_content)
    {
        RelevantFile = file;
        EntryWithinBARDisplay = bar_entry == null ? "" : ("BAR: " + bar_entry);
        EntryWithinBAR = bar_entry ?? "";
        IndexWithinContent = index;
        ContextLeft = context_left;
        ContextMain = context_main;
        ContextRight = context_right;
        WithinContent = within_content;

        if (file.Length > 70)
        {
            ShortenedFilePath = "..." + file[^70..];
        }
        else
        {
            ShortenedFilePath = file;
        }
    }
}