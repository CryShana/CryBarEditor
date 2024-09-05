using CryBar;
using System.IO;
using Avalonia.Controls;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using System;
using System.Collections.ObjectModel;
using CryBarEditor.Classes;

namespace CryBarEditor;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    BarFile? _barFile = null;
    FileStream? _barStream = null;

    public ObservableCollectionExtended<BarFileEntry> Entries { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
    }

    async void Load_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn != null) 
            btn.IsEnabled = false;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open BAR file",
            AllowMultiple = false,
            FileTypeFilter = [ new("BAR file") { Patterns = [ "*.bar"] } ]
        });

        if (btn != null)
            btn.IsEnabled = true;

        if (files.Count == 0)
            return;

        Load(files[0].Path.AbsolutePath);
    }

    public void Load(string bar_file)
    {
        _barStream?.Dispose();

        var stream = File.OpenRead(bar_file);
        
        try
        {
            var file = new BarFile(stream);
            if (!file.Load())
            {
                throw new Exception("Failed to load BAR file, possibly invalid of unsupported format");
            }

            _barStream = stream;
            _barFile = file;
            Refresh();
        }
        catch (Exception ex)
        {
            _barStream = null;

            // TODO: show error
        }
    }

    public void Refresh()
    {
        Entries.Clear();
        if (_barFile?.Entries == null)
            return;

        Entries.AddItems(_barFile.Entries);
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}