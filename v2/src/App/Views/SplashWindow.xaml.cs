using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Velopack;
using Velopack.Sources;

namespace VideoPlayer.App.Views;

/// <summary>
/// Startup screen that doubles as the update check. It stays quiet unless
/// there's actually something to install: no update means no message, just a
/// brief loading screen while the check runs.
/// </summary>
public partial class SplashWindow : Window
{
    private const string RepoUrl = "https://github.com/enachealex/Vault-Player";

    /// <summary>Don't let a slow or unreachable network hold the app hostage.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(8);

    private UpdateManager? _manager;
    private UpdateInfo? _update;
    private TaskCompletionSource<bool>? _choice;

    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Returns when the app should carry on starting. If the user accepts an
    /// update this never returns — the process restarts into the new version.
    /// </summary>
    public async Task RunAsync()
    {
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));

            // Only an installed copy can update itself; a dev build or the
            // portable zip has no install to replace.
            if (!_manager.IsInstalled) return;

            StatusText.Text = "Checking for updates…";
            var check = _manager.CheckForUpdatesAsync();
            if (await Task.WhenAny(check, Task.Delay(CheckTimeout)) != check) return;

            _update = await check;
            if (_update is null) return; // up to date: say nothing, just start

            var version = _update.TargetFullRelease.Version;
            StatusText.Text = $"Version {version} is available.";
            Progress.IsIndeterminate = false;
            Progress.Value = 0;
            UpdatePrompt.Visibility = Visibility.Visible;

            _choice = new TaskCompletionSource<bool>();
            if (!await _choice.Task) return; // "Not now" — carry on to the app

            UpdatePrompt.Visibility = Visibility.Collapsed;
            StatusText.Text = "Downloading update…";
            await _manager.DownloadUpdatesAsync(_update, p => Dispatcher.Invoke(() => Progress.Value = p));

            StatusText.Text = "Restarting…";
            _manager.ApplyUpdatesAndRestart(_update);
        }
        catch (Exception ex)
        {
            // An update problem must never stop someone watching a film.
            StatusText.Text = "Couldn't check for updates.";
            System.Diagnostics.Debug.WriteLine($"Update check failed: {ex}");
            await Task.Delay(900);
        }
    }

    private void Update_Click(object sender, RoutedEventArgs e) => _choice?.TrySetResult(true);
    private void Skip_Click(object sender, RoutedEventArgs e) => _choice?.TrySetResult(false);
}
