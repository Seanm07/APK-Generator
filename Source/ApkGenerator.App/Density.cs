using System.Windows;

namespace ApkGenerator.App;

/// <summary>
/// Swaps the spacing resources (App.xaml) between roomy and tight values, so a small window doesn't look bloated.
/// </summary>
internal static class Density
{
    private static bool? _compact;

    private static readonly (string Key, object Roomy, object Tight)[] Table =
    {
        ("PagePad", new Thickness(28, 20, 28, 14), new Thickness(10, 8, 10, 6)),
        ("DropHeight", 150.0, 96.0),
        ("DropIcon", 34.0, 22.0),
        ("DropTextGap", new Thickness(0, 8, 0, 0), new Thickness(0, 3, 0, 0)),
        ("DropBtnGap", new Thickness(0, 10, 0, 0), new Thickness(0, 5, 0, 0)),
        ("DropBtnPad", new Thickness(18, 6, 18, 6), new Thickness(12, 3, 12, 3)),
        ("DividerGap", new Thickness(0, 14, 0, 14), new Thickness(0, 8, 0, 8)),
        ("FactGapRB", new Thickness(0, 0, 16, 16), new Thickness(0, 0, 10, 8)),
        ("FactGapB", new Thickness(0, 0, 0, 16), new Thickness(0, 0, 0, 8)),
        ("FactGapR", new Thickness(0, 0, 16, 0), new Thickness(0, 0, 10, 0)),
        ("BlockGap", new Thickness(0, 16, 0, 0), new Thickness(0, 8, 0, 0)),
        ("SectionGap", new Thickness(0, 18, 0, 0), new Thickness(0, 8, 0, 0)),
        ("CheckGap", new Thickness(0, 0, 0, 10), new Thickness(0, 0, 0, 4)),
        ("LabelGap", new Thickness(0, 14, 0, 4), new Thickness(0, 8, 0, 3)),
        ("ActionPad", new Thickness(28, 2, 28, 12), new Thickness(10, 2, 10, 6)),
        ("BtnGap", new Thickness(0, 0, 10, 6), new Thickness(0, 0, 6, 4)),
        ("BtnPad", new Thickness(22, 9, 22, 9), new Thickness(12, 6, 12, 6)),
        ("StatusPad", new Thickness(28, 0, 28, 10), new Thickness(10, 0, 10, 6)),
        ("StatusInnerPad", new Thickness(16, 12, 16, 12), new Thickness(8, 6, 8, 6)),
        ("FooterPad", new Thickness(30, 0, 28, 10), new Thickness(12, 0, 10, 4)),
        ("CardPad", new Thickness(18), new Thickness(10)),
        ("TitleGap", new Thickness(0, 0, 0, 12), new Thickness(0, 0, 0, 6)),
    };

    public static void Apply(bool compact)
    {
        if (_compact == compact) return;
        _compact = compact;
        foreach (var (key, roomy, tight) in Table)
            Application.Current.Resources[key] = compact ? tight : roomy;
    }
}
