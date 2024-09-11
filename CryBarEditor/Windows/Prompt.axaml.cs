using Avalonia;
using Avalonia.Controls;

using CryBarEditor.Classes;

namespace CryBarEditor;

public partial class Prompt : SimpleWindow
{
    string _title = "Prompt title";
    string _text = "Prompt text";
    bool _canClose = true;
    PromptType _type = PromptType.Information;

    public string PromptText { get => _text; set { _text = value; OnSelfChanged(); } }
    public string PromptTitle { get => _title; set { _title = value; Title = value; OnSelfChanged(); } }
    public bool CanClose { get => _canClose; set { _canClose = value; OnSelfChanged(); } }

    public bool PromptIsError => _type == PromptType.Error;
    public bool PromptIsSuccess => _type == PromptType.Success;
    public bool PromptIsInformation => _type == PromptType.Information;
    public bool PromptIsProgress => _type == PromptType.Progress;

    public Prompt()
    {
        DataContext = this;
        InitializeComponent();
    }

    public Prompt(PromptType type, string title, string text = "") : this()
    {
        _type = type;
        PromptText = text;
        PromptTitle = title;
        OnPropertyChanged(nameof(PromptIsError));
        OnPropertyChanged(nameof(PromptIsSuccess));
        OnPropertyChanged(nameof(PromptIsInformation));
        OnPropertyChanged(nameof(PromptIsProgress));
    }

    void CloseButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}

public enum PromptType
{
    Information,
    Error,
    Success,
    Progress
}