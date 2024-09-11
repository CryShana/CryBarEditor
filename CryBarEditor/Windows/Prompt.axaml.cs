using Avalonia;
using Avalonia.Controls;

using CryBarEditor.Classes;

using System;

namespace CryBarEditor;

public partial class Prompt : SimpleWindow
{
    string _title = "Prompt title";
    string _text = "Prompt text";
    bool _canClose = true;
    PromptType _type = PromptType.Information;
    Progress<string?>? _progressReporter;
    bool _progressFinished = false;

    public string PromptText { get => _text; set { _text = value; OnSelfChanged(); } }
    public string PromptTitle { get => _title; set { _title = value; Title = value; OnSelfChanged(); } }
    public bool CanClose { get => _canClose; set { _canClose = value; OnSelfChanged(); } }
    public bool ProgressFinished { get => _progressFinished; set { _progressFinished = value; OnSelfChanged(); } }


    public bool PromptIsError => _type == PromptType.Error;
    public bool PromptIsSuccess => _type == PromptType.Success;
    public bool PromptIsInformation => _type == PromptType.Information;
    public bool PromptIsProgress => _type == PromptType.Progress;

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

        if (_progressReporter != null)
        {
            CanClose = false;
            ProgressFinished = false;
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

            progressBar.Minimum = 0;
            progressBar.Maximum = 100;
            progressBar.Value = 100;
            progressBar.IsIndeterminate = false;
            return;
        }

        PromptText = text;
    }

    void CloseButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

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