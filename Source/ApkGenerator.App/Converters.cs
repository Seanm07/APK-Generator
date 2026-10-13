using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ApkGenerator.App.ViewModels;

namespace ApkGenerator.App;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var b = value is true;
        return b != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Visible when the value is a non-empty string / non-null object.</summary>
public sealed class HasValueToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var has = value switch { null => false, string s => s.Length > 0, _ => true };
        return has != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Segoe MDL2 Assets glyphs used around the UI.</summary>
public static class Glyph
{
    public const string Check = "";
    public const string Cancel = "";
    public const string Info = "";
    public const string Sync = "";
    public const string Clock = "";
}

public sealed class StatusKindConverter : IValueConverter
{
    /// <summary>ConverterParameter: "brush" or "glyph".</summary>
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var kind = value is StatusKind k ? k : StatusKind.Idle;
        if ((string?)parameter == "glyph")
            return kind switch
            {
                StatusKind.Success => Glyph.Check,
                StatusKind.Error => Glyph.Cancel,
                StatusKind.Working => Glyph.Sync,
                _ => Glyph.Info,
            };

        var key = kind switch
        {
            StatusKind.Success => "SuccessBrush",
            StatusKind.Error => "ErrorBrush",
            StatusKind.Working => "AccentBrush",
            _ => "MutedBrush",
        };
        return (Brush)Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class RowStateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var state = value is RowState s ? s : RowState.Waiting;
        if ((string?)parameter == "glyph")
            return state switch
            {
                RowState.Success => Glyph.Check,
                RowState.Failed => Glyph.Cancel,
                RowState.Working => Glyph.Sync,
                _ => Glyph.Clock,
            };

        var key = state switch
        {
            RowState.Success => "SuccessBrush",
            RowState.Failed => "ErrorBrush",
            RowState.Working => "AccentBrush",
            _ => "MutedBrush",
        };
        return (Brush)Application.Current.FindResource(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
