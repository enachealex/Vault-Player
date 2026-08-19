using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

public partial class AccountWindow : Window
{
    private enum Mode { SignIn, Register, Phone, Forgot, Reset, SignedIn }

    private Mode _mode;
    private string _pendingEmail = "";     // carried into the reset step
    private string _captchaId = "";

    public AccountWindow()
    {
        InitializeComponent();
        Show(AppServices.Account.IsSignedIn ? Mode.SignedIn : Mode.SignIn);
    }

    private void Show(Mode mode)
    {
        _mode = mode;
        ErrorText.Visibility = Visibility.Collapsed;

        FormPanel.Visibility = mode is Mode.SignIn or Mode.Register ? Visibility.Visible : Visibility.Collapsed;
        PhonePanel.Visibility = mode == Mode.Phone ? Visibility.Visible : Visibility.Collapsed;
        ForgotPanel.Visibility = mode == Mode.Forgot ? Visibility.Visible : Visibility.Collapsed;
        ResetPanel.Visibility = mode == Mode.Reset ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = mode == Mode.SignedIn ? Visibility.Visible : Visibility.Collapsed;

        switch (mode)
        {
            case Mode.SignIn:
            case Mode.Register:
                var reg = mode == Mode.Register;
                Heading.Text = reg ? "Create account" : "Sign in";
                Subhead.Text = reg
                    ? "Pick a username your friends will see. New accounts wait for the server owner's "
                      + "approval before the shared film library opens up."
                    : "Sync your library across computers, join private parties, and watch films "
                      + "hosted on the Vault server.";
                UsernameHint.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                PassHint.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                ConfirmRow.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                EmailRow.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                InviteRow.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                CaptchaRow.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                if (!reg) ConfirmField.Clear();   // no stale confirm when signing in
                ForgotLink.Visibility = reg ? Visibility.Collapsed : Visibility.Visible;
                SubmitBtn.Content = reg ? "Create account" : "Sign in";
                ToggleBtn.Content = reg ? "I have an account" : "Create account";
                if (reg) _ = LoadCaptchaAsync();
                break;
            case Mode.Phone:
                Heading.Text = "Sign in with your phone";
                Subhead.Text = "Your password stays on your phone — this screen only ever sees a short code.";
                LinkCodeBox.Clear();
                var linkUrl = $"{AppServices.Account.BaseUrl}/link";
                LinkUrlText.Text = linkUrl;
                LinkQrImage.Source = Helpers.QrRender.Render(linkUrl);
                break;
            case Mode.Forgot:
                Heading.Text = "Reset password";
                Subhead.Text = "Enter the email on your account and we'll send a reset code.";
                break;
            case Mode.Reset:
                Heading.Text = "Set a new password";
                Subhead.Text = $"Enter the code we sent to {_pendingEmail} and a new password.";
                ResetCodeBox.Clear();
                break;
            case Mode.SignedIn:
                Heading.Text = "Account";
                Subhead.Text = "Your library syncs across the computers you sign in on.";
                var acct = AppServices.Account;
                WhoName.Text = acct.Username ?? acct.Name ?? "Signed in";
                WhoEmail.Text = string.IsNullOrEmpty(acct.Email) ? "No email on this account" : acct.Email;

                // Pending accounts see plainly why the Vault library is empty.
                if (acct.Status == "pending")
                {
                    StatusLine.Text = "Waiting for the server owner to approve this account — "
                        + "the shared film library unlocks once they do.";
                    StatusLine.Visibility = Visibility.Visible;
                }
                else
                {
                    StatusLine.Visibility = Visibility.Collapsed;
                }
                ManageUsersBtn.Visibility = acct.IsOwner ? Visibility.Visible : Visibility.Collapsed;

                var count = AppServices.Settings.SyncedLibrary.Count;
                SyncStatus.Text = count == 0 ? "Nothing synced yet." : $"{count} film{(count == 1 ? "" : "s")} synced.";
                UpdateFriendsLabel();
                _ = RefreshFriendsAsync();
                break;
        }
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Busy(bool busy, System.Windows.Controls.Button btn, string busyText, string idleText)
    {
        btn.IsEnabled = !busy;
        btn.Content = busy ? busyText : idleText;
    }

    // ---- Captcha ----

    private async System.Threading.Tasks.Task LoadCaptchaAsync()
    {
        CaptchaBox.Clear();
        CaptchaImage.Source = null;
        var challenge = await AppServices.Account.FetchCaptchaAsync();
        if (challenge is null)
        {
            ShowError("Couldn't reach the server for a challenge image. Check your connection and press refresh.");
            return;
        }
        _captchaId = challenge.Id;

        using var stream = new MemoryStream(challenge.Png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        CaptchaImage.Source = image;
    }

    private async void RefreshCaptcha_Click(object sender, RoutedEventArgs e) => await LoadCaptchaAsync();

    // ---- Sign in / Register ----

    private void Toggle_Click(object sender, RoutedEventArgs e) =>
        Show(_mode == Mode.Register ? Mode.SignIn : Mode.Register);

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        var pass = PassField.Password;
        if (username.Length == 0 || pass.Length == 0) { ShowError("Enter your username and password."); return; }

        var reg = _mode == Mode.Register;
        if (reg)
        {
            if (pass.Length < 8) { ShowError("Use a password of at least 8 characters."); return; }
            if (pass != ConfirmField.Password) { ShowError("Those passwords don't match."); return; }
            if (CaptchaBox.Text.Trim().Length == 0) { ShowError("Type the characters from the image."); return; }
        }
        Busy(true, SubmitBtn, reg ? "Creating…" : "Signing in…", "");
        var outcome = reg
            ? await AppServices.Account.RegisterAsync(username, pass,
                string.IsNullOrWhiteSpace(EmailBox.Text) ? null : EmailBox.Text.Trim(),
                _captchaId, CaptchaBox.Text.Trim(),
                string.IsNullOrWhiteSpace(InviteBox.Text) ? null : InviteBox.Text.Trim())
            : await AppServices.Account.LoginAsync(username, pass);
        Busy(false, SubmitBtn, "", reg ? "Create account" : "Sign in");

        if (outcome.Status == AccountService.AuthStatus.Success)
        {
            Show(Mode.SignedIn);
        }
        else
        {
            ShowError(outcome.Message ?? "Something went wrong.");
            // The challenge is single-use: any failed attempt burned it.
            if (reg) _ = LoadCaptchaAsync();
        }
    }

    // ---- Sign in with phone (device-link) ----

    private void PhoneSignIn_Click(object sender, RoutedEventArgs e) => Show(Mode.Phone);

    private async void LinkClaim_Click(object sender, RoutedEventArgs e)
    {
        var code = LinkCodeBox.Text.Trim();
        if (code.Length == 0) { ShowError("Type the code your phone showed you."); return; }
        Busy(true, LinkClaimBtn, "Signing in…", "");
        var outcome = await AppServices.Account.LoginWithLinkCodeAsync(code);
        Busy(false, LinkClaimBtn, "", "Sign in");
        if (outcome.Status == AccountService.AuthStatus.Success) Show(Mode.SignedIn);
        else ShowError(outcome.Message ?? "Something went wrong.");
    }

    // ---- Forgot / Reset ----

    private void Forgot_Click(object sender, RoutedEventArgs e) => Show(Mode.Forgot);

    private async void SendCode_Click(object sender, RoutedEventArgs e)
    {
        var email = ForgotEmailBox.Text.Trim();
        if (email.Length == 0) { ShowError("Enter your email."); return; }
        Busy(true, SendCodeBtn, "Sending…", "");
        await AppServices.Account.ForgotAsync(email);
        Busy(false, SendCodeBtn, "", "Send code");
        _pendingEmail = email;
        Show(Mode.Reset);   // always advance — we don't reveal whether the email exists
    }

    private async void Reset_Click(object sender, RoutedEventArgs e)
    {
        var code = ResetCodeBox.Text.Trim();
        var pass = ResetField.Password;
        if (code.Length == 0 || pass.Length == 0) { ShowError("Enter the code and a new password."); return; }
        Busy(true, ResetBtn, "Resetting…", "");
        var error = await AppServices.Account.ResetAsync(_pendingEmail, code, pass);
        Busy(false, ResetBtn, "", "Reset password");
        if (error is null)
        {
            Show(Mode.SignIn);
            Subhead.Text = "Password reset. Sign in with your new password.";
        }
        else ShowError(error);
    }

    // ---- Signed in ----

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Account.SignOut();
        Show(Mode.SignIn);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "Delete your account and everything synced to it? This can't be undone. Films on this computer are not touched.",
            "Delete account", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var error = await AppServices.Account.DeleteAccountAsync();
        if (error is null)
        {
            MessageBox.Show(this, "Your account has been deleted.", "Account",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Show(Mode.SignIn);
        }
        else ShowError(error);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => Show(Mode.SignIn);

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Friends ----

    private void Friends_Click(object sender, RoutedEventArgs e)
    {
        new FriendsWindow { Owner = this }.ShowDialog();
        UpdateFriendsLabel();
    }

    private async System.Threading.Tasks.Task RefreshFriendsAsync()
    {
        await AppServices.Friends.RefreshAsync();
        UpdateFriendsLabel();
    }

    /// <summary>Show a friend count, and flag pending requests so they're noticed.</summary>
    private void UpdateFriendsLabel()
    {
        var f = AppServices.Friends;
        if (f.Incoming.Count > 0)
            FriendsBtnLabel.Text = $"Friends — {f.Incoming.Count} request{(f.Incoming.Count == 1 ? "" : "s")}";
        else
            FriendsBtnLabel.Text = f.Friends.Count == 0 ? "Friends" : $"Friends ({f.Friends.Count})";
    }

    // ---- Owner: manage users ----

    private void ManageUsers_Click(object sender, RoutedEventArgs e) =>
        new ManageUsersWindow { Owner = this }.ShowDialog();
}
