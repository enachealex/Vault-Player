using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VideoPlayer.App.Models;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// Watch Party entry: host flow (pick movie → lobby with room code + roster →
/// start) and guest flow (host address + code → wait → follow the host into
/// the player). The PartySession is handed to PlayerView, which owns it from
/// then on.
/// </summary>
public partial class PartyView : UserControl, IDisposable
{
    private PartySession? _session;
    private MovieItem? _movie;
    private readonly ObservableCollection<string> _roster = new();
    private bool _handedOff;

    public PartyView()
    {
        InitializeComponent();
        RosterList.ItemsSource = _roster;
        // No accounts — just a name for this party. Remembered once chosen, but
        // never silently taken from the Windows account.
        NameBox.Text = AppServices.Settings.DisplayName ?? "";
        UpdateNameState();
        Loaded += (_, _) => NameBox.Focus();
    }

    private string DisplayName => NameBox.Text.Trim();

    private bool HasName => DisplayName.Length >= 2;

    private void NameBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateNameState();

    private void UpdateNameState()
    {
        HostBtn.IsEnabled = JoinBtn.IsEnabled = HasName;
        NameHint.Text = DisplayName.Length == 0
            ? "Enter a username to continue — this is what friends will see."
            : HasName
                ? "This is the name everyone in the party sees."
                : "A little longer, please (at least 2 characters).";
    }

    private void SaveName()
    {
        AppServices.Settings.DisplayName = DisplayName;
        AppServices.Settings.Save();
    }

    private void HomeBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new HomeView());

    // ---- Host flow ---------------------------------------------------------

    private void HostBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveName();
        var folder = AppServices.Settings.LastFolder;
        if (folder is null || !Directory.Exists(folder))
        {
            MessageBox.Show("Pick your movies folder first (Watch Solo → Choose folder).",
                "No library yet", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        BuildPickList();
        ChoosePanel.Visibility = Visibility.Collapsed;
        PickPanel.Visibility = Visibility.Visible;
    }

    /// <summary>Local films first, then streaming titles. Also used when swapping mid-lobby.</summary>
    private void BuildPickList()
    {
        var folder = AppServices.Settings.LastFolder;
        if (folder is null || !Directory.Exists(folder)) return;

        PickList.Children.Clear();
        foreach (var movie in MovieLibrary.Scan(folder))
        {
            var btn = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = movie.Name,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(14, 9, 14, 9),
                Tag = movie,
            };
            System.Windows.Automation.AutomationProperties.SetName(btn, "Host " + movie.Name);
            btn.Click += PickMovie_Click;
            PickList.Children.Add(btn);
        }

        // Streaming titles can be hosted too — the room syncs the start cue
        // rather than the video, since we can't relay DRM-protected content.
        foreach (var item in StreamingServices.AsMovieItems(AppServices.Settings.Shortcuts))
        {
            var btn = new Button
            {
                Style = (Style)FindResource("GhostButton"),
                Content = $"{item.Name}   ·   {item.Service} (synced start only)",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 3, 0, 3),
                Padding = new Thickness(14, 9, 14, 9),
                Tag = item,
            };
            System.Windows.Automation.AutomationProperties.SetName(btn, "Host " + item.Name);
            btn.Click += PickShortcut_Click;
            PickList.Children.Add(btn);
        }
    }

    private async void PickShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieItem item) return;
        _movie = item;
        PickPanel.Visibility = Visibility.Collapsed;
        LobbyMovie.Text = "Preparing…";
        LobbyPanel.Visibility = Visibility.Visible;
        try
        {
            if (_switching && _session is not null)
            {
                _switching = false;
                await _session.SwitchToExternalAsync(
                    item.Name, item.Service ?? "your service", item.Path);
                ShowLobbyFor(item.Name, item.Service);
                return;
            }

            _session = await PartySession.HostExternalAsync(
                item.Name, item.Service ?? "your service", item.Path,
                DisplayName, AppServices.Settings.RendezvousServer);
            _session.RosterChanged += OnRoster;
            _session.Closed += OnClosed;
            _session.CountdownStarted += OnCountdown;

            LobbyCode.Text = _session.RoomCode;
            LobbyAddress.Text = AppServices.Settings.RendezvousServer is null
                ? $"Friends on your network join with server  {_session.ShareAddress}  and this code"
                : "Friends join with just this code";
            ShowLobbyFor(item.Name, item.Service);
            _roster.Clear();
            _roster.Add(DisplayName + " (host)");
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Couldn't host", MessageBoxButton.OK, MessageBoxImage.Warning);
            _session?.Dispose();
            _session = null;
            LobbyPanel.Visibility = Visibility.Collapsed;
            ChoosePanel.Visibility = Visibility.Visible;
        }
    }

    private async void PickMovie_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieItem movie) return;
        _movie = movie;
        PickPanel.Visibility = Visibility.Collapsed;
        LobbyMovie.Text = "Preparing…";
        LobbyPanel.Visibility = Visibility.Visible;
        try
        {
            // Probe duration so guests get the full length (their relayed stream
            // may not report it). Runs on the UI-thread async flow (libVLC rule).
            if (movie.DurationMs is null or 0)
                await MovieLibrary.ProbeDurationsAsync(new[] { movie }, System.Threading.CancellationToken.None);

            // Swapping inside an existing room: keep the code and the guests.
            if (_switching && _session is not null)
            {
                _switching = false;
                await _session.SwitchToLocalAsync(movie);
                ShowLobbyFor(movie.Name, null);
                return;
            }

            _session = await PartySession.HostAsync(movie, DisplayName, AppServices.Settings.RendezvousServer);
            _session.RosterChanged += OnRoster;
            _session.ReadyChanged += OnReady;
            _session.Closed += OnClosed;
            ShowLobbyFor(movie.Name, null);
            LobbyCode.Text = _session.RoomCode;
            LobbyAddress.Text = AppServices.Settings.RendezvousServer is null
                ? $"Friends on your network join with server  {_session.ShareAddress}  and this code"
                : "Friends join with just this code";
            _roster.Clear();
            _roster.Add(DisplayName + " (host)");
            LobbyPanel.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Couldn't host", MessageBoxButton.OK, MessageBoxImage.Warning);
            _session?.Dispose();
            _session = null;
            LobbyPanel.Visibility = Visibility.Collapsed;
            ChoosePanel.Visibility = Visibility.Visible;
        }
    }

    private async void StartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _movie is null) return;

        if (_session.IsExternal)
        {
            // Everyone counts down locally and presses play together; there's
            // no player to hand off to for a streaming title.
            const int seconds = 5;
            await _session.SendCountdownAsync(seconds, _movie.Name,
                _session.ExternalService ?? "your service", _session.ExternalUrl ?? "");
            OnCountdown(seconds, _movie.Name,
                _session.ExternalService ?? "your service", _session.ExternalUrl ?? "");
            return;
        }

        await _session.SendStartAsync();
        _handedOff = true;
        MainWindow.Instance.Navigate(new PlayerView(_movie, null, _session));
    }

    // ---- Synchronised start for streaming titles ---------------------------

    private DispatcherTimer? _countdownTimer;
    private int _countdownLeft;
    private string _countdownUrl = "";

    private void OnCountdown(int seconds, string title, string service, string url)
    {
        Dispatcher.Invoke(() =>
        {
            _countdownUrl = url;
            _countdownLeft = Math.Max(1, seconds);
            CountdownTitle.Text = title;
            CountdownService.Text = $"on {service}";
            CountdownNumber.Text = _countdownLeft.ToString();
            CountdownHint.Text = "Get the title open and paused, then press play on zero.";
            CountdownOpenBtn.Visibility = StreamingServices.IsSafeUrl(url)
                ? Visibility.Visible : Visibility.Collapsed;
            CountdownDismissBtn.Visibility = Visibility.Collapsed;
            CountdownOverlay.Visibility = Visibility.Visible;

            _countdownTimer?.Stop();
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += CountdownTick;
            _countdownTimer.Start();
        });
    }

    private void CountdownTick(object? sender, EventArgs e)
    {
        _countdownLeft--;
        if (_countdownLeft > 0)
        {
            CountdownNumber.Text = _countdownLeft.ToString();
            return;
        }

        _countdownTimer?.Stop();
        _countdownTimer = null;
        CountdownNumber.Text = "PLAY";
        CountdownHint.Text = "Press play now. Out of sync? The host can start another countdown.";
        CountdownDismissBtn.Visibility = Visibility.Visible;
    }

    private void CountdownOpen_Click(object sender, RoutedEventArgs e) =>
        StreamingServices.Launch(_countdownUrl);

    // ---- Host cues (streaming rooms) ---------------------------------------
    // The app can't reach into someone else's Prime player, so "host controls
    // playback" here means the host broadcasts an instruction everyone sees.

    /// <summary>Guest side of a host pause/resume cue in a streaming room.</summary>
    private void OnHostCue()
    {
        if (_session is null || !_session.IsExternal) return;
        ShowCue(_session.BeaconPlaying, "The host");
    }

    private void CuePause_Click(object sender, RoutedEventArgs e) => SendCue(false);
    private void CueResume_Click(object sender, RoutedEventArgs e) => SendCue(true);

    private void SendCue(bool playing)
    {
        if (_session is null) return;
        _session.PushState(0, playing, force: true);
        ShowCue(playing, DisplayName);
    }

    private void ShowCue(bool playing, string who)
    {
        Dispatcher.Invoke(() =>
        {
            _countdownTimer?.Stop();
            _countdownTimer = null;
            CountdownTitle.Text = _session?.MovieTitle ?? "";
            CountdownService.Text = playing ? $"{who} resumed" : $"{who} paused";
            CountdownNumber.Text = playing ? "PLAY" : "PAUSE";
            CountdownHint.Text = playing
                ? "Press play in your player now."
                : "Pause your player now — the host will resume when everyone's ready.";
            CountdownOpenBtn.Visibility = StreamingServices.IsSafeUrl(_countdownUrl)
                ? Visibility.Visible : Visibility.Collapsed;
            CountdownDismissBtn.Visibility = Visibility.Visible;
            CountdownOverlay.Visibility = Visibility.Visible;
        });
    }

    // ---- Changing the film without dropping the room -----------------------

    private bool _switching;

    /// <summary>Puts the lobby into local-file or streaming shape.</summary>
    private void ShowLobbyFor(string title, string? service)
    {
        var external = service is not null;
        LobbyMovie.Text = external
            ? $"Hosting “{title}” on {service} — everyone plays it in their own app"
            : $"Hosting “{title}”";
        ReadyText.Text = external
            ? "DRM means the app can't drive their players — it syncs the cue, not the video."
            : "";
        StartBtn.Content = external ? "Start countdown" : "Start watching";
        System.Windows.Automation.AutomationProperties.SetName(
            StartBtn, external ? "Start countdown" : "Start watching");
        HostCuePanel.Visibility = external ? Visibility.Visible : Visibility.Collapsed;
        PickPanel.Visibility = Visibility.Collapsed;
        LobbyPanel.Visibility = Visibility.Visible;
    }

    private void ChangeFilmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        _switching = true;
        BuildPickList();
        LobbyPanel.Visibility = Visibility.Collapsed;
        PickPanel.Visibility = Visibility.Visible;
    }

    private void CountdownDismiss_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer?.Stop();
        _countdownTimer = null;
        CountdownOverlay.Visibility = Visibility.Collapsed;
    }

    // ---- Guest flow --------------------------------------------------------

    private void JoinBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveName();
        ChoosePanel.Visibility = Visibility.Collapsed;
        JoinPanel.Visibility = Visibility.Visible;
        HostAddressBox.Text = AppServices.Settings.LastPartyAddress
            ?? AppServices.Settings.RendezvousServer ?? "";
    }

    private async void JoinGoBtn_Click(object sender, RoutedEventArgs e)
    {
        SaveName();
        var address = HostAddressBox.Text.Trim();
        var code = CodeBox.Text.Trim();
        if (address.Length == 0 || code.Length == 0)
        {
            JoinStatus.Text = "Enter the party server and the room code.";
            return;
        }
        JoinGoBtn.IsEnabled = false;
        JoinStatus.Text = "Connecting…";
        try
        {
            _session = await PartySession.JoinAsync(address, code, DisplayName);
            AppServices.Settings.LastPartyAddress = address;
            AppServices.Settings.Save();
            _session.Closed += OnClosed;

            // The host can swap the film from the lobby without dropping the room.
            _session.SwitchedFilm += (title, service) => Dispatcher.Invoke(() =>
            {
                if (_handedOff) return;
                JoinStatus.Text = service is null
                    ? $"In room {_session.RoomCode} — now watching “{title}”. Waiting for the host…"
                    : $"In room {_session.RoomCode} — now “{title}” on {service}. "
                      + "Open it there and wait for the countdown.";
            });

            if (_session.IsExternal)
            {
                // Nothing to play here — wait in the lobby for the host's cues.
                _session.CountdownStarted += OnCountdown;
                _session.BeaconUpdated += OnHostCue;
                JoinStatus.Text = $"In room {_session.RoomCode} — “{_session.MovieTitle}” on "
                    + $"{_session.ExternalService}. Open it there and wait for the countdown.";
                JoinGoBtn.IsEnabled = false;
                return;
            }

            var remote = new MovieItem
            {
                Name = _session.MovieTitle ?? "Watch Party",
                Path = _session.MediaUrl ?? "",
                SizeBytes = 0,
                DurationMs = _session.DurationMs > 0 ? _session.DurationMs : null,
            };

            if (_session.AlreadyStarted)
            {
                _handedOff = true;
                MainWindow.Instance.Navigate(new PlayerView(remote, null, _session));
                return;
            }

            JoinStatus.Text = $"In room {_session.RoomCode} — waiting for the host to start…";
            _session.Started += () =>
            {
                if (_handedOff) return;
                _handedOff = true;
                MainWindow.Instance.Navigate(new PlayerView(remote, null, _session));
            };
        }
        catch (Exception ex)
        {
            JoinStatus.Text = ex.Message;
            _session?.Dispose();
            _session = null;
            JoinGoBtn.IsEnabled = true;
        }
    }

    // ---- Shared ------------------------------------------------------------

    private void OnRoster(string[] members)
    {
        _roster.Clear();
        foreach (var member in members) _roster.Add(member);
        UpdateBandwidthNote(Math.Max(0, members.Length - 1)); // exclude the host
    }

    /// <summary>
    /// The host uploads a full copy of the film to every guest simultaneously,
    /// so the requirement scales linearly. Typical home upstream is well under
    /// what a 4K remux needs for even one guest — worth saying before the film
    /// starts buffering rather than after.
    /// </summary>
    private void UpdateBandwidthNote(int guests)
    {
        if (_session is null || _session.IsExternal || _movie is null
            || _movie.DurationMs is not > 0 || _movie.SizeBytes <= 0 || guests <= 0)
        {
            BandwidthNote.Visibility = Visibility.Collapsed;
            return;
        }

        var mbps = _movie.SizeBytes * 8.0 / (_movie.DurationMs.Value / 1000.0) / 1_000_000.0;
        var needed = mbps * guests;

        BandwidthNote.Text =
            $"This film averages {mbps:0.#} Mbps, so {guests} guest{(guests == 1 ? "" : "s")} "
            + $"need{(guests == 1 ? "s" : "")} about {needed:0.#} Mbps of upload from you.";
        if (needed > 25)
        {
            BandwidthNote.Text += " That's more than most home connections upload — expect buffering.";
            BandwidthNote.Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush");
        }
        else
        {
            BandwidthNote.Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush");
        }
        BandwidthNote.Visibility = Visibility.Visible;
    }

    private void OnReady(int ready, int total)
    {
        ReadyText.Text = total == 0 ? "" : $"{ready} of {total} guest{(total == 1 ? "" : "s")} ready to watch";
    }

    private void OnClosed(string reason)
    {
        if (_handedOff) return;
        MessageBox.Show(reason, "Watch Party", MessageBoxButton.OK, MessageBoxImage.Information);
        MainWindow.Instance.Navigate(new HomeView());
    }

    public void Dispose()
    {
        // Only tear the session down if it was NOT handed to the player.
        if (!_handedOff) _session?.Dispose();
    }
}
