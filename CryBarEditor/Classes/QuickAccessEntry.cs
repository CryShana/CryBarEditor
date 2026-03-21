using CryBar.Bar;

using System;
using System.IO;
using System.Text.Json.Serialization;

namespace CryBarEditor.Classes;

public enum QuickAccessEntryType
{
    RootFile = 0,
    BarEntry = 1,
    FmodEvent = 2
}

public class QuickAccessEntry
{
    public QuickAccessEntryType EntryType { get; set; }

    /// <summary>Display filename</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Directory portion shown in gray</summary>
    public string DirectoryPath { get; set; } = "";

    /// <summary>For RootFile: relative path within root directory</summary>
    public string? RootRelativePath { get; set; }

    /// <summary>For BarEntry: relative path of the BAR archive within root directory</summary>
    public string? BarArchivePath { get; set; }

    /// <summary>For BarEntry: relative path of the entry within the BAR archive</summary>
    public string? EntryRelativePath { get; set; }

    /// <summary>For FmodEvent: relative path of the bank file within root directory</summary>
    public string? BankPath { get; set; }

    /// <summary>For FmodEvent: event path within the bank</summary>
    public string? EventPath { get; set; }

    public DateTime DateAdded { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public bool IsValid { get; set; } = true;

    /// <summary>
    /// Returns a unique key for deduplication.
    /// </summary>
    public string GetKey() => EntryType switch
    {
        QuickAccessEntryType.RootFile => $"root:{RootRelativePath}",
        QuickAccessEntryType.BarEntry => $"bar:{BarArchivePath}|{EntryRelativePath}",
        QuickAccessEntryType.FmodEvent => $"fmod:{BankPath}|{EventPath}",
        _ => ""
    };

    public static QuickAccessEntry FromRootFile(RootFileEntry entry)
    {
        return new QuickAccessEntry
        {
            EntryType = QuickAccessEntryType.RootFile,
            DisplayName = entry.Name,
            DirectoryPath = entry.DirectoryPath,
            RootRelativePath = entry.RelativePath,
            DateAdded = DateTime.UtcNow
        };
    }

    public static QuickAccessEntry FromBarEntry(BarFileEntry barEntry, string barArchiveRelativePath)
    {
        return new QuickAccessEntry
        {
            EntryType = QuickAccessEntryType.BarEntry,
            DisplayName = barEntry.Name,
            DirectoryPath = Path.GetDirectoryName(barEntry.RelativePath)?.Replace('\\', '/') is { Length: > 0 } dir ? dir + "/" : "",
            BarArchivePath = barArchiveRelativePath,
            EntryRelativePath = barEntry.RelativePath,
            DateAdded = DateTime.UtcNow
        };
    }

    public static QuickAccessEntry FromFmodEvent(FMODEvent fmodEvent, string bankRelativePath)
    {
        var eventName = fmodEvent.Path;
        var lastSlash = eventName.LastIndexOf('/');
        return new QuickAccessEntry
        {
            EntryType = QuickAccessEntryType.FmodEvent,
            DisplayName = lastSlash >= 0 ? eventName[(lastSlash + 1)..] : eventName,
            DirectoryPath = lastSlash >= 0 ? eventName[..(lastSlash + 1)] : "",
            BankPath = bankRelativePath,
            EventPath = fmodEvent.Path,
            DateAdded = DateTime.UtcNow
        };
    }
}
