using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LufiaForge.Modules.MemoryMonitor;

/// <summary>
/// Returns Visible when count > 0, Collapsed otherwise.
/// </summary>
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
