using Avalonia.Controls;
using Avalonia.Interactivity;

using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace CryBarEditor.Windows;

public partial class QuickAccessWindow : SimpleWindow
{
    string _filter = "";
    QuickAccessEntry? _selectedEntry;

    readonly MainWindow _owner;
    readonly List<QuickAccessEntry> _allEntries;

    public string Filter
    {
        get => _filter;
        set { _filter = value; OnSelfChanged(); RefreshFiltered(); }
    }

    public QuickAccessEntry? SelectedEntry
    {
        get => _selectedEntry;
        set { _selectedEntry = value; OnSelfChanged(); }
    }

    public string StatusText => $"{FilteredEntries.Count} of {_allEntries.Count} items";
    public ObservableCollectionExtended<QuickAccessEntry> FilteredEntries { get; } = new();

    public QuickAccessWindow()
    {
        _owner = null!;
        _allEntries = new();
        DataContext = this;
        InitializeComponent();
    }

    public QuickAccessWindow(MainWindow owner, List<QuickAccessEntry> entries) : this()
    {
        _owner = owner;
        _allEntries = entries;
        ValidateEntries();
        RefreshFiltered();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Size to owner height
        if (Owner is Window ownerWindow)
        {
            Height = ownerWindow.Height;
        }
    }

    public void RefreshFromSource()
    {
        RefreshFiltered();
    }

    void RefreshFiltered()
    {
        FilteredEntries.Clear();
        var filter = _filter.Trim();
        foreach (var entry in _allEntries)
        {
            if (filter.Length > 0)
            {
                var fullPath = entry.DirectoryPath + entry.DisplayName;
                if (!fullPath.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    continue;
            }
            FilteredEntries.Add(entry);
        }
        OnPropertyChanged(nameof(StatusText));
    }

    void ValidateEntries()
    {
        var rootDir = _owner.RootDirectory;
        if (string.IsNullOrEmpty(rootDir) || !Directory.Exists(rootDir))
        {
            foreach (var entry in _allEntries)
                entry.IsValid = false;
            return;
        }

        foreach (var entry in _allEntries)
        {
            entry.IsValid = entry.EntryType switch
            {
                QuickAccessEntryType.RootFile =>
                    entry.RootRelativePath != null && File.Exists(Path.Combine(rootDir, entry.RootRelativePath)),
                QuickAccessEntryType.BarEntry =>
                    entry.BarArchivePath != null && File.Exists(Path.Combine(rootDir, entry.BarArchivePath)),
                QuickAccessEntryType.FmodEvent =>
                    entry.BankPath != null && File.Exists(Path.Combine(rootDir, entry.BankPath)),
                _ => false
            };
        }
    }

    QuickAccessEntry? GetEntryFromSender(object? sender)
    {
        if (sender is Button btn)
            return btn.DataContext as QuickAccessEntry;
        if (sender is MenuItem mi)
            return SelectedEntry;
        return null;
    }

    int GetSourceIndex(QuickAccessEntry entry) => _allEntries.IndexOf(entry);

    void SwapEntries(int indexA, int indexB)
    {
        if (indexA < 0 || indexB < 0 || indexA >= _allEntries.Count || indexB >= _allEntries.Count)
            return;
        (_allEntries[indexA], _allEntries[indexB]) = (_allEntries[indexB], _allEntries[indexA]);
        RefreshFiltered();
        _owner.SaveConfiguration();
    }

    void MoveUp_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;
        var idx = GetSourceIndex(entry);
        if (idx > 0) SwapEntries(idx, idx - 1);
    }

    void MoveDown_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;
        var idx = GetSourceIndex(entry);
        if (idx >= 0 && idx < _allEntries.Count - 1) SwapEntries(idx, idx + 1);
    }

    void MoveToTop_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;
        var idx = GetSourceIndex(entry);
        if (idx > 0)
        {
            _allEntries.RemoveAt(idx);
            _allEntries.Insert(0, entry);
            RefreshFiltered();
            _owner.SaveConfiguration();
        }
    }

    void MoveToBottom_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;
        var idx = GetSourceIndex(entry);
        if (idx >= 0 && idx < _allEntries.Count - 1)
        {
            _allEntries.RemoveAt(idx);
            _allEntries.Add(entry);
            RefreshFiltered();
            _owner.SaveConfiguration();
        }
    }

    void Remove_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromSender(sender);
        if (entry == null) return;
        _allEntries.Remove(entry);
        RefreshFiltered();
        _owner.SaveConfiguration();
    }

    bool _navigationInProgress;
    async void OpenEntry_Click(object? sender, RoutedEventArgs e)
    {
        var entry = GetEntryFromSender(sender);
        if (entry == null || _navigationInProgress) return;

        if (!entry.IsValid)
            return;

        _navigationInProgress = true;
        try
        {
            await NavigateToEntry(entry);
        }
        finally
        {
            _navigationInProgress = false;
        }
    }

    async Task NavigateToEntry(QuickAccessEntry entry)
    {
        switch (entry.EntryType)
        {
            case QuickAccessEntryType.RootFile:
            {
                if (entry.RootRelativePath == null) return;
                var target = _owner.NavigateToRootFile(entry.RootRelativePath);
                if (target == null)
                {
                    entry.IsValid = false;
                    RefreshFiltered();
                }
                break;
            }
            case QuickAccessEntryType.BarEntry:
            {
                if (entry.BarArchivePath == null || entry.EntryRelativePath == null) return;
                var barEntry = await _owner.NavigateToBarEntryAsync(entry.BarArchivePath, entry.EntryRelativePath);
                if (barEntry == null)
                {
                    entry.IsValid = false;
                    RefreshFiltered();
                }
                break;
            }
            case QuickAccessEntryType.FmodEvent:
            {
                if (entry.BankPath == null || entry.EventPath == null) return;
                var fmodEvent = await _owner.NavigateToFmodEventAsync(entry.BankPath, entry.EventPath);
                if (fmodEvent == null)
                {
                    entry.IsValid = false;
                    RefreshFiltered();
                }
                break;
            }
        }
    }

    async void ListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
    {
        if (_navigationInProgress || SelectedEntry == null || !SelectedEntry.IsValid) return;

        _navigationInProgress = true;
        try
        {
            await NavigateToEntry(SelectedEntry);
        }
        finally
        {
            _navigationInProgress = false;
        }
    }

}
