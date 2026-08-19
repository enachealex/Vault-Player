using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using QRCoder;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// Mints a library invite and shows it the way it will actually travel: as a
/// QR code a friend scans with their phone. The QR carries the /invite/ page
/// URL — the page explains the steps and shows the code, so the person needs
/// nothing but the scan. The code and link are also copyable for chats.
/// </summary>
public partial class InviteWindow : Window
{
    public sealed class Row
    {
        public required AccountService.LibraryInvite Invite { get; init; }
        public string Code => Invite.Code;
        public string StatusText
        {
            get
            {
                if (Invite.Revoked) return "revoked";
                if (Invite.UsedByName.Length > 0) return $"used by {Invite.UsedByName}";
                var left = DateTimeOffset.FromUnixTimeMilliseconds(Invite.ExpiresAt) - DateTimeOffset.UtcNow;
                return left.TotalHours < 1 ? "expiring" : $"open — {(int)left.TotalDays}d left";
            }
        }
        public Visibility RevokeVisible =>
            !Invite.Revoked && Invite.UsedByName.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private AccountService.LibraryInvite? _current;

    public InviteWindow()
    {
        InitializeComponent();
        _ = MintAsync();
    }

    /// <summary>Create a fresh invite and put it on screen.</summary>
    private async Task MintAsync()
    {
        ErrorText.Visibility = Visibility.Collapsed;
        var invite = await AppServices.Account.CreateInviteAsync();
        if (invite is null)
        {
            ShowError("Couldn't create an invite. Check your connection and try again.");
            return;
        }
        _current = invite;
        CodeText.Text = invite.Code;
        ExpiryText.Text =
            $"expires {DateTimeOffset.FromUnixTimeMilliseconds(invite.ExpiresAt).ToLocalTime():d MMM, HH:mm}";
        QrImage.Source = Helpers.QrRender.Render(invite.Url);
        await RefreshListAsync();
    }

    private async Task RefreshListAsync()
    {
        var invites = await AppServices.Account.ListInvitesAsync();
        if (invites is null) return;
        InviteList.ItemsSource = invites
            .Where(i => i.Code != _current?.Code)
            .Select(i => new Row { Invite = i })
            .ToList();
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try { Clipboard.SetText(_current.Code); } catch { }
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        try { Clipboard.SetText(_current.Url); } catch { }
    }

    private async void NewInvite_Click(object sender, RoutedEventArgs e) => await MintAsync();

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not Row row) return;
        var error = await AppServices.Account.RevokeInviteAsync(row.Code);
        if (error is not null) ShowError(error);
        await RefreshListAsync();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
