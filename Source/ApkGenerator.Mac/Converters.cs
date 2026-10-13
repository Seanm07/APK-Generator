using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using ApkGenerator.Core.ViewModels;

namespace ApkGenerator.Mac;

/// <summary>Tiny converters used by the status area (state → icon name / colour).</summary>
public static class Conv
{
    private static readonly IBrush Success = new SolidColorBrush(Color.Parse("#2EA043"));
    private static readonly IBrush Error = new SolidColorBrush(Color.Parse("#E5484D"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#2F80ED"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#8C8C8C"));

    public static readonly IValueConverter StatusIcon = new Func<StatusKind, string>(k => k switch
    {
        StatusKind.Success => "check",
        StatusKind.Error => "x",
        StatusKind.Working => "refresh",
        _ => "info",
    }).AsConverter();

    public static readonly IValueConverter StatusBrush = new Func<StatusKind, IBrush>(k => k switch
    {
        StatusKind.Success => Success,
        StatusKind.Error => Error,
        StatusKind.Working => Accent,
        _ => Muted,
    }).AsConverter();

    public static readonly IValueConverter RowIcon = new Func<RowState, string>(s => s switch
    {
        RowState.Success => "check",
        RowState.Failed => "x",
        RowState.Working => "refresh",
        _ => "clock",
    }).AsConverter();

    public static readonly IValueConverter RowBrush = new Func<RowState, IBrush>(s => s switch
    {
        RowState.Success => Success,
        RowState.Failed => Error,
        RowState.Working => Accent,
        _ => Muted,
    }).AsConverter();

    private static IValueConverter AsConverter<TIn, TOut>(this Func<TIn, TOut> f) => new FuncConverter<TIn, TOut>(f);

    private sealed class FuncConverter<TIn, TOut>(Func<TIn, TOut> f) : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is TIn v ? f(v) : default(TOut);

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
