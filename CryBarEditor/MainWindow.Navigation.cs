using CryBar.Bar;
using CryBar.Indexing;
using CryBarEditor.Classes;

using System;
using System.Linq;
using System.Threading.Tasks;

namespace CryBarEditor;

public partial class MainWindow
{
    /// <summary>
    /// Waits for the preview document to finish loading, then searches for <paramref name="text"/>
    /// and scrolls to + selects the first occurrence.
    /// </summary>
    public async Task HighlightTextInPreviewAsync(string text)
    {
        await _docReadyTask;
        await Task.Delay(100);

        var content = _txtEditor.Document.Text;
        if (string.IsNullOrEmpty(content)) return;

        var index = content.IndexOf(text, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;

        _scrollVersion++;
        var location = _txtEditor.Document.GetLocation(index);
        _txtEditor.ScrollTo(location.Line, location.Column);
        _txtEditor.Select(index, text.Length);
    }

    /// <summary>
    /// Navigates to a root file entry by its relative path.
    /// Clears the filter if the entry is hidden. Returns null if not found.
    /// </summary>
    public RootFileEntry? NavigateToRootFile(string rootRelativePath)
    {
        var target = FindAndRevealRootFile(rootRelativePath);
        if (target == null) return null;
        SelectedRootFileEntry = target;
        return target;
    }

    /// <summary>
    /// Navigates to a BAR entry within a BAR archive.
    /// First navigates to the BAR archive root file, waits for it to load, then selects the entry.
    /// Returns null if not found.
    /// </summary>
    public async Task<BarFileEntry?> NavigateToBarEntryAsync(string barArchivePath, string entryRelativePath)
    {
        var barTarget = FindAndRevealRootFile(barArchivePath);
        if (barTarget == null) return null;
        SelectedRootFileEntry = barTarget;
        await Task.Delay(50);

        var barEntry = FindAndRevealBarFileEntry(entryRelativePath);
        if (barEntry == null) return null;
        SelectedBarEntry = barEntry;
        return barEntry;
    }

    /// <summary>
    /// Navigates to an FMOD event within a bank file.
    /// First navigates to the bank root file, waits for it to load, then selects the event.
    /// Returns null if not found.
    /// </summary>
    public async Task<FMODEvent?> NavigateToFmodEventAsync(string bankPath, string eventPath)
    {
        var bankTarget = FindAndRevealRootFile(bankPath);
        if (bankTarget == null) return null;
        SelectedRootFileEntry = bankTarget;
        await Task.Delay(50);

        var fmodEvent = FmodBank?.Events?.FirstOrDefault(e => e.Path == eventPath);
        if (fmodEvent == null) return null;
        SelectedBankEntry = fmodEvent;
        return fmodEvent;
    }
}
