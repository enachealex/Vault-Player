using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// The owner's view of every account: approve the pending, block the unwanted,
/// remove the gone. This is the whole moderation surface — approval is what
/// stands between a fresh sign-up and the film library.
/// </summary>
public partial class ManageUsersWindow : Window
{
    /// <summary>Row shape the XAML binds to; wraps the wire DTO with display logic.</summary>
    public sealed class Row
    {
        public required AccountService.ManagedUser User { get; init; }

        public string Username => User.Username;
        public string Badge => User.IsOwner ? "owner" : User.Status;
        public Brush BadgeBrush => User.Status switch
        {
            _ when User.IsOwner => Brushes.MediumPurple,
            "approved" => Brushes.MediumSeaGreen,
            "blocked" => Brushes.IndianRed,
            _ => Brushes.Orange,          // pending: the one that wants attention
        };
        public string Detail
        {
            get
            {
                var joined = User.CreatedAt > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(User.CreatedAt).ToLocalTime().ToString("d MMM yyyy")
                    : "before usernames";
                return string.IsNullOrEmpty(User.Email) ? $"joined {joined}" : $"{User.Email}  ·  joined {joined}";
            }
        }

        // The owner's own row shows no buttons at all: the server refuses those
        // calls anyway, and a button that always errors is just a trap.
        public Visibility ApproveVisible =>
            !User.IsOwner && User.Status == "pending" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility BlockVisible =>
            !User.IsOwner && User.Status != "blocked" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility UnblockVisible =>
            !User.IsOwner && User.Status == "blocked" ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveVisible => User.IsOwner ? Visibility.Collapsed : Visibility.Visible;
    }

    public ManageUsersWindow()
    {
        InitializeComponent();
        _ = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var users = await AppServices.Account.ListUsersAsync();
        if (users is null)
        {
            ShowError("Couldn't load the user list. Check your connection and try refresh.");
            return;
        }

        // Pending first — they're the reason the owner opened this window.
        UserList.ItemsSource = users
            .OrderBy(u => u.IsOwner ? 2 : u.Status == "pending" ? 0 : 1)
            .ThenBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(u => new Row { User = u })
            .ToList();

        var pending = users.Count(u => u.Status == "pending");
        Subhead.Text = pending == 0
            ? "New accounts start as pending and can't see your films until you approve them."
            : $"{pending} account{(pending == 1 ? "" : "s")} waiting for approval.";
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row row) return;
        var error = await AppServices.Account.SetUserStatusAsync(row.User.UserId, "approved");
        if (error is not null) ShowError(error);
        await RefreshAsync();
    }

    private async void Block_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row row) return;
        var confirm = MessageBox.Show(this,
            $"Block {row.Username}? They'll be signed out everywhere and can't sign in again until unblocked.",
            "Block user", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        var error = await AppServices.Account.SetUserStatusAsync(row.User.UserId, "blocked");
        if (error is not null) ShowError(error);
        await RefreshAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row row) return;
        var confirm = MessageBox.Show(this,
            $"Remove {row.Username}'s account and everything synced to it? This can't be undone.",
            "Remove user", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var error = await AppServices.Account.RemoveUserAsync(row.User.UserId);
        if (error is not null) ShowError(error);
        await RefreshAsync();
    }

    private async void Invite_Click(object sender, RoutedEventArgs e)
    {
        new InviteWindow { Owner = this }.ShowDialog();
        await RefreshAsync();   // an invited friend may have registered meanwhile
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
