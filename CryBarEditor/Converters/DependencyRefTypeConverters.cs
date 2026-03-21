using Avalonia.Data.Converters;
using Avalonia.Media;

using CryBar;
using CryBar.Dependencies;

using Material.Icons;

using System;
using System.Globalization;

namespace CryBarEditor.Converters;

public class DependencyRefTypeToIconKindConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DependencyRefType type ? type switch
        {
            DependencyRefType.FilePath => MaterialIconKind.FileOutline,
            DependencyRefType.StringKey => MaterialIconKind.FormatLetterCase,
            DependencyRefType.SoundsetName => MaterialIconKind.MusicNote,
            _ => MaterialIconKind.FileOutline
        } : MaterialIconKind.FileOutline;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class DependencyRefTypeToForegroundConverter : IValueConverter
{
    static readonly SolidColorBrush FilePathBrush = new(Color.Parse("#d9d9d9"));
    static readonly SolidColorBrush StringKeyBrush = new(Color.Parse("#c4a96a"));
    static readonly SolidColorBrush SoundsetBrush = new(Color.Parse("#6f96bf"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DependencyRefType type ? type switch
        {
            DependencyRefType.StringKey => StringKeyBrush,
            DependencyRefType.SoundsetName => SoundsetBrush,
            _ => FilePathBrush
        } : FilePathBrush;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
