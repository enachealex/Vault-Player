using System.Windows;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

public partial class AccountWindow : Window
{
    private enum Mode { SignIn, Register, Verify, Forgot, Reset, SignedIn }

    private Mode _mode;
    private string _pendingEmail = "";   // carried into the verify/reset steps

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
        VerifyPanel.Visibility = mode == Mode.Verify ? Visibility.Visible : Visibility.Collapsed;
        ForgotPanel.Visibility = mode == Mode.Forgot ? Visibility.Visible : Visibility.Collapsed;
        ResetPanel.Visibility = mode == Mode.Reset ? Visibility.Visible : Visibility.Collapsed;
        SignedInPanel.Visibility = mode == Mode.SignedIn ? Visibility.Visible : Visibility.Collapsed;

        switch (mode)
        {
            case Mode.SignIn:
            case Mode.Register:
                var reg = mode == Mode.Register;
                Heading.Text = reg ? "Create account" : "Sign in";
                Subhead.Text = "Sync your library — resume points and watch counts — across your computers. Optional; the app works fully without it.";
                NameRow.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                PassHint.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                ConfirmRow.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
                if (!reg) ConfirmField.Clear();   // no stale confirm when signing in
                ForgotLink.Visibility = reg ? Visibility.Collapsed : Visibility.Visible;
                SubmitBtn.Content = reg ? "Create account" : "Sign in";
                ToggleBtn.Content = reg ? "I have an account" : "Create account";
                break;
            case Mode.Verify:
                Heading.Text = "Check your email";
                Subhead.Text = $"We sent a 6-digit code to {_pendingEmail}. Enter it to finish. It expires in 20 minutes.";
                CodeBox.Clear();
                break;
            case Mode.Forgot:
                Heading.Text = "Reset password";
                Subhead.Text = "Enter your email and we'll send a reset code.";
                ForgotEmailBox.Text = EmailBox.Text;
                break;
            case Mode.Reset:
                Heading.Text = "Set a new password";
                Subhead.Text = $"Enter the code we sent to {_pendingEmail} and a new password.";
                ResetCodeBox.Clear();
                break;
            case Mode.SignedIn:
                Heading.Text = "Account";
                Subhead.Text = "Your library syncs across the computers you sign in on.";
                WhoName.Text = AppServices.Account.Name ?? "Signed in";
                WhoEmail.Text = AppServices.Account.Email ?? "";
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

    // ---- Sign in / Register ----

    private void Toggle_Click(object sender, RoutedEventArgs e) =>
        Show(_mode == Mode.Register ? Mode.SignIn : Mode.Register);

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var pass = PassField.Password;
        if (email.Length == 0 || pass.Length == 0) { ShowError("Enter your email and password."); return; }

        var reg = _mode == Mode.Register;
        if (reg)
        {
            if (pass.Length < 8) { ShowError("Use a password of at least 8 characters."); return; }
            if (pass != ConfirmField.Password) { ShowError("Those passwords don't match."); return; }
        }
        Busy(true, SubmitBtn, reg ? "Creating…" : "Signing in…", "");
        var outcome = reg
            ? await AppServices.Account.RegisterAsync(email, pass, NameBox.Text.Trim())
            : await AppServices.Account.LoginAsync(email, pass);
        Busy(false, SubmitBtn, "", reg ? "Create account" : "Sign in");

        _pendingEmail = email;
        HandleOutcome(outcome);
    }

    private void HandleOutcome(AccountService.AuthOutcome outcome)
    {
        switch (outcome.Status)
        {
            case AccountService.AuthStatus.Success: Show(Mode.SignedIn); break;
            case AccountService.AuthStatus.NeedsVerification:
                _pendingEmail = outcome.Email ?? _pendingEmail;
                Show(Mode.Verify);
                break;
            default: ShowError(outcome.Message ?? "Something went wrong."); break;
        }
    }

    // ---- Verify ----

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var code = CodeBox.Text.Trim();
        if (code.Length == 0) { ShowError("Enter the code from your email."); return; }
        Busy(true, VerifyBtn, "Verifying…", "");
        var outcome = await AppServices.Account.VerifyAsync(_pendingEmail, code);
        Busy(false, VerifyBtn, "", "Verify");
        HandleOutcome(outcome);
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        await AppServices.Account.ResendCodeAsync(_pendingEmail);
        ShowError("");   // clear any prior error
        Subhead.Text = $"A new code is on its way to {_pendingEmail}.";
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
            EmailBox.Text = _pendingEmail;
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
}
