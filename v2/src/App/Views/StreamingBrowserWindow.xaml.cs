using System;
using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// In-app browser for streaming services: sign in, find what you own, then hand
/// the actual playback to the real browser.
///
/// Playing here is deliberately not attempted. Embedded browsers have no
/// Widevine module, and the services only permit certified browsers anyway, so
/// a protected title would render a black rectangle. Instead the app spots a
/// player page and offers the hand-off, where it plays at full quality.
///
/// Sign-in happens on the service's own page — the app never sees or stores
/// credentials. The session cookie lives in the user's own profile folder, the
/// same as any browser, so signing in is a once-per-service thing.
/// </summary>
public partial class StreamingBrowserWindow : Window
{
    private readonly string _startUrl;
    private string _currentUrl;

    public StreamingBrowserWindow(string startUrl, string? serviceName = null)
    {
        InitializeComponent();
        _startUrl = StreamingServices.IsSafeUrl(startUrl) ? startUrl : "about:blank";
        _currentUrl = _startUrl;
        if (serviceName is { Length: > 0 }) Title = serviceName;
        Loaded += async (_, _) => await StartAsync();
        Closed += (_, _) => Web.Dispose();
    }

    /// <summary>Browser profile lives beside the app's other data, not in Temp.</summary>
    private static string ProfileFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoPlayerV2", "browser");

    private async System.Threading.Tasks.Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(ProfileFolder);
            // An explicit environment is what makes the profile persist; the
            // default would put it beside the executable, which Velopack
            // replaces on every update (signing you out each time).
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileFolder);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            // Keep sign-in pop-ups in this window rather than spawning bare
            // frames the user can't navigate back from.
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (StreamingServices.IsSafeUrl(e.Uri)) core.Navigate(e.Uri);
            };
            core.SourceChanged += (_, _) => OnNavigated();
            core.NavigationCompleted += (_, _) => OnNavigated();
            core.DocumentTitleChanged += (_, _) => PageTitle.Text = core.DocumentTitle;

            core.Navigate(_startUrl);
        }
        catch (Exception ex)
        {
            // Most likely the WebView2 runtime is missing. Nothing here is
            // essential — the browser hand-off does the same job.
            FailText.Text = $"{ex.Message}\n\nYour normal browser works fine for this.";
            FailPanel.Visibility = Visibility.Visible;
        }
    }

    private void OnNavigated()
    {
        var core = Web.CoreWebView2;
        if (core is null) return;

        _currentUrl = core.Source;
        BackBtn.IsEnabled = core.CanGoBack;
        ForwardBtn.IsEnabled = core.CanGoForward;
        if (!string.IsNullOrEmpty(core.DocumentTitle)) PageTitle.Text = core.DocumentTitle;

        DrmBar.Visibility = StreamingServices.IsProtectedPlayer(_currentUrl)
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2?.CanGoBack == true) Web.CoreWebView2.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2?.CanGoForward == true) Web.CoreWebView2.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Reload();

    /// <summary>Hand the page the user is on to their real browser and step aside.</summary>
    private void Handoff_Click(object sender, RoutedEventArgs e)
    {
        var url = string.IsNullOrWhiteSpace(_currentUrl) ? _startUrl : _currentUrl;
        if (!StreamingServices.Launch(url))
            MessageBox.Show(this, "Couldn't open your browser.", "Vault Movies",
                MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
