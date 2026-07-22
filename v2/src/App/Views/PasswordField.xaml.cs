using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace VideoPlayer.App.Views;

/// <summary>
/// A password box with a show/hide eye. WPF's PasswordBox can't reveal its text,
/// so this keeps a hidden TextBox alongside it and swaps which one is visible;
/// whichever is showing is the source of truth for <see cref="Password"/>.
/// </summary>
public partial class PasswordField : UserControl
{
    private bool _revealed;

    public PasswordField()
    {
        InitializeComponent();
        AutomationProperties.SetName(Masked, FieldName);
        AutomationProperties.SetName(Plain, FieldName);
    }

    /// <summary>The entered password, read from whichever box is visible.</summary>
    public string Password => _revealed ? Plain.Text : Masked.Password;

    public void Clear()
    {
        Masked.Clear();
        Plain.Clear();
    }

    public new void Focus() => (_revealed ? (Control)Plain : Masked).Focus();

    /// <summary>Accessible name applied to both inner boxes (used by tests too).</summary>
    public static readonly DependencyProperty FieldNameProperty =
        DependencyProperty.Register(nameof(FieldName), typeof(string), typeof(PasswordField),
            new PropertyMetadata("Password", OnFieldNameChanged));

    public string FieldName
    {
        get => (string)GetValue(FieldNameProperty);
        set => SetValue(FieldNameProperty, value);
    }

    private static void OnFieldNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var f = (PasswordField)d;
        AutomationProperties.SetName(f.Masked, (string)e.NewValue);
        AutomationProperties.SetName(f.Plain, (string)e.NewValue);
    }

    private void Eye_Click(object sender, RoutedEventArgs e)
    {
        _revealed = !_revealed;
        if (_revealed)
        {
            Plain.Text = Masked.Password;
            Masked.Visibility = Visibility.Collapsed;
            Plain.Visibility = Visibility.Visible;
            Plain.Focus();
            Plain.CaretIndex = Plain.Text.Length;
        }
        else
        {
            Masked.Password = Plain.Text;
            Plain.Visibility = Visibility.Collapsed;
            Masked.Visibility = Visibility.Visible;
            Masked.Focus();
        }
        EyeGlyph.Foreground = (Brush)FindResource(_revealed ? "AccentBrush" : "TextMutedBrush");
        EyeBtn.ToolTip = _revealed ? "Hide password" : "Show password";
        AutomationProperties.SetName(EyeBtn, _revealed ? "Hide password" : "Show password");
    }
}
