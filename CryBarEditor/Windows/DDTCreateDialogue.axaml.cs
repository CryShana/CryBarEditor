using Avalonia;
using Avalonia.Controls;

using CryBar;
using CryBar.Export;

using CryBarEditor.Classes;

using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

using System;
using System.IO;

using Image = SixLabors.ImageSharp.Image;

namespace CryBarEditor.Windows;

public partial class DDTCreateDialogue : SimpleWindow
{
    bool _busy;
    string? _errorMessage;

    public bool Busy { get => _busy; set { _busy = value; OnSelfChanged(); } }
    public string? ErrorMessage { get => _errorMessage; set { _errorMessage = value; OnSelfChanged(); } }

    public string InputFile { get; } = "";
    public string OutputFile { get; } = "";
    public string MaterialName { get; private set; } = "";

    public string InputFileShort => Path.GetFileName(InputFile);
    public string OutputFileShort => Path.GetFileName(OutputFile);

    public bool ParamsOnlyMode { get; private set; }
    public bool FileMode => !ParamsOnlyMode;

    readonly Image<Rgba32>? _image;

    public sealed record Result(GlbConverter.DdtMaterialParams Params, bool ApplyToAll);
    Result? _pickedResult;
    public Result? PickedResult => _pickedResult;

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

            InitMipmapsFromImage();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to load image: " + ex.Message);
        }

        LoadLastUsedIndices();
    }

    public DDTCreateDialogue(string materialName, byte[] pngBytes) : this()
    {
        ParamsOnlyMode = true;
        MaterialName = materialName;
        OnPropertyChanged(nameof(MaterialName));
        OnPropertyChanged(nameof(ParamsOnlyMode));
        OnPropertyChanged(nameof(FileMode));

        try
        {
            _image = Image.Load<Rgba32>(pngBytes);
            InitMipmapsFromImage();
        }
        catch (Exception ex)
        {
            throw new Exception("Failed to load image: " + ex.Message);
        }

        LoadLastUsedIndices();
    }

    void InitMipmapsFromImage()
    {
        var mipmaps = DDTImage.GetMaxMinmapLevels(_image!.Width, _image.Height);
        _txtMipmapNumber.Minimum = 1;
        _txtMipmapNumber.Maximum = mipmaps;
        _txtMipmapNumber.Value = mipmaps;
    }

    void LoadLastUsedIndices()
    {
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

        ErrorMessage = null;
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
            byte mipmaps = (byte)(_txtMipmapNumber.Value ?? _txtMipmapNumber.Maximum);

            if (ParamsOnlyMode)
            {
                _pickedResult = new Result(
                    new GlbConverter.DdtMaterialParams(version, usage, alpha, format, mipmaps, null),
                    _chkApplyToAll.IsChecked ?? false);
                Close();
                return;
            }

            var data = await DDTImage.EncodeImageToDDT(_image, version, usage, alpha, format, mipmaps);

            using (var out_file = File.Create(OutputFile))
                out_file.Write(data.Span);

            Close();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Failed to create DDT: " + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }
}