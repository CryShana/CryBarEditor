using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;

using CryBar.Dependencies;
using CryBar.Indexing;
using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace CryBarEditor.Windows;

public partial class DependenciesWindow : SimpleWindow
{
    string _filter = "";
    bool _isLoading;

    readonly MainWindow _owner;
    readonly List<DependencyGroupItem> _allGroups = new();
    string _currentEntryPath = "";
    FileIndex? _currentFileIndex;

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
        set { _isLoading = value; OnSelfChanged(); OnPropertyChanged(nameof(StatusText)); }
    }

    public string WindowTitle { get; private set; } = "Dependencies";
    public string StatusText => IsLoading ? "Loading..." : $"{FilteredGroups.Count} groups, {_totalRefs} total references";
    public ObservableCollectionExtended<DependencyGroupItem> FilteredGroups { get; } = new();

    int _totalRefs;

    // Simple converters for XAML bindings
    public static IValueConverter GreaterThan0Converter { get; } = new FuncValueConverter<int, bool>(v => v > 0);
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

    public string CurrentEntryPath => _currentEntryPath;

    /// <summary>
    /// Shows the window in loading state immediately, before dependency analysis completes.
    /// </summary>
    public void StartLoading(string entryPath, string? displayName = null)
    {
        _currentEntryPath = entryPath;
        IsLoading = true;
        WindowTitle = $"Dependencies \u2014 {displayName ?? Path.GetFileName(entryPath)}";
        OnPropertyChanged(nameof(WindowTitle));
        Title = WindowTitle;

        _allGroups.Clear();
        FilteredGroups.Clear();
        _resolvedNavigationIndex.Clear();
        _totalRefs = 0;
        OnPropertyChanged(nameof(StatusText));
    }

    public void LoadDependenciesFromResult(DependencyResult result, string? displayName = null, FileIndex? fileIndex = null)
    {
        _currentEntryPath = result.EntryPath;
        _currentFileIndex = fileIndex;
        WindowTitle = $"Dependencies \u2014 {displayName ?? Path.GetFileName(result.EntryPath)}";
        OnPropertyChanged(nameof(WindowTitle));
        Title = WindowTitle;

        _allGroups.Clear();
        FilteredGroups.Clear();
        _resolvedNavigationIndex.Clear();

        var items = result.Groups.Select(g => new DependencyGroupItem(g)).ToList();
        _allGroups.AddRange(items);
        _totalRefs = _allGroups.Sum(g => g.ReferenceCount);
        RefreshFiltered();
        OnPropertyChanged(nameof(StatusText));
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

            // Skip external entries
            var entry = reference.Resolved[idx];
            var startIdx = idx;
            while (entry.IsExternal)
            {
                idx = (idx + 1) % reference.Resolved.Count;
                if (idx == startIdx) return; // all external, nothing to navigate to
                entry = reference.Resolved[idx];
            }

            // SoundsetName -> bank file: navigate to the FMOD event instead
            if (reference.Type == DependencyRefType.SoundsetName &&
                entry.FileName.EndsWith(".bank", StringComparison.OrdinalIgnoreCase))
            {
                await NavigateToFmodEventBySoundsetName(entry, reference.RawValue);
            }
            else
            {
                await NavigateToIndexEntry(entry);

                // Highlight the reference value in the previewed document
                if (reference.Type is DependencyRefType.StringKey or DependencyRefType.SoundsetName)
                    await _owner.HighlightTextInPreviewAsync(reference.RawValue);
                else if (reference.Type == DependencyRefType.FilePath && reference.SourceTag == "sound")
                    await _owner.HighlightTextInPreviewAsync(reference.RawValue);
            }

            _resolvedNavigationIndex[reference] = idx + 1;
        }
        finally
        {
            _navigationInProgress = false;
        }
    }

    async Task NavigateToFmodEventBySoundsetName(FileIndexEntry bankEntry, string soundsetName)
    {
        // Find the bank root file path
        var bankRelPath = bankEntry.FullRelativePath;
        var rootRelevantPath = _owner.RootFileRootPath;
        if (rootRelevantPath != "-" && bankRelPath.StartsWith(rootRelevantPath, StringComparison.OrdinalIgnoreCase))
            bankRelPath = bankRelPath[rootRelevantPath.Length..];

        // Navigate to bank, then find FMOD event by soundset name (= last segment of event path)
        _owner.NavigateToRootFile(bankRelPath);
        await Task.Delay(50);

        var fmodEvent = _owner.FmodBank?.Events?.FirstOrDefault(
            ev => ev.Path.EndsWith("/" + soundsetName, StringComparison.OrdinalIgnoreCase));
        if (fmodEvent != null)
            _owner.SelectedBankEntry = fmodEvent;
    }

    async void NavigateToGroup_Click(object? sender, RoutedEventArgs e)
    {
        if (_navigationInProgress) return;

        var group = (sender as Button)?.DataContext as DependencyGroupItem;
        if (group == null) return;

        _navigationInProgress = true;
        try
        {
            // Find the source file in the file index
            var sourceEntries = _currentFileIndex?.Find(Path.GetFileName(_currentEntryPath));
            if (sourceEntries != null && sourceEntries.Count > 0)
            {
                await NavigateToIndexEntry(sourceEntries[0]);

                // For named groups, highlight the entity name in the preview
                if (group.Group.EntityName != null)
                    await _owner.HighlightTextInPreviewAsync(group.Group.EntityName);
            }
        }
        finally
        {
            _navigationInProgress = false;
        }
    }

    void ShowGraph_Click(object? sender, RoutedEventArgs e)
    {
        var group = (sender as Button)?.DataContext as DependencyGroupItem;
        if (group == null || !group.CanShowGraph) return;

        DependencyGraphWindow.ShowForGroup(group, _currentFileIndex, _owner);
    }

    async Task NavigateToIndexEntry(FileIndexEntry entry)
    {
        var entryRelPath = entry.EntryRelativePath;
        if (entry.Source == FileIndexSource.BarEntry && entry.BarFilePath != null && entryRelPath.Length > 0)
        {
            // Navigate to a BAR file entry
            var barRelPath = entry.BarFilePath;
            if (Directory.Exists(_owner.RootDirectory) && barRelPath.StartsWith(_owner.RootDirectory))
                barRelPath = Path.GetRelativePath(_owner.RootDirectory, barRelPath);

            await _owner.NavigateToBarEntryAsync(barRelPath, entryRelPath.ToString());
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
