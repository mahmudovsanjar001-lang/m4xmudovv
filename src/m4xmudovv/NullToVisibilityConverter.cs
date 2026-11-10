using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace m4xmudovv;

/// <summary>Visible when the bound value is null or an empty/whitespace string; otherwise Collapsed.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isEmpty = value is null || (value is string text && string.IsNullOrWhiteSpace(text));
        return isEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
