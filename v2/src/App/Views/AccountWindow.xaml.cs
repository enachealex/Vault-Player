using System.Windows;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

public partial class AccountWindow : Window
{
    private bool _registerMode;

    public AccountWindow()
    {
        InitializeComponent();
        if (AppServices.Account.IsSignedIn) ShowSignedIn();
        else ShowForm();
    }

    private void ShowSignedIn()
    {
        FormPanel.Visibility = Visibility.Collapsed;
        SignedInPanel.Visibility = Visibility.Visible;
        Heading.Text = "Account";
        Subhead.Text = "Your library syncs across the computers you sign in on.";
        WhoName.Text = AppServices.Account.Name ?? "Signed in";
        WhoEmail.Text = AppServices.Account.Email ?? "";
        var count = AppServices.Settings.SyncedLibrary.Count;
        SyncStatus.Text = count == 0 ? "Nothing synced yet." : $"{count} film{(count == 1 ? "" : "s")} synced.";
    }

    private void ShowForm()
    {
        FormPanel.Visibility = Visibility.Visible;
        SignedInPanel.Visibility = Visibility.Collapsed;
        _registerMode = false;
        ApplyMode();
    }

    private void ApplyMode()
    {
        Heading.Text = _registerMode ? "Create account" : "Sign in";
        NameRow.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        PassHint.Visibility = _registerMode ? Visibility.Visible : Visibility.Collapsed;
        SubmitBtn.Content = _registerMode ? "Create account" : "Sign in";
        ToggleBtn.Content = _registerMode ? "I have an account" : "Create account";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _registerMode = !_registerMode;
        ApplyMode();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e)
    {
        var email = EmailBox.Text.Trim();
        var pass = PassBox.Password;
        if (email.Length == 0 || pass.Length == 0)
        {
            ShowError("Enter your email and password.");
            return;
        }

        SubmitBtn.IsEnabled = ToggleBtn.IsEnabled = false;
        SubmitBtn.Content = _registerMode ? "Creating…" : "Signing in…";

        var error = _registerMode
            ? await AppServices.Account.RegisterAsync(email, pass, NameBox.Text.Trim())
            : await AppServices.Account.LoginAsync(email, pass);

        SubmitBtn.IsEnabled = ToggleBtn.IsEnabled = true;
        ApplyMode();

        if (error is null) ShowSignedIn();
        else ShowError(error);
    }

    private void ShowError(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Account.SignOut();
        ShowForm();
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
