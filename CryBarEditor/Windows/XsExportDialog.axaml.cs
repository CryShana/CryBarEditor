using Avalonia.Media;

using CryBarEditor.Classes;

namespace CryBarEditor;

public partial class XsExportDialog : SimpleWindow
{
    static readonly IBrush LossyHighlight = new SolidColorBrush(Color.Parse("#edc042"));
    static readonly IBrush LosslessHighlight = new SolidColorBrush(Color.Parse("#78f542"));
    static readonly IBrush TransparentBrush = Brushes.Transparent;

    bool _isLossy = true;
    bool _confirmed;
    bool _triggerDataMissing;

    public bool IsLossy
    {
        get => _isLossy;
        set
        {
            _isLossy = value;
            OnSelfChanged();
            OnPropertyChanged(nameof(IsLossless));
            OnPropertyChanged(nameof(ShowWarning));
            OnPropertyChanged(nameof(LossyBorderBrush));
            OnPropertyChanged(nameof(LosslessBorderBrush));
        }
    }

    public bool IsLossless
    {
        get => !_isLossy;
        set
        {
            _isLossy = !value;
            OnSelfChanged();
            OnPropertyChanged(nameof(IsLossy));
            OnPropertyChanged(nameof(ShowWarning));
            OnPropertyChanged(nameof(LossyBorderBrush));
            OnPropertyChanged(nameof(LosslessBorderBrush));
        }
    }

    public IBrush LossyBorderBrush => _isLossy ? LossyHighlight : TransparentBrush;
    public IBrush LosslessBorderBrush => !_isLossy ? LosslessHighlight : TransparentBrush;

    public bool ShowWarning => _triggerDataMissing && _isLossy;
    public string WarningText { get; }

    public XsExportDialog() : this(false) { }

    public XsExportDialog(bool triggerDataMissing)
    {
        _triggerDataMissing = triggerDataMissing;
        WarningText = triggerDataMissing
            ? "trigger_data.xml not found in root folder. XS to XML/TRG conversion may produce less accurate results (more raw code blocks instead of structured triggers)."
            : "";

        DataContext = this;
        InitializeComponent();
    }

    /// <summary>
    /// Returns true for lossless, false for lossy, or null if cancelled.
    /// </summary>
    public bool? GetResult() => _confirmed ? IsLossless : null;

    void ExportClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _confirmed = true;
        Close();
    }

    void CancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}
