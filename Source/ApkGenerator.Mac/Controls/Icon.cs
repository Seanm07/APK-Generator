using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Shape = Avalonia.Controls.Shapes.Path;
using Avalonia.Media;

namespace ApkGenerator.Mac.Controls;

/// <summary>
/// A small line icon drawn from path data (a 24x24 grid), tinted with the surrounding text colour.
/// macOS has no Segoe icon font, so nothing here depends on a font. Size it with Width/Height.
/// </summary>
public sealed class Icon : Viewbox
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    private readonly Shape _path = new()
    {
        StrokeThickness = 2,
        StrokeLineCap = PenLineCap.Round,
        StrokeJoin = PenLineJoin.Round,
        Stretch = Stretch.None,
    };

    static Icon()
    {
        KindProperty.Changed.AddClassHandler<Icon>((i, _) => i.Update());
    }

    public Icon()
    {
        Width = 16;
        Height = 16;
        Stretch = Stretch.Uniform;
        IsHitTestVisible = false;
        Child = new Canvas { Width = 24, Height = 24, Children = { _path } };

        // Follow the inherited text colour, so icons match their button / label (and the light or dark theme).
        PropertyChanged += (_, e) => { if (e.Property == TextElement.ForegroundProperty) _path.Stroke = e.NewValue as IBrush; };
        AttachedToVisualTree += (_, _) => _path.Stroke = GetValue(TextElement.ForegroundProperty);
    }

    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    private void Update() =>
        _path.Data = Kind is { } k && Shapes.TryGetValue(k, out var data) ? Geometry.Parse(data) : null;

    private static readonly Dictionary<string, string> Shapes = new()
    {
        ["play"] = "M7,4 L19,12 L7,20 Z",
        ["save"] = "M5,3 L17,3 L21,7 L21,21 L5,21 Z M8,3 L8,9 L16,9 L16,3 M8,21 L8,14 L18,14 L18,21",
        ["upload"] = "M12,17 L12,4 M6,10 L12,4 L18,10 M4,17 L4,20 L20,20 L20,17",
        ["download"] = "M12,4 L12,17 M6,11 L12,17 L18,11 M4,17 L4,20 L20,20 L20,17",
        ["refresh"] = "M20,12 A8,8 0 1 1 16.5,5.4 M20,3 L20,8 L15,8",
        ["check"] = "M5,12 L10,17 L19,7",
        ["x"] = "M6,6 L18,18 M18,6 L6,18",
        ["info"] = "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 Z M12,11 L12,17 M12,7 L12,7.5",
        ["clock"] = "M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 Z M12,7 L12,12 L16,14",
        ["warning"] = "M12,3 L22,20 L2,20 Z M12,9 L12,14 M12,17 L12,17.5",
        ["eye"] = "M2,12 C5,6.5 8.5,5 12,5 C15.5,5 19,6.5 22,12 C19,17.5 15.5,19 12,19 C8.5,19 5,17.5 2,12 Z " +
                  "M12,9.5 A2.5,2.5 0 1 1 12,14.5 A2.5,2.5 0 1 1 12,9.5 Z",
        ["eye-off"] = "M2,12 C5,6.5 8.5,5 12,5 C15.5,5 19,6.5 22,12 C19,17.5 15.5,19 12,19 C8.5,19 5,17.5 2,12 Z " +
                      "M12,9.5 A2.5,2.5 0 1 1 12,14.5 A2.5,2.5 0 1 1 12,9.5 Z M4,20 L20,4",
        ["cog"] = Gear(),
    };

    /// <summary>An eight-toothed gear with a hole, computed rather than hand-typed.</summary>
    private static string Gear()
    {
        const double cx = 12, cy = 12, outer = 10, inner = 7.4, hole = 3.2;
        var sb = new System.Text.StringBuilder();
        string P(double r, double deg) =>
            FormattableString.Invariant($"{cx + r * Math.Cos(deg * Math.PI / 180):0.###},{cy + r * Math.Sin(deg * Math.PI / 180):0.###}");

        for (var i = 0; i < 8; i++)
        {
            var c = i * 45.0;
            sb.Append(i == 0 ? "M" : "L").Append(P(inner, c - 19)).Append(' ');
            sb.Append('L').Append(P(outer, c - 10)).Append(' ');
            sb.Append('L').Append(P(outer, c + 10)).Append(' ');
            sb.Append('L').Append(P(inner, c + 19)).Append(' ');
        }
        sb.Append("Z ");
        sb.Append(FormattableString.Invariant($"M{cx + hole},{cy} A{hole},{hole} 0 1 1 {cx - hole},{cy} A{hole},{hole} 0 1 1 {cx + hole},{cy} Z"));
        return sb.ToString();
    }
}
