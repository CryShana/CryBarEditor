using System;
using System.Linq;
using System.Threading.Tasks;

using Avalonia.Platform.Storage;

using CryBarEditor.Classes;

namespace CryBarEditor.Windows;

public partial class CaptureScreenshotWindow : SimpleWindow
{
    public record ScreenshotOptions(double Factor, bool Transparent, string Format);

    static readonly double[] _factorChoices = [0.5, 1, 2, 3, 4];

    public string[] FactorLabels { get; } = [.. _factorChoices.Select(f => $"{f}x")];
    public string[] FormatLabels { get; } = ["WEBP", "PNG"];

    readonly int _baseWidth;
    readonly int _baseHeight;
    readonly string _suggestedBaseName;
    readonly Func<ScreenshotOptions, IStorageFile, Task>? _saveAction;
    ScreenshotOptions? _result;

    int _factorIndex = 1;
    public int FactorIndex
    {
        get => _factorIndex;
        set { if (value < 0) return; _factorIndex = value; OnSelfChanged(); OnPropertyChanged(nameof(ResolutionText)); }
    }

    bool _transparent = true;
    public bool Transparent
    {
        get => _transparent;
        set { _transparent = value; OnSelfChanged(); }
    }

    int _formatIndex;
    public int FormatIndex
    {
        get => _formatIndex;
        set { _formatIndex = value; OnSelfChanged(); OnPropertyChanged(nameof(IsPngSelected)); }
    }

    bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnSelfChanged(); }
    }

    string _errorText = "";
    public string ErrorText
    {
        get => _errorText;
        set { _errorText = value; OnSelfChanged(); OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => _errorText.Length > 0;
    public bool IsPngSelected => _formatIndex == 1;
    public string ResolutionText
        => $"{(int)(_baseWidth * _factorChoices[_factorIndex])} x {(int)(_baseHeight * _factorChoices[_factorIndex])}";

    public CaptureScreenshotWindow() : this(1920, 1080, 1, true, "webp", "model", null) { }

    public CaptureScreenshotWindow(int baseWidth, int baseHeight, double factor, bool transparent, string format,
        string suggestedBaseName, Func<ScreenshotOptions, IStorageFile, Task>? saveAction)
    {
        _baseWidth = baseWidth;
        _baseHeight = baseHeight;
        _suggestedBaseName = suggestedBaseName;
        _saveAction = saveAction;

        int idx = Array.IndexOf(_factorChoices, factor);
        _factorIndex = idx >= 0 ? idx : 1;
        _transparent = transparent;
        _formatIndex = format == "png" ? 1 : 0;

        DataContext = this;
        InitializeComponent();
    }

    public ScreenshotOptions? GetResult() => _result;

    ScreenshotOptions BuildOptions()
        => new(_factorChoices[_factorIndex], _transparent, _formatIndex == 1 ? "png" : "webp");

    async void ConfirmClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isBusy || _saveAction == null) return;

        var options = BuildOptions();
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save screenshot",
            SuggestedFileName = _suggestedBaseName + "." + options.Format,
            DefaultExtension = options.Format,
            FileTypeChoices =
            [
                options.Format == "png"
                    ? new FilePickerFileType("PNG image") { Patterns = ["*.png"] }
                    : new FilePickerFileType("WEBP image") { Patterns = ["*.webp"] },
            ],
        });
        if (file == null) return;

        IsBusy = true;
        ErrorText = "";
        try
        {
            await _saveAction(options, file);
            _result = options;
            Close();
        }
        catch (Exception ex)
        {
            ErrorText = $"Failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_isBusy) return;
        Close();
    }
}
