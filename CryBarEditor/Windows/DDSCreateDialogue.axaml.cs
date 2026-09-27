using CryBar.Bar;
using CryBar.BCnEncoder.Shared;

using CryBarEditor.Classes;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.IO;

using Image = SixLabors.ImageSharp.Image;

namespace CryBarEditor.Windows;

public partial class DDSCreateDialogue : SimpleWindow
{
    bool _busy;
    string? _errorMessage;

    public bool Busy { get => _busy; set { _busy = value; OnSelfChanged(); } }
    public string? ErrorMessage { get => _errorMessage; set { _errorMessage = value; OnSelfChanged(); } }

    public string InputFile { get; } = "";
    public string OutputFile { get; } = "";
    public string OutputFileShort => Path.GetFileName(OutputFile);

    readonly Image<Rgba32>? _image;

    static int _lastIndexFormat = 2;
    static bool _lastSrgb;

    public DDSCreateDialogue()
    {
        DataContext = this;
        InitializeComponent();
        Closing += (_, _) => _image?.Dispose();
    }

    public DDSCreateDialogue(string in_file, string out_file) : this()
    {
        InputFile = in_file;
        OutputFile = out_file;
        OnPropertyChanged(nameof(OutputFileShort));

        try
        {
            var data = File.ReadAllBytes(in_file);
            _image = Image.Load<Rgba32>(data);
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to load image: " + ex.Message);
        }

        _comboFormat.SelectedIndex = _lastIndexFormat;
        _chkSrgb.IsChecked = _lastSrgb;
        var maxMips = CryBar.DDTImage.GetMaxMinmapLevels(_image.Width, _image.Height);
        _txtMipmapNumber.Maximum = maxMips;
        _txtMipmapNumber.Value = maxMips;
    }

    void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    async void CreateDDSClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_image == null) { Close(); return; }

        ErrorMessage = null;
        Busy = true;

        try
        {
            _lastIndexFormat = _comboFormat.SelectedIndex;
            _lastSrgb = _chkSrgb.IsChecked == true;

            var format = _lastIndexFormat switch
            {
                0 => CompressionFormat.Bc1,
                1 => CompressionFormat.Bc3,
                _ => CompressionFormat.Bc7
            };
            byte mipmaps = (byte)(_txtMipmapNumber.Value ?? _txtMipmapNumber.Maximum);

            var ddsBytes = await ConversionHelper.EncodeImageToDdsBytes(_image, format, _lastSrgb, mipmaps);
            await File.WriteAllBytesAsync(OutputFile, ddsBytes);
            Close();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to create DDS: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}
