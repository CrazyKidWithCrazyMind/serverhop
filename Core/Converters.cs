using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ServerHop.Core;

/// <summary>True when the bound int equals the ConverterParameter. TwoWay: writes the
/// parameter back when checked, returns Binding.DoNothing when unchecked.</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int v && parameter?.ToString() is string s &&
            int.TryParse(s, NumberStyles.Integer, culture, out var p))
            return v == p;
        return false;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter?.ToString() is string s &&
            int.TryParse(s, NumberStyles.Integer, culture, out var p))
            return p;
        return Binding.DoNothing;
    }
}

/// <summary>bool → Visibility (with optional inversion).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
