using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Threading;

using CryBar;
using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class DependenciesWindow : SimpleWindow
{
    string _filter = "";
    bool _isLoading;

    readonly MainWindow _owner;
    readonly List<DependencyGroupItem> _allGroups = new();

    /// <summary>
    /// Tracks per-reference which resolved index to navigate to next (cycles on repeated clicks).
    /// </summary>
    readonly Dictionary<DependencyReference, int> _resolvedNavigationIndex = new();

    public string Filter
    {
        get => _filter;
        set { _filter = value; OnSelfChanged(); RefreshFiltered(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnSelfChanged(); }
    }

    public string WindowTitle { get; private set; } = "Dependencies";
    public string StatusText => IsLoading ? "Loading..." : $"{FilteredGroups.Count} groups, {_totalRefs} total references";
    public ObservableCollectionExtended<DependencyGroupItem> FilteredGroups { get; } = new();

    int _totalRefs;

    // Simple converters for XAML bindings
    public static IValueConverter GreaterThan1Converter { get; } = new FuncValueConverter<int, bool>(v => v > 1);
    public static IValueConverter EqualsZeroConverter { get; } = new FuncValueConverter<int, bool>(v => v == 0);
    public static IValueConverter PluralSConverter { get; } = new FuncValueConverter<int, string>(v => v != 1 ? "es" : "");

    public DependenciesWindow()
    {
        _owner = null!;
        DataContext = this;
        InitializeComponent();
    }

    public DependenciesWindow(MainWindow owner) : this()
    {
        _owner = owner;

        // Size to owner height
        if (owner.Height > 0)
            Height = owner.Height;
    }

    public void LoadDependencies(string content, string entryPath, FileIndex? fileIndex, SoundsetIndex? soundsetIndex, string? stringTableLanguage = null)
    {
        WindowTitle = $"Dependencies \u2014 {Path.GetFileName(entryPath)}";
        OnPropertyChanged(nameof(WindowTitle));
        Title = WindowTitle;

        IsLoading = true;
        _allGroups.Clear();
        FilteredGroups.Clear();
        _resolvedNavigationIndex.Clear();
        OnPropertyChanged(nameof(StatusText));

        Task.Run(() =>
        {
            var result = DependencyFinder.FindDependencies(content, entryPath, fileIndex, soundsetIndex, stringTableLanguage);
            var items = result.Groups.Select(g => new DependencyGroupItem(g)).ToList();

            Dispatcher.UIThread.InvokeAsync(() =>
            {
                _allGroups.Clear();
                _allGroups.AddRange(items);
                _totalRefs = _allGroups.Sum(g => g.ReferenceCount);
                RefreshFiltered();
                IsLoading = false;
                OnPropertyChanged(nameof(StatusText));
            });
        });
    }

    void RefreshFiltered()
    {
        FilteredGroups.Clear();
        var filter = _filter.Trim();
        foreach (var group in _allGroups)
        {
            if (filter.Length > 0)
            {
                // Match against group name, entity type, or any reference value
                var matchesGroup = group.DisplayName.Contains(filter, StringComparison.OrdinalIgnoreCase)
                    || group.EntityTypeLabel.Contains(filter, StringComparison.OrdinalIgnoreCase);

                if (!matchesGroup)
                {
                    var matchesRef = group.References.Any(r =>
                        r.RawValue.Contains(filter, StringComparison.OrdinalIgnoreCase));
                    if (!matchesRef) continue;
                }
            }
            FilteredGroups.Add(group);
        }
        OnPropertyChanged(nameof(StatusText));
    }

    bool _navigationInProgress;

    async void NavigateToRef_Click(object? sender, RoutedEventArgs e)
    {
        if (_navigationInProgress) return;

        var reference = (sender as Button)?.DataContext as DependencyReference;
        if (reference == null || reference.Resolved.Count == 0) return;

        _navigationInProgress = true;
        try
        {
            // Cycle through resolved entries on repeated clicks
            _resolvedNavigationIndex.TryGetValue(reference, out var idx);
            if (idx >= reference.Resolved.Count) idx = 0;

            await NavigateToIndexEntry(reference.Resolved[idx]);

            // For string keys, highlight the key in the previewed document
            if (reference.Type == DependencyRefType.StringKey)
                await _owner.HighlightTextInPreviewAsync(reference.RawValue);

            _resolvedNavigationIndex[reference] = idx + 1;
        }
        finally
        {
            _navigationInProgress = false;
        }
    }

    async Task NavigateToIndexEntry(FileIndexEntry entry)
    {
        if (entry.Source == FileIndexSource.BarEntry && entry.BarFilePath != null && entry.EntryRelativePath != null)
        {
            // Navigate to a BAR file entry
            var barRelPath = entry.BarFilePath;
            if (Directory.Exists(_owner.RootDirectory) && barRelPath.StartsWith(_owner.RootDirectory))
                barRelPath = Path.GetRelativePath(_owner.RootDirectory, barRelPath);

            await _owner.NavigateToBarEntryAsync(barRelPath, entry.EntryRelativePath);
        }
        else
        {
            // Navigate to a root file
            // Strip the root relevant path prefix to get the root-relative path
            var fullRelPath = entry.FullRelativePath;
            var rootRelevantPath = _owner.RootFileRootPath;
            if (rootRelevantPath != "-" && fullRelPath.StartsWith(rootRelevantPath, StringComparison.OrdinalIgnoreCase))
                fullRelPath = fullRelPath[rootRelevantPath.Length..];

            _owner.NavigateToRootFile(fullRelPath);
        }

        // Wait for preview to load after navigation
        await Task.Delay(50);
    }
}
