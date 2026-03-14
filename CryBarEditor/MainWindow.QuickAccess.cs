using CryBarEditor.Classes;

using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CryBarEditor;

public partial class MainWindow
{
    List<QuickAccessEntry> _quickAccessEntries = new();
    QuickAccessWindow? _quickAccessWindow;

    public bool IsInQuickAccess
    {
        get
        {
            var key = GetCurrentQuickAccessKey();
            return key != null && _quickAccessEntries.Any(e => e.GetKey() == key);
        }
    }

    public string QuickAccessToggleText => IsInQuickAccess ? "Remove from Quick Access" : "Add to Quick Access";

    public string FmodQuickAccessToggleText
    {
        get
        {
            if (SelectedBankEntry == null) return "Add to Quick Access";
            var bankRelPath = GetBankRelativePath();
            if (bankRelPath == null) return "Add to Quick Access";
            var key = QuickAccessEntry.FromFmodEvent(SelectedBankEntry, bankRelPath).GetKey();
            return _quickAccessEntries.Any(e => e.GetKey() == key) ? "Remove from Quick Access" : "Add to Quick Access";
        }
    }

    string? GetCurrentQuickAccessKey()
    {
        if (SelectedBarEntry != null && _barStream != null)
        {
            var barRelPath = GetBarRelativePath();
            if (barRelPath == null) return null;
            return QuickAccessEntry.FromBarEntry(SelectedBarEntry, barRelPath).GetKey();
        }

        if (SelectedRootFileEntry != null)
            return QuickAccessEntry.FromRootFile(SelectedRootFileEntry).GetKey();

        return null;
    }

    string? GetBarRelativePath()
    {
        if (_barStream == null || !Directory.Exists(_rootDirectory))
            return null;
        if (!_barStream.Name.StartsWith(_rootDirectory))
            return null;
        return Path.GetRelativePath(_rootDirectory, _barStream.Name);
    }

    string? GetBankRelativePath()
    {
        if (_fmodBank == null || !Directory.Exists(_rootDirectory))
            return null;
        if (!_fmodBank.BankPath.StartsWith(_rootDirectory))
            return null;
        return Path.GetRelativePath(_rootDirectory, _fmodBank.BankPath);
    }

    void MenuItem_ToggleQuickAccess(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        if (IsContextFromBAR(list))
        {
            if (SelectedBarEntry == null) return;
            var barRelPath = GetBarRelativePath();
            if (barRelPath == null) return;

            var entry = QuickAccessEntry.FromBarEntry(SelectedBarEntry, barRelPath);
            ToggleQuickAccessEntry(entry);
        }
        else
        {
            if (SelectedRootFileEntry == null) return;
            var entry = QuickAccessEntry.FromRootFile(SelectedRootFileEntry);
            ToggleQuickAccessEntry(entry);
        }
    }

    void MenuItem_ToggleQuickAccessFmod(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedBankEntry == null) return;
        var bankRelPath = GetBankRelativePath();
        if (bankRelPath == null) return;

        var entry = QuickAccessEntry.FromFmodEvent(SelectedBankEntry, bankRelPath);
        ToggleQuickAccessEntry(entry);
    }

    void ToggleQuickAccessEntry(QuickAccessEntry newEntry)
    {
        var key = newEntry.GetKey();
        var existing = _quickAccessEntries.FindIndex(e => e.GetKey() == key);
        if (existing >= 0)
        {
            _quickAccessEntries.RemoveAt(existing);
        }
        else
        {
            _quickAccessEntries.Add(newEntry);
        }

        OnPropertyChanged(nameof(IsInQuickAccess));
        OnPropertyChanged(nameof(QuickAccessToggleText));
        OnPropertyChanged(nameof(FmodQuickAccessToggleText));
        SaveConfiguration();

        // Refresh the quick access window if open
        _quickAccessWindow?.RefreshFromSource();
    }

    void QuickAccess_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_quickAccessWindow != null)
        {
            _quickAccessWindow.Focus();
            return;
        }

        _quickAccessWindow = new QuickAccessWindow(this, _quickAccessEntries);
        _quickAccessWindow.Closed += (_, _) =>
        {
            _quickAccessWindow = null;
        };

        _quickAccessWindow.Show(this);
    }
}
