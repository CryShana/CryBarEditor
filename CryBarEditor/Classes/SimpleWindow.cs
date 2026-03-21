using Avalonia.Controls;
using Avalonia.Interactivity;

using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace CryBarEditor.Classes;

public abstract class SimpleWindow : Window, INotifyPropertyChanged
{
    static readonly WindowIcon? _appIcon = LoadAppIcon();

    static WindowIcon? LoadAppIcon()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("crybar.ico");
        return stream != null ? new WindowIcon(stream) : null;
    }

    public SimpleWindow()
    {
        if (_appIcon != null) Icon = _appIcon;
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void OnSelfChanged([CallerMemberName] string propertyName = "") => OnPropertyChanged(propertyName);

    /// <summary>
    /// Clears the sibling TextBox in a Grid containing a filter clear button.
    /// </summary>
    protected static void FilterClear_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Parent is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is TextBox textBox)
                {
                    textBox.Text = "";
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Toggles the visibility of a sibling clear button based on TextBox content.
    /// </summary>
    protected static void FilterTextBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox textBox && textBox.Parent is Grid grid)
        {
            foreach (var child in grid.Children)
            {
                if (child is Button btn && btn.Classes.Contains("filterClear"))
                {
                    btn.IsVisible = !string.IsNullOrEmpty(textBox.Text);
                    break;
                }
            }
        }
    }
}
