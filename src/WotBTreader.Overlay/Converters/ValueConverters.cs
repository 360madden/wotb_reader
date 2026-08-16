using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WotBTreader.Overlay.Converters;

/// <summary>
/// Returns <see cref="Visibility.Visible"/> when the bound value is non-null,
/// <see cref="Visibility.Collapsed"/> when null.
/// </summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Maps team number (int) to a <see cref="Color"/> for the participant dot:
/// 1 → ally blue (matches the HUD nameplate palette), 2 → enemy red, other → Gray.
/// </summary>
public sealed class TeamToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int team = value is int i ? i : 0;
        return team switch
        {
            1 => Color.FromRgb(0x4F, 0xA8, 0xFF),
            2 => Color.FromRgb(0xFF, 0x6B, 0x6B),
            _ => Colors.Gray,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
