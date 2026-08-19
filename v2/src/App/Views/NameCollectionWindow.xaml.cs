using System.Windows;
using System.Windows.Input;

namespace VideoPlayer.App.Views;

/// <summary>
/// Asks for a collection name. WPF has no input box, so this is the same small
/// modal pattern as AddShortcutWindow: a nullable result, and null means the
/// person changed their mind.
/// </summary>
public partial class NameCollectionWindow : Window
{
    public string? Result { get; private set; }

    public NameCollectionWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>Show the dialog and hand back a trimmed name, or null if cancelled.</summary>
    public static string? Ask(Window? owner)
    {
        var dialog = new NameCollectionWindow { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            // The server would refuse this too; catching it here saves a round
            // trip and says so where the person is already looking.
            ErrorText.Text = "Give the collection a name.";
            ErrorText.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }
        Result = name;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Create_Click(sender, e);
    }
}
