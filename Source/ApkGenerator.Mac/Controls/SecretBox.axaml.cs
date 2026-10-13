using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace ApkGenerator.Mac.Controls;

/// <summary>A password box with a show/hide button and a bindable <see cref="Secret"/>.</summary>
public partial class SecretBox : UserControl
{
    public static readonly StyledProperty<string> SecretProperty =
        AvaloniaProperty.Register<SecretBox, string>(nameof(Secret), "", defaultBindingMode: BindingMode.TwoWay);

    private bool _revealed;
    private bool _syncing;

    public SecretBox()
    {
        InitializeComponent();
        Box.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            _syncing = true;
            try { Secret = Box.Text ?? ""; } finally { _syncing = false; }
        };
        SecretProperty.Changed.AddClassHandler<SecretBox>((s, e) =>
        {
            if (s._syncing) return;
            s._syncing = true;
            try { s.Box.Text = (string?)e.NewValue ?? ""; } finally { s._syncing = false; }
        });
    }

    public string Secret
    {
        get => GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }

    private void Reveal_Click(object? sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        Box.PasswordChar = _revealed ? '\0' : '●';
        EyeIcon.Kind = _revealed ? "eye-off" : "eye";
        ToolTip.SetTip(Reveal, _revealed ? "Hide password" : "Show password");
        Avalonia.Automation.AutomationProperties.SetName(Reveal, _revealed ? "Hide password" : "Show password");
        Box.Focus();
    }
}
