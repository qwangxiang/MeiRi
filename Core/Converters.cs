using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MeiRi;

/// <summary>true → Visible. Pass "invert" as ConverterParameter to flip.</summary>
public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool v = value is true;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) v = !v;
        return v ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Day density level (0-4, git-contribution style) → cell tint.
/// The palette is GitHub's, lightened so that the day number stays readable.
/// </summary>
public sealed class LevelBrushConverter : IValueConverter
{
    private static readonly Dictionary<int, Brush> Cache = new()
    {
        [0] = Brushes.Transparent,
        [1] = Frozen("#C9EDD5"),
        [2] = Frozen("#A3E0B7"),
        [3] = Frozen("#63C888"),
        [4] = Frozen("#2FA968")
    };

    private static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int level = value is int i ? i : 0;
        return Cache.TryGetValue(level, out var brush) ? brush : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Level 3-4 cells get white text, everything else the normal ink colour.</summary>
public sealed class LevelInkConverter : IValueConverter
{
    private static readonly Brush Light = Frozen("#FFFFFF");
    private static readonly Brush Dark = Frozen("#242731");

    private static Brush Frozen(string color)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        b.Freeze();
        return b;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i >= 4 ? Light : Dark;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
