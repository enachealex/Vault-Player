using System;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

public partial class HomeView : UserControl
{
    // The clock only shows minutes, so it only needs to be right to the
    // minute; checking every few seconds keeps it from lagging a whole one.
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(5) };

    public HomeView()
    {
        InitializeComponent();
        RefreshAccount();
        Tick();
        _clock.Tick += (_, _) => Tick();
        _clock.Start();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = version is null ? "Vault Movies" : $"Vault Movies {version.ToString(3)}";

        AppServices.Account.Changed += RefreshAccount;
        Unloaded += (_, _) =>
        {
            _clock.Stop();
            AppServices.Account.Changed -= RefreshAccount;
        };
    }

    private void Tick()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        ClockText.Text = now.ToString("t", culture);
        // Day and date without the year — a home screen, not a letterhead.
        DateText.Text = $"{now.ToString("dddd", culture)}, {now.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}";

        var part = now.Hour switch { < 5 => "Good evening", < 12 => "Good morning", < 18 => "Good afternoon", _ => "Good evening" };
        Greeting.Text = AppServices.Account.IsSignedIn && AppServices.Account.Name is { Length: > 0 } name
            ? $"{part}, {name}"
            : part;
    }

    private void RefreshAccount() => Dispatcher.Invoke(() =>
    {
        AccountLabel.Text = AppServices.Account.IsSignedIn
            ? AppServices.Account.Name ?? "Account"
            : "Sign in";
        FriendsBtn.Visibility = AppServices.Account.IsSignedIn ? Visibility.Visible : Visibility.Collapsed;
        Tick();
    });

    private void AccountBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AccountWindow { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        RefreshAccount();
    }

    private void SoloBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new LibraryView());

    private void PartyBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new PartyView());

    private void FilmsBtn_Click(object sender, RoutedEventArgs e) =>
        new LibrarySetupWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void FriendsBtn_Click(object sender, RoutedEventArgs e) =>
        new FriendsWindow { Owner = Window.GetWindow(this) }.ShowDialog();

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow { Owner = Window.GetWindow(this) }.ShowDialog();
}
