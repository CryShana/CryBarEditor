using Avalonia.Data.Converters;

using CryBar;

using CryBarEditor.Classes;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;


namespace CryBarEditor.Converters;

public class IsOverridenToBoolConverter : IMultiValueConverter
{
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count != 2) 
            return false;

        var main_window = values[1] as MainWindow;
        if (main_window == null) 
            return false;

        var value = values[0];
        if (value == null) 
            return false;

        if (!main_window.CanExport)
            return false;

        string relative_path = "";
        if (value is RootFileEntry root_value)
        {
            relative_path = main_window.GetRootFullRelativePath(root_value);
        }
        else if (value is BarFileEntry bar_value)
        {
            relative_path = main_window.GetBARFullRelativePath(bar_value);
        }

        return main_window.IsFileOverriden(relative_path);
    }
}
