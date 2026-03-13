using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CryBar;
using CryBarEditor.Classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CryBarEditor;

public partial class MainWindow
{
    #region Context Menu Helpers
    /// <summary>
    /// Resolves the ListBox that owns this context menu item.
    /// </summary>
    ListBox? GetContextListBox(object? sender)
    {
        var item = sender as MenuItem;
        return item?.Parent?.Parent?.Parent as ListBox;
    }

    /// <summary>
    /// Returns whether the context menu was opened from the BAR entries list (vs Root files list).
    /// </summary>
    bool IsContextFromBAR(ListBox list) => list.ItemsSource == BarEntries;

    /// <summary>
    /// Gets the full relative path for the currently selected entry, regardless of which list it's in.
    /// Returns null if no valid selection exists.
    /// </summary>
    string? GetContextSelectedRelativePath(ListBox list)
    {
        if (IsContextFromBAR(list))
        {
            if (SelectedBarEntry == null) return null;
            return GetBARFullRelativePath(SelectedBarEntry);
        }
        else
        {
            if (SelectedRootFileEntry == null) return null;
            return GetRootFullRelativePath(SelectedRootFileEntry);
        }
    }

    /// <summary>
    /// Gets the display name of the currently selected entry.
    /// </summary>
    string? GetContextSelectedName(ListBox list)
    {
        if (IsContextFromBAR(list))
            return SelectedBarEntry?.Name;
        return SelectedRootFileEntry?.Name;
    }

    /// <summary>
    /// Builds ExportFileInfo list from the current selection in the given ListBox.
    /// </summary>
    List<ExportFileInfo> GetContextSelectedExportFiles(ListBox list)
    {
        if (IsContextFromBAR(list))
        {
            return SelectedBarFileEntries.Select(e => new ExportFileInfo
            {
                RelativePath = e.RelativePath,
                FullRelativePath = GetBARFullRelativePath(e),
                IsCompressed = e.IsCompressed
            }).ToList();
        }

        return SelectedRootFileEntries.Select(e => new ExportFileInfo
        {
            RelativePath = e.RelativePath,
            FullRelativePath = GetRootFullRelativePath(e),
            IsCompressed = false // root files don't have a compression flag
        }).ToList();
    }
    #endregion

    #region ContextMenu events
    void MenuItem_CopyFileName(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        var name = GetContextSelectedName(list);
        if (name != null) Clipboard?.SetTextAsync(name);
    }

    void MenuItem_CopyFilePath(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var list = GetContextListBox(sender);
        if (list == null) return;

        var path = GetContextSelectedRelativePath(list);
        if (path != null) Clipboard?.SetTextAsync(path);
    }

    void MenuItem_ExportSelectedOpenDirectory(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!Directory.Exists(_exportRootDirectory))
            return;

        var list = GetContextListBox(sender);
        if (list == null) return;

        var relative_path_full = GetContextSelectedRelativePath(list);
        if (relative_path_full == null) return;

        var export_path = Path.Combine(_exportRootDirectory, relative_path_full);
        var export_dir = Path.GetDirectoryName(export_path);
        if (!string.IsNullOrEmpty(export_dir))
            Directory.CreateDirectory(export_dir);

        var process_info = new ProcessStartInfo
        {
            UseShellExecute = true,
            FileName = $"explorer.exe",
            Arguments = $"\"{export_dir}\""
        };

        Process.Start(process_info);
    }

    CancellationTokenSource? bank_play_csc = null;
    void BankItem_Play(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (SelectedBankEntry == null || FmodBank == null)
            return;

        bank_play_csc?.Cancel();
        bank_play_csc = new();
        _ = SelectedBankEntry.Play(bank_play_csc.Token);
    }

    void ContextMenu_Opened(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var listbox = (ListBox)((ContextMenu)sender!).Parent!.Parent!;
        ContextSelectedItemsCount = listbox.SelectedItems!.Count;
    }
    #endregion
}
