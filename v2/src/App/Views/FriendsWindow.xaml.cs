using System.Windows;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// Manage friends: add by email, answer incoming requests, and remove people.
/// Reads live state from <see cref="FriendsService"/> and refreshes on open.
/// </summary>
public partial class FriendsWindow : Window
{
    public FriendsWindow()
    {
        InitializeComponent();
        Render();
        AppServices.Friends.Changed += OnChanged;
        Closed += (_, _) => AppServices.Friends.Changed -= OnChanged;
        Loaded += async (_, _) => await AppServices.Friends.RefreshAsync();
    }

    private void OnChanged() => Dispatcher.Invoke(Render);

    private void Render()
    {
        var f = AppServices.Friends;

        RequestsList.ItemsSource = f.Incoming;
        RequestsSection.Visibility = f.Incoming.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        FriendsList.ItemsSource = f.Friends;
        EmptyFriends.Visibility = f.Friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (f.Outgoing.Count > 0)
        {
            PendingSection.Visibility = Visibility.Visible;
            PendingText.Text = f.Outgoing.Count == 1
                ? $"Waiting for {f.Outgoing[0].Name} to accept your request."
                : $"Waiting on {f.Outgoing.Count} sent requests.";
        }
        else PendingSection.Visibility = Visibility.Collapsed;
    }

    private void ShowStatus(string msg, bool isError)
    {
        StatusText.Text = msg;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(isError ? "AccentBrush" : "AccentWarmBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        if (email.Length == 0) { ShowStatus("Enter their email.", true); return; }

        AddBtn.IsEnabled = false;
        var error = await AppServices.Friends.AddByEmailAsync(email);
        AddBtn.IsEnabled = true;

        if (error is null)
        {
            EmailBox.Clear();
            ShowStatus("Request sent.", false);
        }
        else ShowStatus(error, true);
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FriendsService.Person p)
            await AppServices.Friends.RespondAsync(p.RequestId, accept: true);
    }

    private async void Decline_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is FriendsService.Person p)
            await AppServices.Friends.RespondAsync(p.RequestId, accept: false);
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not FriendsService.Person p) return;
        var confirm = MessageBox.Show(this, $"Remove {p.Name} from your friends?",
            "Remove friend", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm == MessageBoxResult.Yes) await AppServices.Friends.RemoveAsync(p.UserId);
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
