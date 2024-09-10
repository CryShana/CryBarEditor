using Avalonia.Controls;
using Avalonia.Threading;

using CryBar;

using CryBarEditor.Classes;

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;


namespace CryBarEditor;

public partial class SearchWindow : Window, INotifyPropertyChanged
{
    string _status = "This searches through all FILTERED files and opened BAR archives";
    string _query = "";
    bool _searching = false;
    CancellationTokenSource? _csc;

    public bool CurrentlySearching { get => _searching; set { _searching = value; OnPropertyChanged(nameof(CurrentlySearching)); } }
    public bool CanSearch => _query.Length > 2;
    public string Query { get => _query; set { _query = value; OnPropertyChanged(nameof(Query)); OnPropertyChanged(nameof(CanSearch)); } }
    public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
    public ObservableCollectionExtended<SearchResult> SearchResults { get; } = new();

    readonly string? _rootDirectory;
    readonly List<FileEntry>? _rootEntries;
    readonly BarFile? _barFile;
    readonly FileStream? _barFileStream;

    public SearchWindow()
    {
        DataContext = this;
        InitializeComponent();
    }

    public SearchWindow(MainWindow owner) : this()
    {
        _rootDirectory = owner.RootDirectory;
        if (Directory.Exists(_rootDirectory))
            _rootEntries = owner.FileEntries.ToList();

        _barFile = owner.BarFile;
        _barFileStream = owner.BarFileStream;

        var count = (_barFile != null ? 1 : 0) + (_rootEntries?.Count ?? 0);

        Title = $"Search in [{count}] captured files";
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _csc?.Cancel();
        base.OnClosing(e);
    }

    async void Search_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        const int MAX_FILE_SIZE = 4_000_000; // 4 MB

        if (!CanSearch)
            return;

        if (CurrentlySearching)
        {
            // cancel previous search
            _csc?.Cancel();
            CurrentlySearching = false;
            return;
        }

        _csc = new();
        var token = _csc.Token;
        var query = Query;
        CurrentlySearching = true;
        SearchResults.Clear();

        try
        {
            await Task.Run(() =>
            {
                var searched = new HashSet<string>();
                var current_items = new List<string>();

                // go through bar file if opened (it's not always included with root files)
                if (_barFile?.Entries != null && _barFileStream != null)
                {
                    var file = _barFileStream.Name;
                    if (searched.Add(file))
                    {
                        Status = Path.GetFileName(file);

                        foreach (var bar_entry in _barFile.Entries)
                        {
                            if (token.IsCancellationRequested) break;
                            if (bar_entry.SizeUncompressed > MAX_FILE_SIZE) continue;

                            // check the filename itself
                            var name_index = bar_entry.RelativePath.IndexOf(query);
                            if (name_index >= 0)
                            {
                                var context = MakeContext(name_index, query, bar_entry.RelativePath);
                                SearchResults.Add(new SearchResult(file, bar_entry.RelativePath, name_index, context.left, context.mid, context.right));
                            }

                            var ddata = bar_entry.ReadDataDecompressed(_barFileStream);
                            SearchData(ddata, file, bar_entry.RelativePath, SearchResults, query, token);
                        }
                    }
                }

                // go through all root files (in parallel)
                if (_rootEntries != null && _rootDirectory != null)
                {
                    var count = 0;
                    var total_count = _rootEntries.Count;

                    Parallel.ForEach(_rootEntries, new ParallelOptions { MaxDegreeOfParallelism = 4 },
                        (entry, opt) =>
                        {
                            if (token.IsCancellationRequested)
                            {
                                opt.Break();
                                return;
                            }

                            Interlocked.Increment(ref count);
                            var file = Path.Combine(_rootDirectory, entry.RelativePath);

                            lock (searched)
                                if (!searched.Add(file)) 
                                    return;

                            // status update
                            var file_name = Path.GetFileName(file);
                            lock (current_items)
                            {
                                current_items.Add(file_name);
                                Status = $"[{count}/{total_count}] {string.Join(", ", current_items)}";
                            }

                            try
                            {
                                // check the filename itself
                                var name_index = file.IndexOf(query);
                                if (name_index >= 0)
                                {
                                    var context = MakeContext(name_index, query, file);

                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        SearchResults.Add(new SearchResult(file, null, name_index, context.left, context.mid, context.right));
                                    });
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
                                            if (bar_entry.SizeUncompressed > MAX_FILE_SIZE) continue;

                                            // check the filename itself
                                            name_index = bar_entry.RelativePath.IndexOf(query);
                                            if (name_index >= 0)
                                            {
                                                var context = MakeContext(name_index, query, bar_entry.RelativePath);
                                                Dispatcher.UIThread.Post(() =>
                                                {
                                                    SearchResults.Add(new SearchResult(file, bar_entry.RelativePath, name_index, context.left, context.mid, context.right));
                                                });
                                            }

                                            var ddata = bar_entry.ReadDataDecompressed(stream);
                                            SearchData(ddata, file, bar_entry.RelativePath, SearchResults, query, token);
                                        }
                                    }
                                }
                                else
                                {
                                    if (new FileInfo(file).Length > MAX_FILE_SIZE) return;

                                    // process file directly
                                    var data = File.ReadAllBytes(file);
                                    var ddata = BarCompression.EnsureDecompressed(data, out _);
                                    SearchData(ddata, file, null, SearchResults, query, token);
                                }
                            }
                            finally
                            {
                                lock (current_items)
                                {
                                    current_items.Remove(file_name);
                                }
                            }
                        });
                }

                Status = "Done";
            });
        }
        finally
        {
            CurrentlySearching = false;
        }

        static bool ValidForSearch(string ext)
        {
            if (ext is ".jpg" or ".jpeg" or ".tga" or ".ddt" or ".png" or ".gif" or ".jpx" or ".webp") 
                return false;

            if (ext is ".wav" or ".mp3" or ".wmv" or ".opus" or ".vorbis" or ".ogg" or ".m4a")
                return false;

            if (ext is ".mp4" or ".mov" or ".webm" or ".avi" or ".mkv")
                return false;

            // FOR NOW IGNORE SPECIAL FORMATS WE CAN'T READ (this may change)
            if (ext is ".data" or ".hkt" or ".tma" or ".tmm")
                return false;

            return true;
        }

        static void SearchData(Memory<byte> decompressed_data, string file_path, string? bar_entry_path, IList<SearchResult> results, string query, CancellationToken token)
        {
            if (token.IsCancellationRequested) return;
            
            var text = "";

            var ext = Path.GetExtension(bar_entry_path ?? file_path).ToLower();
            if (!ValidForSearch(ext)) return;

            if (ext == ".xmb")
            {
                // let's also parse XMB
                var xml = BarFormatConverter.XMBtoXML(decompressed_data.Span);
                if (xml == null) return;

                text = BarFormatConverter.FormatXML(xml);
            }
            else
            {
                text = Encoding.UTF8.GetString(decompressed_data.Span);
            }

            // now search
            var start_index = 0;
            while (true)
            {
                var found_index = text.IndexOf(query, start_index);
                if (found_index == -1) break;
                start_index = found_index + 1;

                var context = MakeContext(found_index, query, text);

                if (token.IsCancellationRequested) break;

                var result = new SearchResult(file_path, bar_entry_path, found_index, context.left, context.mid, context.right);
                Dispatcher.UIThread.Post(() =>
                {
                    results.Add(result);
                });
            }
        }

        static (string left, string mid, string right) MakeContext(int index, string query, string text)
        {
            const int LEFT_CONTEXT_SIZE = 15;
            const int RIGHT_CONTEXT_SIZE = 25;

            var match_begin = index;
            var match_end = index + query.Length;

            var from = Math.Max(0, match_begin - LEFT_CONTEXT_SIZE);
            var to = Math.Min(text.Length, match_end + RIGHT_CONTEXT_SIZE);
            var full_context = text[from..to];
            var left_context = MakeItSafe(full_context[..(match_begin - from)]);
            var right_context = MakeItSafe(full_context[(match_begin - from + query.Length)..]);

            return (left_context, query, right_context);
        }

        static string MakeItSafe(string text)
        {
            return GetUnsafeCharsRgx().Replace(text, " ");
        }
    }

    [GeneratedRegex(@"\n|\r|[^\u0020-\u007E\u00A1-]")]
    private static partial Regex GetUnsafeCharsRgx();


    public new event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class SearchResult
{
    public string ShortenedFilePath { get; }
    public string RelevantFile { get; }
    public string? EntryWithinBAR { get; }
    public int IndexWithinContent { get; }
    public string ContextLeft { get; }
    public string ContextMain { get; }
    public string ContextRight { get; }

    public SearchResult(string file, string? bar_entry, int index, string context_left, string context_main, string context_right)
    {
        RelevantFile = file;
        EntryWithinBAR = bar_entry == null ? "" : ("BAR: " + bar_entry);
        IndexWithinContent = index;
        ContextLeft = context_left;
        ContextMain = context_main;
        ContextRight = context_right;

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