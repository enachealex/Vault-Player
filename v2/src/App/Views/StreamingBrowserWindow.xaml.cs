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

    /// <summary>
    /// Browser profile: local app data, not roaming.
    ///
    /// This is a cache — 127 MB of it after a little browsing, mostly compiled
    /// JavaScript and images. Roaming app data is for settings small enough to
    /// follow a person between machines, and on a domain-joined PC it is copied
    /// at every logon; a browser cache there is both wrong and slow. Local is
    /// where Edge itself keeps this.
    /// </summary>
    internal static string ProfileFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoPlayerV2", "browser");

    /// <summary>Where it used to live, so an existing profile can be moved rather than lost.</summary>
    private static string LegacyProfileFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VideoPlayerV2", "browser");

    /// <summary>
    /// Move an existing profile across on first run after the change. Without
    /// this everyone is silently signed out of every service they had set up.
    /// A failure is not fatal: the worst case is signing in again.
    /// </summary>
    private static void MigrateProfileIfNeeded()
    {
        try
        {
            if (Directory.Exists(ProfileFolder) || !Directory.Exists(LegacyProfileFolder)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(ProfileFolder)!);
            Directory.Move(LegacyProfileFolder, ProfileFolder);
            StartupTrace.Note("browser: moved profile from roaming to local app data");
        }
        catch (Exception ex)
        {
            StartupTrace.Note($"browser: could not move the old profile ({ex.Message}); starting fresh");
        }
    }

    private async System.Threading.Tasks.Task StartAsync()
    {
        try
        {
            MigrateProfileIfNeeded();
            Directory.CreateDirectory(ProfileFolder);
            // An explicit environment is what makes the profile persist; the
            // default would put it beside the executable, which Velopack
            // replaces on every update (signing you out each time).
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileFolder);
            StartupTrace.Note($"browser: environment ready in {clock.ElapsedMilliseconds} ms " +
                              $"(runtime {env.BrowserVersionString})");

            clock.Restart();
            await Web.EnsureCoreWebView2Async(env);
            StartupTrace.Note($"browser: control ready in {clock.ElapsedMilliseconds} ms");

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

            // Timing for "why is this slow". Silent unless VAULT_STARTUP_TRACE
            // is set, same as the launch profiler.
            core.NavigationStarting += (_, e) =>
            {
                _navClock.Restart();
                SetBusy(true);
                StartupTrace.Note($"browser: -> {Shorten(e.Uri)}");
            };
            core.NavigationCompleted += async (_, e) =>
            {
                SetBusy(false);
                StartupTrace.Note($"browser: finished in {_navClock.ElapsedMilliseconds} ms  " +
                                  $"ok={e.IsSuccess} status={e.HttpStatusCode}");
                await ReportRendererOnceAsync(core);
            };
            // A site can paint long before it finishes loading; drop the bar
            // then, so it reflects when the page is usable rather than when the
            // last tracking pixel arrives. The gap between these two numbers is
            // the whole story on a streaming site: the browse grid is usable in
            // seconds while trailers and box art keep loading for a minute.
            core.DOMContentLoaded += (_, _) =>
            {
                SetBusy(false);
                StartupTrace.Note($"browser: usable after {_navClock.ElapsedMilliseconds} ms (DOM ready)");
            };

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

    /// <summary>
    /// Show that a page is on its way. Streaming sites routinely take several
    /// seconds before painting anything, and with no indicator a slow load and
    /// a broken one are indistinguishable — which is most of why this felt bad.
    /// </summary>
    private void SetBusy(bool busy)
    {
        LoadBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        BusyDots.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) PageTitle.Text = "Loading…";
    }

    private readonly System.Diagnostics.Stopwatch _navClock = new();
    private bool _rendererReported;

    private static string Shorten(string url) => url.Length <= 70 ? url : url[..70] + "…";

    /// <summary>
    /// Ask the page which renderer it actually got, once per window.
    ///
    /// This is the difference between "the network is slow" and "everything is
    /// being drawn in software". WebView2 falls back to SwiftShader when it
    /// cannot get at the GPU, and the symptom is exactly what gets reported
    /// here — every page feels heavy, regardless of connection.
    /// </summary>
    private async System.Threading.Tasks.Task ReportRendererOnceAsync(CoreWebView2 core)
    {
        if (_rendererReported) return;
        _rendererReported = true;
        try
        {
            var js = @"(function(){try{
                var c=document.createElement('canvas');
                var g=c.getContext('webgl')||c.getContext('experimental-webgl');
                if(!g) return 'no webgl';
                var d=g.getExtension('WEBGL_debug_renderer_info');
                return d ? g.getParameter(d.UNMASKED_RENDERER_WEBGL) : 'no renderer info';
            }catch(e){return 'error: '+e.message}})()";
            var renderer = await core.ExecuteScriptAsync(js);
            StartupTrace.Note($"browser: renderer = {renderer}");
        }
        catch (Exception ex)
        {
            StartupTrace.Note($"browser: renderer probe failed: {ex.Message}");
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
