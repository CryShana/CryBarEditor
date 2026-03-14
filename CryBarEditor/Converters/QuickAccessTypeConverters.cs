using Avalonia.Data.Converters;
using Avalonia.Media;

using CryBarEditor.Classes;

using Material.Icons;

using System;
using System.Globalization;

namespace CryBarEditor.Converters;

public class QuickAccessTypeToIconKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is QuickAccessEntryType type ? type switch
        {
            QuickAccessEntryType.BarEntry => MaterialIconKind.ArchiveOutline,
            QuickAccessEntryType.FmodEvent => MaterialIconKind.MusicNote,
            _ => MaterialIconKind.FileOutline
        } : MaterialIconKind.FileOutline;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class QuickAccessTypeToForegroundConverter : IValueConverter
{
    static readonly SolidColorBrush DefaultBrush = new(Color.Parse("#d9d9d9"));
    static readonly SolidColorBrush AccentBrush = new(Color.Parse("#6f96bf"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is QuickAccessEntryType.RootFile ? DefaultBrush : AccentBrush;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
