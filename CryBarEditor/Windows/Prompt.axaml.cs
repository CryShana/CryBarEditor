using Avalonia;
using Avalonia.Controls;

using CryBarEditor.Classes;

using System;
using System.Diagnostics;
using System.IO;

namespace CryBarEditor;

public partial class Prompt : SimpleWindow
{
    string _title = "Prompt title";
    string _text = "Prompt text";
    bool _canClose = true;
    PromptType _type = PromptType.Information;
    Progress<string?>? _progressReporter;
    bool _progressFinished = false;
    bool _showSuccess = false;
    bool _showProgress = false;
    string? _linkUrl;
    string? _openFolderPath;

    public string PromptText { get => _text; set { _text = value; OnSelfChanged(); } }
    public string PromptTitle { get => _title; set { _title = value; Title = value; OnSelfChanged(); } }
    public bool CanClose { get => _canClose; set { _canClose = value; OnSelfChanged(); } }
    public bool ProgressFinished { get => _progressFinished; set { _progressFinished = value; OnSelfChanged(); } }
    public string? LinkUrl { get => _linkUrl; set { _linkUrl = value; OnSelfChanged(); OnPropertyChanged(nameof(HasLink)); } }
    public string? OpenFolderPath { get => _openFolderPath; set { _openFolderPath = value; OnSelfChanged(); OnPropertyChanged(nameof(HasOpenFolder)); } }

    public bool HasLink => _linkUrl != null;
    public bool HasOpenFolder => _openFolderPath != null;

    public bool PromptIsError => _type == PromptType.Error;
    public bool PromptIsSuccess => _type == PromptType.Success;
    public bool PromptIsInformation => _type == PromptType.Information;
    public bool PromptIsProgress => _type == PromptType.Progress;

    public bool ShowProgressIcon { get => _showProgress; set { _showProgress = value; OnSelfChanged(); } }
    public bool ShowSuccessIcon { get => _showSuccess; set { _showSuccess = value; OnSelfChanged(); } }

    public Prompt()
    {
        DataContext = this;
        InitializeComponent();
    }

    public Prompt(PromptType type, string title, string text = "", Progress<string?>? progress_reporter = null) : this()
    {
        _type = type;
        PromptText = text;
        PromptTitle = title;
        _progressReporter = progress_reporter;
        OnPropertyChanged(nameof(PromptIsError));
        OnPropertyChanged(nameof(PromptIsSuccess));
        OnPropertyChanged(nameof(PromptIsInformation));
        OnPropertyChanged(nameof(PromptIsProgress));

        if (type == PromptType.Success) 
            ShowSuccessIcon = true;
        
        if (_progressReporter != null)
        {
            CanClose = false;
            ProgressFinished = false;
            ShowProgressIcon = true;
            _progressReporter.ProgressChanged += ProgressChanged;
        }
    }

    void ProgressChanged(object? sender, string? text)
    {
        if (text == null)
        {
            // finished
            CanClose = true;
            ProgressFinished = true;
            ShowProgressIcon = false;
            ShowSuccessIcon = true;

            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 100;
            progressBar.IsIndeterminate = false;
            return;
        }

        PromptText = text;
    }

    void CloseButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!CanClose)
            return;

        Close();
    }

    void OpenLink_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_linkUrl == null) return;
        try { Process.Start(new ProcessStartInfo(_linkUrl) { UseShellExecute = true }); }
        catch { }
    }

    void OpenFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_openFolderPath == null) return;
        var dir = Directory.Exists(_openFolderPath) ? _openFolderPath : Path.GetDirectoryName(_openFolderPath);
        if (dir == null) return;
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
        catch { }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_progressReporter != null)
        {
            _progressReporter.ProgressChanged -= ProgressChanged;
        }
    }
}

public enum PromptType
{
    Information,
    Error,
    Success,
    Progress
}