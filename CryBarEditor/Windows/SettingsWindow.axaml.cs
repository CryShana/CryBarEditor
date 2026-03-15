using Avalonia.Controls;
using CryBarEditor.Classes;

namespace CryBarEditor;

public partial class SettingsWindow : SimpleWindow
{
    string _editorCommand = "";
    string _stringTableLanguage = "";

    public string EditorCommand { get => _editorCommand; set { _editorCommand = value; OnSelfChanged(); } }
    public string StringTableLanguage { get => _stringTableLanguage; set { _stringTableLanguage = value; OnSelfChanged(); } }
    public bool Confirmed { get; private set; }

    public SettingsWindow()
    {
        DataContext = this;
        InitializeComponent();
    }

    public SettingsWindow(string? editorCommand, string? stringTableLanguage) : this()
    {
        EditorCommand = editorCommand ?? "";
        StringTableLanguage = stringTableLanguage ?? "";
    }

    void OKClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (PlatformImpl == null) return;
        Confirmed = true;
        Close();
    }

    void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (PlatformImpl == null) return;
        Close();
    }
}
