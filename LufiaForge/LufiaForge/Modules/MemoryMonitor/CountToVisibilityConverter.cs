using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LufiaForge.Modules.MemoryMonitor;

// Visible when count > 0, otherwise Collapsed
public class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
