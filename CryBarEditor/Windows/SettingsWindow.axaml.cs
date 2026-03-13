using Avalonia.Controls;
using CryBarEditor.Classes;

namespace CryBarEditor;

public partial class SettingsWindow : SimpleWindow
{
    string _editorCommand = "";

    public string EditorCommand { get => _editorCommand; set { _editorCommand = value; OnSelfChanged(); } }
    public bool Confirmed { get; private set; }

    public SettingsWindow()
    {
        DataContext = this;
        InitializeComponent();
    }

    public SettingsWindow(string? editorCommand) : this()
    {
        EditorCommand = editorCommand ?? "";
    }

    void OKClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
