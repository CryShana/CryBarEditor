using Avalonia;
using Avalonia.Controls;

using CryBar;

using CryBarEditor.Classes;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.IO;

using Image = SixLabors.ImageSharp.Image;

namespace CryBarEditor;

public partial class DDTCreateDialogue : SimpleWindow
{
    bool _busy;

    public bool Busy { get => _busy; set { _busy = value; OnSelfChanged(); } }

    public string InputFile { get; } = "";
    public string OutputFile { get; } = "";

    public string InputFileShort => Path.GetFileName(InputFile);
    public string OutputFileShort => Path.GetFileName(OutputFile);

    readonly Image<Rgba32>? _image;

    static int _lastIndexVersion = 1;
    static int _lastIndexUsage = 0;
    static int _lastIndexAlpha = 0;
    static int _lastIndexFormat = 4;

    public DDTCreateDialogue()
    {
        DataContext = this;
        InitializeComponent();

        Closing += OnClosing;
    }

    void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        _image?.Dispose();
    }

    public DDTCreateDialogue(string in_file, string out_file) : this()
    {
        InputFile = in_file;
        OutputFile = out_file;
        OnPropertyChanged(nameof(InputFileShort));
        OnPropertyChanged(nameof(OutputFileShort));

        try
        {
            var data = File.ReadAllBytes(in_file);
            _image = Image.Load<Rgba32>(data);

            var mipmaps = DDTImage.GetMaxMinmapLevels(_image.Width, _image.Height);

            _txtMipmapNumber.Minimum = 1;
            _txtMipmapNumber.Maximum = mipmaps;
            _txtMipmapNumber.Value = mipmaps;
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to load image: " + ex.Message);
        }


        // load last used settings
        _comboVersion.SelectedIndex = _lastIndexVersion;
        _comboUsage.SelectedIndex = _lastIndexUsage;
        _comboAlpha.SelectedIndex = _lastIndexAlpha;
        _comboFormat.SelectedIndex = _lastIndexFormat;
    }

    void CloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }

    async void CreateDDTClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_image == null)
        {
            Close();
            return;
        }

        Busy = true;

        try
        {
            _lastIndexVersion = _comboVersion.SelectedIndex;
            _lastIndexUsage = _comboUsage.SelectedIndex;
            _lastIndexAlpha = _comboAlpha.SelectedIndex;
            _lastIndexFormat = _comboFormat.SelectedIndex;

            var version = (DDTVersion)_lastIndexVersion;
            var usage = (DDTUsage)_lastIndexUsage;
            var alpha = (DDTAlpha)_lastIndexAlpha;
            var format = (DDTFormat)_lastIndexFormat;
            byte mipmaps = (byte)_txtMipmapNumber.Value!;

            var data = await DDTImage.EncodeImageToDDT(_image, version, usage, alpha, format, mipmaps);

            using (var out_file = File.Create(OutputFile))
                out_file.Write(data.Span);

            Close();
        }
        catch (Exception ex)
        {
            // TODO: display error somewhere
        }
        finally
        {
            Busy = false;
        }
    }
}