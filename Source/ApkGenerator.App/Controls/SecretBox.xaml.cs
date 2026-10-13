using System.Windows;
using System.Windows.Controls;

namespace ApkGenerator.App.Controls;

/// <summary>A password box with a "show" toggle and a bindable <see cref="Secret"/> (PasswordBox.Password can't be bound).</summary>
public partial class SecretBox : UserControl
{
    public static readonly DependencyProperty SecretProperty = DependencyProperty.Register(
        nameof(Secret), typeof(string), typeof(SecretBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSecretChanged));

    private bool _syncing;

    public SecretBox() => InitializeComponent();

    public string Secret
    {
        get => (string)GetValue(SecretProperty);
        set => SetValue(SecretProperty, value);
    }

    private static void OnSecretChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (SecretBox)d;
        if (box._syncing) return;
        box._syncing = true;
        try
        {
            var value = (string?)e.NewValue ?? "";
            if (box.Hidden.Password != value) box.Hidden.Password = value;
            if (box.Shown.Text != value) box.Shown.Text = value;
        }
        finally
        {
            box._syncing = false;
        }
    }

    private void Hidden_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            Shown.Text = Hidden.Password;
            Secret = Hidden.Password;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void Shown_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            Hidden.Password = Shown.Text;
            Secret = Shown.Text;
        }
        finally
        {
            _syncing = false;
        }
    }

    private bool _revealed;

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        Shown.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;
        Hidden.Visibility = _revealed ? Visibility.Collapsed : Visibility.Visible;
        Slash.Visibility = _revealed ? Visibility.Visible : Visibility.Collapsed;   // slashed eye = "click to hide"
        Reveal.ToolTip = _revealed ? "Hide password" : "Show password";
        System.Windows.Automation.AutomationProperties.SetName(Reveal, _revealed ? "Hide password" : "Show password");
        (_revealed ? (Control)Shown : Hidden).Focus();
    }
}
