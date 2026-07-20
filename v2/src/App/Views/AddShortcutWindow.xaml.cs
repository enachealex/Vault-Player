using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

public partial class AddShortcutWindow : Window
{
    /// <summary>The shortcut the user built, or null if they cancelled.</summary>
    public StreamingShortcut? Result { get; private set; }

    public AddShortcutWindow()
    {
        InitializeComponent();
        ServiceBox.ItemsSource = StreamingServices.ServiceNames;
        ServiceBox.SelectedIndex = 0; // Prime Video — what prompted this feature.
        Loaded += (_, _) => TitleBox.Focus();
    }

    private void Validate(object sender, RoutedEventArgs e)
    {
        var hasTitle = TitleBox.Text.Trim().Length > 0;
        var url = UrlBox.Text.Trim();
        var urlOk = url.Length == 0 || StreamingServices.IsSafeUrl(url);

        if (url.Length > 0 && !urlOk)
        {
            UrlHint.Text = "That doesn't look like a web link — it needs to start with http:// or https://";
            UrlHint.Foreground = (Brush)FindResource("AccentBrush");
        }
        else
        {
            UrlHint.Text = "Paste the title's page from the service for a one-click open. " +
                           "Leave blank to search the service by title instead.";
            UrlHint.Foreground = (Brush)FindResource("TextMutedBrush");
        }

        AddBtn.IsEnabled = hasTitle && urlOk;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        Result = new StreamingShortcut
        {
            Title = TitleBox.Text.Trim(),
            Service = (string)(ServiceBox.SelectedItem ?? StreamingServices.ServiceNames[0]),
            Url = url.Length > 0 ? url : null,
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
