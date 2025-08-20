using Avalonia.Controls;
using Avalonia.Threading;

using CryBar;

using CryBarEditor.Classes;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using System.Buffers;
using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace CryBarEditor;

public partial class SearchWindow : SimpleWindow
{
    string _status = "This searches through all FILTERED files and opened BAR archives";
    string _query = "";
    string _exclusionFilter = "";
    bool _isRegex = false;
    bool _isCaseInsensitive = false;
    bool _searching = false;
    CancellationTokenSource? _csc;

    public bool CurrentlySearching { get => _searching; set { _searching = value; OnSelfChanged(); } }
    public bool CanSearch => _query.Length > 2;

    public string Query { get => _query; set { _query = value; OnSelfChanged(); OnPropertyChanged(nameof(CanSearch)); } }
    public string Status { get => _status; set { _status = value; OnSelfChanged(); } }
    public string ExclusionFilter { get => _exclusionFilter; set { _exclusionFilter = value; _ = RebuildExclusionRegex(); OnSelfChanged(); } }
    public bool IsRegex { get => _isRegex; set { _isRegex = value; OnSelfChanged(); if (value) IsCaseInsensitive = false; } }
    public bool IsCaseInsensitive { get => _isCaseInsensitive; set { _isCaseInsensitive = value; OnSelfChanged(); } }
    public ObservableCollectionExtended<SearchResult> SearchResults { get; } = new();

    private Regex? _fileExclusionRegex;
    private Task? _rebuildTask;
    private bool _rebuildPending;
    private readonly SemaphoreSlim _rebuildSemaphore = new(1, 1);

    readonly string? _rootDirectory;
    readonly List<RootFileEntry>? _rootEntries;

    BarFile? _barFile;
    FileStream? _barFileStream;
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

        OnBarFileChanged(owner.BarFile, owner.BarFileStream);
        owner.OnBarFileLoaded += OnBarFileChanged;

        ExclusionFilter = _owner._searchExclusionFilter;
    }

    void OnBarFileChanged(BarFile? file, FileStream? stream)
    {
        // different BAR file opened
        _barFile = file;
        _barFileStream = stream;

        // adjust this count
        var count = (_barFile != null ? 1 : 0) + (_rootEntries?.Count ?? 0);
        Title = $"Search in [{count}] captured files";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _csc?.Cancel();
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

                _rebuildTask = Task.Run(() =>
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

                await _rebuildTask;
            }

            _rebuildTask = null;
        }
        finally
        {
            _rebuildSemaphore.Release();
        }
    }

    static string EscapeFilter(string filter) => Regex.Escape(filter).Replace("\\*", ".*");
    
    bool IsFileExcluded(string filename) => _fileExclusionRegex?.IsMatch(filename) ?? false;

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

        // if rebuilding in progress, wait for it to finish
        if (_rebuildTask != null)
            await _rebuildTask;

        // save used filter
        if (_owner != null && _owner._searchExclusionFilter != _exclusionFilter)
        {
            _owner._searchExclusionFilter = _exclusionFilter;
            _owner.SaveConfiguration();
        }

        _csc = new();
        var token = _csc.Token;
        var query = Query;
        CurrentlySearching = true;
        SearchResults.Clear();

        var time_started = Stopwatch.GetTimestamp();
        var comparer = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var regex = IsRegex ? await Task.Run(() => new Regex(query, RegexOptions.Compiled | RegexOptions.Singleline)) : null;

        // THIS IS THE SEARCH FUNCTION
        Func<string, int, (int index, int length)> searcher = regex == null ?
            (txt, si) =>
            {
                var i = txt.IndexOf(query, si, comparer);
                return (i, query.Length);
            }
        :
            (txt, si) =>
            {
                var m = regex.Match(txt, si);
                if (!m.Success) return (-1, 0);
                return (m.Index, m.Length);
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
            await foreach (var result in channel.Reader.ReadAllAsync(token))
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
        }, token);

        var updater = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && !search_finished)
            {
                Status = $"[{processed_root_files_count}/{total_root_files_count}] {string.Join(", ", current_items.Select(x => x.Key))}";
                await Task.Delay(100, token);
            }
        }, token);
        

        try
        {
            // go through bar file if opened (it's not always included with root files)
            if (_barFile?.Entries != null && _barFileStream != null)
            {
                var file = _barFileStream.Name;
                if (searched.TryAdd(file, 0))
                {
                    var filename = Path.GetFileName(file);
                    if (!IsFileExcluded(filename))
                    {
                        Status = filename;
                        foreach (var bar_entry in _barFile.Entries)
                        {
                            if (token.IsCancellationRequested) break;
                            if (IsFileExcluded(bar_entry.Name))
                                continue;

                            // check the filename itself
                            var (name_index, matched_length) = searcher(bar_entry.RelativePath, 0);
                            if (name_index >= 0)
                            {
                                var context = MakeContext(name_index, bar_entry.RelativePath.AsSpan(name_index, matched_length), bar_entry.RelativePath);
                                var result = new SearchResult(file, bar_entry.RelativePath, name_index, context.left, context.mid, context.right, false);
                                await channel.Writer.WriteAsync(result, token);
                            }

                            if (bar_entry.SizeUncompressed > MAX_FILE_SIZE) continue;
                            var ddata = bar_entry.ReadDataDecompressed(_barFileStream);
                            await SearchData(ddata, file, bar_entry.RelativePath, channel.Writer, searcher, token);
                        }
                    }
                }
            }

            // go through all root files (in parallel)
            if (root_files != null && _rootDirectory != null)
            {
                await Parallel.ForEachAsync(root_files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
                    async (entry, token) =>
                    {
                        if (token.IsCancellationRequested)
                            return;

                        Interlocked.Increment(ref processed_root_files_count);
                        var file = Path.Combine(_rootDirectory, entry.RelativePath);

                        // check if file is being processed by different thread
                        if (!searched.TryAdd(file, 0))
                            return;

                        // status update
                        var file_name = Path.GetFileName(file);
                        current_items.TryAdd(file_name, 0);

                        try
                        {
                            // check the filename itself
                            var (name_index, matched_length) = searcher(file, 0);
                            if (name_index >= 0)
                            {
                                var context = MakeContext(name_index, file.AsSpan(name_index, matched_length), file);
                                var result = new SearchResult(file, null, name_index, context.left, context.mid, context.right, false);
                                await channel.Writer.WriteAsync(result, token);
                            }

                            var ext = Path.GetExtension(file).ToLower();
                            if (ext == ".bar")
                            {
                                // load bar
                                using var stream = File.OpenRead(file);
                                var bar_file = new BarFile(stream);
                                if (bar_file.Load(out _))
                                {
                                    foreach (var bar_entry in bar_file.Entries)
                                    {
                                        if (token.IsCancellationRequested) break;
                                        if (IsFileExcluded(bar_entry.Name))
                                            continue;

                                        // check the filename itself
                                        (name_index, matched_length) = searcher(bar_entry.RelativePath, 0);
                                        if (name_index >= 0)
                                        {
                                            var context = MakeContext(name_index, bar_entry.RelativePath.AsSpan(name_index, matched_length), bar_entry.RelativePath);
                                            var result = new SearchResult(file, bar_entry.RelativePath, name_index, context.left, context.mid, context.right, false);
                                            await channel.Writer.WriteAsync(result, token);
                                        }

                                        if (bar_entry.SizeUncompressed > MAX_FILE_SIZE) continue;
                                        var ddata = bar_entry.ReadDataDecompressed(stream);
                                        await SearchData(ddata, file, bar_entry.RelativePath, channel.Writer, searcher, token);
                                    }
                                }
                            }
                            else
                            {
                                if (new FileInfo(file).Length > MAX_FILE_SIZE) return;

                                // process file directly
                                var data = await File.ReadAllBytesAsync(file);
                                var ddata = BarCompression.EnsureDecompressed(data, out _);
                                await SearchData(ddata, file, null, channel.Writer, searcher, token);
                            }
                        }
                        finally
                        {
                            current_items.TryRemove(file_name, out _);
                        }
                    });
            }

            search_finished = true;
            channel.Writer.Complete();
            await consumer;
            await updater;
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
            CurrentlySearching = false;
        }

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
                // let's also parse XMB
                var xml = BarFormatConverter.XMBtoXML(span);
                if (xml == null) return;

                text = BarFormatConverter.FormatXML(xml);
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
                await channel.WriteAsync(result, token);
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
            RootFileEntry? toSelect = null;
            foreach (var entry in _owner.RootFileEntries)
            {
                if (entry.RelativePath != file)
                    continue;

                toSelect = entry;
                break;
            }

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
                if (_owner?.BarFile?.Entries == null)
                    return;

                BarFileEntry? toSelectBarEntry = null;
                foreach (var entry in _owner.BarFile.Entries)
                {
                    if (entry.RelativePath != bar_entry)
                        continue;

                    toSelectBarEntry = entry;
                    break;
                }

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

            // wait a bit for editor to load
            await Task.Delay(50);

            // this is index of character within file
            var index = result.IndexWithinContent;

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