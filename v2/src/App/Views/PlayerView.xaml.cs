using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using VideoPlayer.App.Models;
using VideoPlayer.App.Services;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoPlayer.App.Views;

/// <summary>
/// The full player: native libVLC playback of any codec, v1-parity controls
/// (playback modes, ±10sec, speed, volume, scrub preview, fullscreen,
/// keyboard shortcuts, resume), embedded audio/subtitle track pickers, and
/// casting to Chromecast-class (libVLC renderer) or DLNA devices.
/// </summary>
public partial class PlayerView : UserControl, IDisposable
{
    private static readonly double[] Rates = { 0.5, 0.75, 1.0, 1.25, 1.5, 2.0 };
    private static readonly string[] SubtitleExts = { ".srt", ".vtt", ".ass", ".sub" };

    private readonly MovieItem _movie;
    private readonly IReadOnlyList<MovieItem> _playlist;
    private readonly int _index;
    private MediaPlayer? _player;
    private readonly DispatcherTimer _uiTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _initializing = true;
    private int _tick;

    // Fullscreen state
    private bool _fullscreen;

    private WindowState _prevWindowState;

    // DLNA session state (this view owns the session; leaving the player stops it).
    private DlnaRenderer? _dlnaDevice;
    private MediaHttpServer? _dlnaServer;
    private bool _dlnaPaused;
    private TimeSpan _dlnaPos;
    private TimeSpan _dlnaDur;
    private bool _dlnaPollBusy;

    // Watch Party session (null when watching solo). Host or guest.
    private readonly PartySession? _party;
    private bool PartyGuest => _party is { IsHost: false };
    /// <summary>Last host play/pause state a guest saw, so we only toast on change.</summary>
    private bool? _lastSeenHostPlaying;
    private bool PartyHost => _party is { IsHost: true };
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _chatLines = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _partyRoster = new();

    public PlayerView(MovieItem movie, IReadOnlyList<MovieItem>? playlist = null, PartySession? party = null)
    {
        InitializeComponent();
        _movie = movie;
        _party = party;
        _playlist = playlist ?? new[] { movie };
        _index = Math.Max(0, ((List<MovieItem>)_playlist.ToList()).FindIndex(m => m.Path == movie.Path));
        // Guests get the room identity from the shared-screen badge instead.
        TitleText.Text = party is null || party is { IsHost: false }
            ? movie.Name
            : $"{movie.Name} — Watch Party {party.RoomCode}";
        Loaded += OnLoaded;
    }

    private bool DlnaActive => _dlnaDevice is not null;
    private string Mode => AppServices.Settings.PlaybackMode;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_player is not null) return; // Loaded can re-fire
        MainWindow.Instance.SetTitleContext(_movie.Name);
        _player = new MediaPlayer(AppServices.LibVlc);
        Video.MediaPlayer = _player;
        _player.EndReached += (_, _) => Dispatcher.BeginInvoke(OnEnded);

        AppServices.Cast.EnsureStarted();
        // DLNA sessions don't survive navigation (the media server lives here).
        if (AppServices.Cast.Active is DlnaTarget) AppServices.Cast.Active = null;

        // Apply persisted playback preferences.
        var s = AppServices.Settings;
        VolumeSlider.Value = s.Volume;
        _initializing = false;

        var resume = s.ResumePositions.TryGetValue(_movie.Path, out var pos) ? pos : 0;
        var durationMs = _movie.DurationMs ?? 0;
        if (resume < 5000 || (durationMs > 0 && resume > durationMs - 15000)) resume = 0;

        StartPlayback(resume);
        UpdateCastUi();
        UpdateAutoplayUi();
        UpdateRateUi();
        UpdateVolumeUi();
        PrevBtn.IsEnabled = NextBtn.IsEnabled = _playlist.Count > 1;

        _uiTimer.Tick += (_, _) => { UpdateUi(); UpdateIdleState(); UpdateVolumePopupState(); };
        _uiTimer.Start();

        // Owned click/drag on both sliders: press anywhere jumps there, drags
        // are smooth, release commits. (WPF's built-in track behaviour is not
        // reliable enough for the most-used control in the app.)
        _seekDrag = Helpers.SliderDragBehavior.Attach(Seek,
            onPreview: ShowScrubPreview,
            onCommit: _ =>
            {
                ScrubPopup.IsOpen = false;
                ScrubThumbBorder.Visibility = Visibility.Collapsed;
                ApplySeek();
            });
        Seek.MouseMove += Seek_MouseMove;
        Seek.MouseLeave += Seek_MouseLeave;
        // No SliderDragBehavior here: it measures with ActualWidth, so it cannot
        // drive a vertical slider. IsMoveToPointEnabled in the XAML gives the
        // click-to-position behaviour instead.
        VolumeSlider.ValueChanged += VolumeSlider_ValueChanged;

        // Keyboard shortcuts must work no matter which window has focus —
        // including libVLC's floating overlay window that hosts our video UI.
        MainWindow.Instance.PreviewKeyDown += OnKeyDown;
        CastBanner.Loaded += (_, _) =>
        {
            var overlayWindow = Window.GetWindow(CastBanner);
            if (overlayWindow is not null && !ReferenceEquals(overlayWindow, MainWindow.Instance)
                && !ReferenceEquals(overlayWindow, _overlayWindow))
            {
                _overlayWindow = overlayWindow;
                _overlayWindow.PreviewKeyDown += OnKeyDown;
            }
        };

        // If the cast menu is open while discovery lands, refresh it in place.
        AppServices.Cast.Devices.CollectionChanged += OnCastDevicesChanged;

        // ---- Watch Party wiring ----
        if (_party is not null)
        {
            ChatBtn.Visibility = Visibility.Visible;
            ChatList.ItemsSource = _chatLines;
            // Remember what to reopen if the user wanders back to the library.
            AppServices.CurrentParty ??= _party;
            AppServices.CurrentPartyMovie = _movie;

            // The paused card shows who is in the room, so the roster has to be
            // tracked here as well as in the lobby.
            PausedRoster.ItemsSource = _partyRoster;
            PausedTitle.Text = _movie.Name;
            PausedCode.Text = _party.RoomCode;
            _party.RosterChanged += members => Dispatcher.Invoke(() =>
            {
                _partyRoster.Clear();
                foreach (var member in members) _partyRoster.Add(member);
            });
            _party.ChatReceived += (who, text) => AddChatLine($"{who}: {text}");
            _party.Closed += reason =>
            {
                // The room is genuinely gone, so the app is no longer in a party.
                if (ReferenceEquals(AppServices.CurrentParty, _party)) AppServices.CurrentParty = null;
                MessageBox.Show(reason, "Watch Party", MessageBoxButton.OK, MessageBoxImage.Information);
                MainWindow.Instance.Navigate(new HomeView());
            };
            if (PartyHost)
            {
                _party.GuestJoined += who => ShowToast($"{who} joined the party", null, null);
                _party.PauseRequested += (who, kind) =>
                    ShowToast($"{who} asked to {kind}",
                        kind == "pause" ? "Pause" : "Resume",
                        () =>
                        {
                            TogglePlay();
                            PushPartyState(force: true);
                        });
            }
            if (PartyGuest)
            {
                // You're watching someone else's screen: hide what you can't do
                // rather than showing a row of dead buttons. Volume, subtitles
                // and fullscreen stay — those are yours, like your own TV.
                AutoplayBtn.Visibility = Visibility.Collapsed;
                CastBtn.Visibility = Visibility.Collapsed;
                RateBtn.Visibility = Visibility.Collapsed;
                PrevBtn.Visibility = NextBtn.Visibility = Visibility.Collapsed;
                PlayBtn.Visibility = Visibility.Collapsed;
                Back10Btn.Visibility = Visibility.Collapsed;
                Fwd10Btn.Visibility = Visibility.Collapsed;

                // The timeline becomes a read-out, not a control. It already
                // refused to seek, but the handle still moved under the mouse
                // and snapped back -- which reads as broken rather than
                // deliberate. No hit-testing also means no hover preview, which
                // a guest could not have used anyway: their source is a relay
                // stream ffmpeg cannot seek.
                Seek.IsHitTestVisible = false;
                Seek.Focusable = false;
                SharedScreenBadge.Visibility = Visibility.Visible;
                SharedScreenText.Text = $"Watching {_party!.HostName}'s screen";
            }
        }
    }

    private void AddChatLine(string line)
    {
        _chatLines.Add(line);
        if (_chatLines.Count > 200) _chatLines.RemoveAt(0);
        ChatScroll.ScrollToEnd();
    }

    private void ChatBtn_Click(object sender, RoutedEventArgs e) =>
        ChatPanel.Visibility = ChatPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _party is null) return;
        var text = ChatInput.Text.Trim();
        if (text.Length == 0) return;
        ChatInput.Clear();
        _ = _party.SendChatAsync(text);
        if (PartyHost) AddChatLine($"{_party.DisplayName}: {text}"); // host echo (server broadcasts to guests only)
        e.Handled = true;
    }

    /// <summary>Host toast with an optional action button; auto-dismisses.</summary>
    private void ShowToast(string text, string? actionLabel, Action? action)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, actionLabel is null ? 0 : 10, 0),
        });
        var toast = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1A, 0x22)),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 9),
            Margin = new Thickness(0, 0, 0, 8),
            Child = panel,
        };
        if (actionLabel is not null && action is not null)
        {
            var btn = new Button
            {
                Style = (Style)FindResource("PrimaryButton"),
                Content = actionLabel,
                Padding = new Thickness(12, 5, 12, 5),
            };
            btn.Click += (_, _) =>
            {
                action();
                ToastPanel.Children.Remove(toast);
            };
            panel.Children.Add(btn);
        }
        ToastPanel.Children.Add(toast);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ToastPanel.Children.Remove(toast);
        };
        timer.Start();
    }

    private void PushPartyState(bool force = false)
    {
        if (!PartyHost || _player is null) return;
        _party!.PushState(_player.Time, _player.IsPlaying, force);
    }

    private Helpers.SliderDragHandle? _seekDrag;
    private Window? _overlayWindow;

    private DateTime _lastLiveSeek = DateTime.MinValue;

    /// <summary>Minimum gap between live seeks while dragging. Below roughly this,
    /// libVLC spends longer seeking than showing frames and the drag feels worse.</summary>
    private static readonly TimeSpan LiveSeekInterval = TimeSpan.FromMilliseconds(90);

    private void ShowScrubPreview(double value)
    {
        if (Seek.ActualWidth <= 0) return;
        var fraction = value / Seek.Maximum;
        var durationMs = DlnaActive ? _dlnaDur.TotalMilliseconds : _player?.Length ?? 0;
        var targetMs = (long)(durationMs * fraction);

        ScrubText.Text = Fmt(targetMs);
        ShowScrubPopupAt(fraction);

        // Move the picture with the handle so you can see where you are landing.
        // Throttled, because a drag fires far faster than libVLC can seek and
        // queuing every pixel makes it lurch rather than glide.
        if (!PartyGuest && !DlnaActive && _player is not null && durationMs > 0)
        {
            var now = DateTime.UtcNow;
            if (now - _lastLiveSeek >= LiveSeekInterval)
            {
                _lastLiveSeek = now;
                try { _player.Time = targetMs; } catch { /* torn down mid-drag */ }
            }
        }
    }

    private void ShowScrubPopupAt(double fraction)
    {
        var width = ScrubThumbBorder.Visibility == Visibility.Visible ? 106 : 30;
        ScrubPopup.HorizontalOffset = fraction * Seek.ActualWidth - width;
        ScrubPopup.VerticalOffset = ScrubThumbBorder.Visibility == Visibility.Visible ? -140 : -36;
        ScrubPopup.IsOpen = true;
    }

    // ---- Timeline hover preview --------------------------------------------

    private CancellationTokenSource? _hoverCts;
    private double _hoverBucket = -1;

    private void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (_seekDrag?.IsDragging == true) return;   // dragging has its own preview
        if (Seek.ActualWidth <= 0) return;

        var fraction = Math.Clamp(e.GetPosition(Seek).X / Seek.ActualWidth, 0, 1);
        var durationMs = DlnaActive ? _dlnaDur.TotalMilliseconds : _player?.Length ?? 0;
        if (durationMs <= 0) return;

        var seconds = durationMs / 1000.0 * fraction;
        ScrubText.Text = Fmt((long)(durationMs * fraction));
        ShowScrubPopupAt(fraction);
        _ = RequestHoverFrameAsync(seconds, fraction);
    }

    private void Seek_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_seekDrag?.IsDragging == true) return;
        _hoverCts?.Cancel();
        ScrubPopup.IsOpen = false;
        ScrubThumbBorder.Visibility = Visibility.Collapsed;
        _hoverBucket = -1;
    }

    /// <summary>
    /// Pull a frame for the hovered position. Only local files: a party guest's
    /// source is an HTTP relay that ffmpeg cannot seek cheaply, and asking it to
    /// would compete with the playback stream for the host's upload.
    /// </summary>
    private async Task RequestHoverFrameAsync(double seconds, double fraction)
    {
        if (IsRemoteSource || _movie.IsShortcut) return;

        var bucket = Math.Round(seconds / 5.0) * 5.0;
        if (Math.Abs(bucket - _hoverBucket) < 0.01) return;  // same frame as last time
        _hoverBucket = bucket;

        _hoverCts?.Cancel();
        var cts = new CancellationTokenSource();
        _hoverCts = cts;
        try
        {
            var frame = await ThumbnailService.ExtractFrameAsync(_movie.Path, bucket, cts.Token);
            if (cts.IsCancellationRequested || frame is null) return;

            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            // OnLoad so the cached jpg is not left open by the UI.
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(frame);
            image.EndInit();
            image.Freeze();

            ScrubThumb.Source = image;
            ScrubThumbBorder.Visibility = Visibility.Visible;
            ShowScrubPopupAt(fraction);
        }
        catch
        {
            // No preview is fine; the timestamp alone is still useful.
        }
    }

    private void OnCastDevicesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (_castMenu?.IsOpen == true) FillCastMenu(_castMenu);
    }

    private bool IsRemoteSource => _movie.Path.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    /// <summary>(Re)start playback locally or on the active libVLC renderer.</summary>
    private void StartPlayback(long resumeMs)
    {
        if (_player is null) return;
        _player.Stop();
        _player.SetRenderer((AppServices.Cast.Active as VlcTarget)?.Item!);

        using var media = new Media(AppServices.LibVlc, _movie.Path,
            IsRemoteSource ? FromType.FromLocation : FromType.FromPath);
        if (resumeMs > 500)
        {
            var seconds = (resumeMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            media.AddOption($":start-time={seconds}");
        }
        _player.Play(media);

        var s = AppServices.Settings;
        _player.SetRate(PartyGuest ? 1f : (float)s.Rate);
        ApplyAudio();
        if (!IsRemoteSource) AddSidecarSubtitles();
    }

    /// <summary>Sidecar .srt/.vtt/.ass next to the movie become selectable subtitle tracks.</summary>
    private void AddSidecarSubtitles()
    {
        try
        {
            var dir = Path.GetDirectoryName(_movie.Path);
            if (dir is null) return;
            var baseName = Path.GetFileNameWithoutExtension(_movie.Path);
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (!SubtitleExts.Contains(ext)) continue;
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(baseName, StringComparison.OrdinalIgnoreCase)) continue;
                _player?.AddSlave(MediaSlaveType.Subtitle, new Uri(file).AbsoluteUri, false);
            }
        }
        catch
        {
            // Sidecar detection is best-effort.
        }
    }

    // ---- End-of-video behaviour -------------------------------------------

    private bool Autoplay => AppServices.Settings.PlaybackMode != "PlayOnce";

    private void OnEnded()
    {
        if (_party is not null) return; // parties end together; no auto-chaining

        CountWatchOnce();
        AppServices.Settings.ResumePositions.Remove(_movie.Path);
        AppServices.Settings.Save();

        if (Autoplay && _index < _playlist.Count - 1)
            MainWindow.Instance.Navigate(new PlayerView(_playlist[_index + 1], _playlist));
    }

    private void AutoplayBtn_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Settings.PlaybackMode = Autoplay ? "PlayOnce" : "AutoplayNext";
        AppServices.Settings.Save();
        UpdateAutoplayUi();
    }

    private void UpdateAutoplayUi()
    {
        AutoplayBtn.Background = Autoplay
            ? (Brush)FindResource("AccentBrush")
            : (Brush)FindResource("SurfaceAltBrush");
    }

    // ---- Chapters ----------------------------------------------------------

    private void ChaptersBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var s = AppServices.Settings;
        var durationMs = DlnaActive ? (long)_dlnaDur.TotalMilliseconds : _player?.Length ?? 0;

        // Embedded chapters from the file itself (MKVs often have them).
        var embedded = Array.Empty<ChapterDescription>();
        try
        {
            if (_player is not null) embedded = _player.FullChapterDescriptions(-1);
        }
        catch
        {
        }
        if (embedded.Length > 0)
        {
            menu.Items.Add(new MenuItem { Header = "In this film", IsEnabled = false, FontWeight = FontWeights.Bold });
            for (var i = 0; i < embedded.Length; i++)
            {
                var name = string.IsNullOrWhiteSpace(embedded[i].Name) ? $"Chapter {i + 1}" : embedded[i].Name;
                var offset = embedded[i].TimeOffset;
                var item = new MenuItem { Header = $"{name}   {Fmt(offset)}" };
                item.Click += (_, _) => JumpTo(offset);
                menu.Items.Add(item);
            }
        }

        // The user's own marks for this movie.
        if (s.CustomChapters.TryGetValue(_movie.Path, out var marks) && marks.Count > 0)
        {
            menu.Items.Add(new MenuItem { Header = "My chapters", IsEnabled = false, FontWeight = FontWeights.Bold });
            foreach (var mark in marks.OrderBy(m => m.TimeMs))
            {
                var item = new MenuItem { Header = $"{mark.Name}   {Fmt(mark.TimeMs)}" };
                var time = mark.TimeMs;
                item.Click += (_, _) => JumpTo(time);
                menu.Items.Add(item);
            }
        }

        if (menu.Items.Count > 0) menu.Items.Add(new Separator());

        var currentMs = DlnaActive ? (long)_dlnaPos.TotalMilliseconds : _player?.Time ?? 0;
        var add = new MenuItem { Header = $"Add chapter at {Fmt(currentMs)}" };
        add.Click += (_, _) =>
        {
            var list = s.CustomChapters.TryGetValue(_movie.Path, out var existing) ? existing : new List<ChapterMark>();
            list.Add(new ChapterMark { Name = $"Chapter {list.Count + 1}", TimeMs = currentMs });
            s.CustomChapters[_movie.Path] = list;
            s.Save();
        };
        menu.Items.Add(add);

        if (s.CustomChapters.TryGetValue(_movie.Path, out var any) && any.Count > 0)
        {
            var clear = new MenuItem { Header = "Remove my chapters" };
            clear.Click += (_, _) =>
            {
                s.CustomChapters.Remove(_movie.Path);
                s.Save();
            };
            menu.Items.Add(clear);
        }

        menu.PlacementTarget = ChaptersBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    /// <summary>Jump to an absolute position on whichever sink is active.</summary>
    private void JumpTo(long ms)
    {
        if (PartyGuest) return; // host owns the timeline
        if (DlnaActive)
        {
            var target = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            _dlnaPos = target;
            _ = DlnaService.SeekAsync(_dlnaDevice!, target);
            return;
        }
        if (_player is not null) _player.Time = Math.Max(0, ms);
        PushPartyState(force: true);
    }

    // ---- Transport ---------------------------------------------------------

    private void BackBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new LibraryView());

    private void PlayBtn_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        // Guests don't control playback — they ask the host.
        if (PartyGuest)
        {
            var wantPause = _player?.IsPlaying == true;
            _ = _party!.RequestPauseAsync(wantPause);
            AddChatLine($"(you asked the host to {(wantPause ? "pause" : "resume")})");
            ChatPanel.Visibility = Visibility.Visible;
            return;
        }

        if (DlnaActive)
        {
            var device = _dlnaDevice!;
            if (_dlnaPaused) _ = DlnaService.PlayAsync(device);
            else _ = DlnaService.PauseAsync(device);
            _dlnaPaused = !_dlnaPaused;
            return;
        }
        if (_player is null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
        PushPartyState(force: true);
    }

    private void Back10_Click(object sender, RoutedEventArgs e) => SeekBy(-10_000);
    private void Fwd10_Click(object sender, RoutedEventArgs e) => SeekBy(10_000);

    private void SeekBy(long deltaMs)
    {
        if (PartyGuest) return; // host owns the timeline
        if (DlnaActive)
        {
            var target = _dlnaPos + TimeSpan.FromMilliseconds(deltaMs);
            if (target < TimeSpan.Zero) target = TimeSpan.Zero;
            _dlnaPos = target;
            _ = DlnaService.SeekAsync(_dlnaDevice!, target);
            return;
        }
        if (_player is null) return;
        _player.Time = Math.Max(0, _player.Time + deltaMs);
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        var timeMs = DlnaActive ? (long)_dlnaPos.TotalMilliseconds : _player?.Time ?? 0;
        if (timeMs > 3000 || _index == 0)
        {
            if (DlnaActive) SeekBy(-timeMs);
            else if (_player is not null) _player.Time = 0;
        }
        else
        {
            MainWindow.Instance.Navigate(new PlayerView(_playlist[_index - 1], _playlist));
        }
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.Count <= 1) return;
        MainWindow.Instance.Navigate(new PlayerView(_playlist[(_index + 1) % _playlist.Count], _playlist));
    }

    // ---- Seek bar ----------------------------------------------------------

    private void ApplySeek()
    {
        if (PartyGuest) return; // host owns the timeline
        var fraction = Seek.Value / Seek.Maximum;
        if (DlnaActive)
        {
            if (_dlnaDur <= TimeSpan.Zero) return;
            var target = TimeSpan.FromMilliseconds(_dlnaDur.TotalMilliseconds * fraction);
            _dlnaPos = target; // optimistic; poll corrects
            _ = DlnaService.SeekAsync(_dlnaDevice!, target);
            return;
        }
        if (_player is null || _player.Length <= 0) return;
        _player.Position = (float)fraction;
    }

    // ---- Audio / subtitles / rate / volume ---------------------------------

    private void AudioBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || DlnaActive) return;
        ShowTrackMenu(AudioBtn, _player.AudioTrackDescription, _player.AudioTrack,
            id => _player.SetAudioTrack(id));
    }

    private void SubsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null || DlnaActive) return;
        ShowTrackMenu(SubsBtn, _player.SpuDescription, _player.Spu,
            id => _player.SetSpu(id));
    }

    private static void ShowTrackMenu(Button anchor, TrackDescription[] tracks, int currentId, Action<int> select)
    {
        var menu = new ContextMenu();
        if (tracks.Length == 0)
        {
            menu.Items.Add(new MenuItem { Header = "No tracks", IsEnabled = false });
        }
        foreach (var track in tracks)
        {
            var item = new MenuItem { Header = track.Name, IsChecked = track.Id == currentId };
            var id = track.Id;
            item.Click += (_, _) => select(id);
            menu.Items.Add(item);
        }
        menu.PlacementTarget = anchor;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void RateBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var rate in Rates)
        {
            var item = new MenuItem
            {
                Header = $"{rate}×",
                IsChecked = Math.Abs(AppServices.Settings.Rate - rate) < 0.01,
            };
            var captured = rate;
            item.Click += (_, _) =>
            {
                AppServices.Settings.Rate = captured;
                AppServices.Settings.Save();
                _player?.SetRate((float)captured);
                UpdateRateUi();
            };
            menu.Items.Add(item);
        }
        menu.PlacementTarget = RateBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen = true;
    }

    private void UpdateRateUi() =>
        RateText.Text = $"{AppServices.Settings.Rate}×";

    // ---- Volume ------------------------------------------------------------
    //
    // Mute is implemented as volume 0, not libVLC's Mute property.
    // libvlc_audio_set_mute is advisory: it is silently dropped when no audio
    // output exists yet -- which is exactly the moment just after Play -- and
    // libVLC can change the state underneath us. That produced both "shows
    // muted but audible" and the reverse. Volume is deterministic, so
    // Settings.Muted is the single source of truth and the player is only ever
    // told a number.

    private int DesiredVolume => AppServices.Settings.Muted ? 0 : AppServices.Settings.Volume;

    /// <summary>Push the intended audio state onto the player.</summary>
    private void ApplyAudio()
    {
        if (_player is null) return;
        try { _player.Volume = DesiredVolume; }
        catch { /* player torn down mid-call */ }
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Settings;
        s.Muted = !s.Muted;
        s.Save();
        ApplyAudio();
        UpdateVolumeUi();
        ShowVolumePopup();
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var s = AppServices.Settings;
        s.Volume = (int)VolumeSlider.Value;
        // Dragging the slider up is an unmistakable request to hear something.
        if (s.Muted && s.Volume > 0) s.Muted = false;
        s.Save();
        ApplyAudio();
        UpdateVolumeUi();
    }

    // ---- Volume popup ------------------------------------------------------

    private static readonly TimeSpan VolumeIdleBeforeHiding = TimeSpan.FromSeconds(1.5);
    private DateTime? _volumeAwaySince;

    private void VolumeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (VolumePopup.IsOpen) VolumePopup.IsOpen = false;
        else ShowVolumePopup();
    }

    private void ShowVolumePopup()
    {
        VolumePopup.IsOpen = true;
        _volumeAwaySince = DateTime.UtcNow; // closes on its own if nobody comes near
    }

    /// <summary>
    /// Close the volume popup 1.5s after the pointer leaves it. Polled from the
    /// UI tick rather than driven by MouseEnter/MouseLeave: the popup is its own
    /// window, and if it opens without the pointer ever entering, a Leave event
    /// can never arrive and it would hang around forever.
    /// </summary>
    private void UpdateVolumePopupState()
    {
        if (!VolumePopup.IsOpen) { _volumeAwaySince = null; return; }

        var pointerOnIt = VolumeBtn.IsMouseOver
                          || VolumePopupBody.IsMouseOver
                          || VolumeSlider.IsMouseCaptureWithin; // mid-drag counts
        if (pointerOnIt)
        {
            _volumeAwaySince = null;
            return;
        }

        _volumeAwaySince ??= DateTime.UtcNow;
        if (DateTime.UtcNow - _volumeAwaySince > VolumeIdleBeforeHiding) VolumePopup.IsOpen = false;
    }

    private void UpdateVolumeUi()
    {
        var s = AppServices.Settings;
        var glyph = s.Muted || s.Volume == 0 ? "\uE74F" : s.Volume < 50 ? "\uE993" : "\uE767";
        MuteGlyph.Text = glyph;
        MuteToggleGlyph.Text = glyph;
        VolumeReadout.Text = s.Muted ? "Muted" : s.Volume.ToString();
    }

    // ---- Fullscreen --------------------------------------------------------

    // ---- Auto-hiding the playback controls when idle ------------------------
    //
    // Mouse position is POLLED rather than taken from WPF's MouseMove. libVLC
    // renders into its own child window covering the video, so WPF never sees
    // the pointer over most of the screen in fullscreen -- the events simply
    // never arrive. Asking Windows where the cursor is sidesteps that entirely.

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out System.Drawing.Point point);

    private System.Drawing.Point _lastCursor;
    private DateTime _lastActivity = DateTime.UtcNow;

    /// <summary>Called from the UI tick; decides whether the controls belong on screen.</summary>
    private void UpdateIdleState()
    {
        if (GetCursorPos(out var now) && (now.X != _lastCursor.X || now.Y != _lastCursor.Y))
        {
            _lastCursor = now;
            // Only movement over this window counts. Otherwise working in
            // another app -- or on a second monitor -- would keep the controls
            // sitting over the film indefinitely.
            if (IsPointerOverThisView(now)) NoteActivity();
        }

        var idle = DateTime.UtcNow - _lastActivity;
        // Paused means the viewer stopped on purpose and is probably reaching
        // for a control, so leave them up.
        var keepUp = idle < IdleBeforeHiding
                     || _player is null || !_player.IsPlaying
                     || ControlBar.IsMouseOver;   // don't vanish under the pointer

        ControlBar.Visibility = keepUp ? Visibility.Visible : Visibility.Collapsed;
        // The top bar goes with it. Leaving it behind draws the eye to the one
        // strip of furniture still sitting over the film.
        if (!_fullscreen) TopBar.Visibility = keepUp ? Visibility.Visible : Visibility.Collapsed;
        Cursor = keepUp ? null : Cursors.None;

        // Pausing a party brings the lobby back.
        PausedPartyCard.Visibility = _party is not null && _player is not null && !_player.IsPlaying
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CopyRoomCode_Click(object sender, RoutedEventArgs e)
    {
        if (_party is null) return;
        try
        {
            Clipboard.SetText(_party.RoomCode);
            PausedCopyGlyph.Text = char.ConvertFromUtf32(0xE73E); // tick
            var revert = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            revert.Tick += (_, _) =>
            {
                revert.Stop();
                PausedCopyGlyph.Text = char.ConvertFromUtf32(0xE8C8);
            };
            revert.Start();
        }
        catch
        {
            // Another process can hold the clipboard open; not worth interrupting
            // a film over.
        }
    }

    /// <summary>Three seconds is the convention: long enough to aim at a button.</summary>
    private static readonly TimeSpan IdleBeforeHiding = TimeSpan.FromSeconds(3);

    private void NoteActivity() => _lastActivity = DateTime.UtcNow;

    private string _trackInfo = "";
    private bool _trackInfoResolved;

    /// <summary>
    /// Codec/channel label for the status line, worked out once per film.
    ///
    /// This used to read <c>_player.Media?.Tracks</c> on every UI tick. Each
    /// call hands back a fresh wrapper holding a native reference, and nothing
    /// released them — twice a second for a two-hour film is thousands of
    /// leaked handles, eventually freed by the GC's finalizer thread. Releasing
    /// libVLC objects off the dispatcher thread is exactly what produces the
    /// 0xc0000005 access violations inside libvlc.dll, so the wrapper is now
    /// disposed deterministically, here, on the UI thread.
    /// </summary>
    private string ResolveTrackInfo()
    {
        if (_trackInfoResolved || _player is null) return _trackInfo;
        try
        {
            using var media = _player.Media;
            if (media is null) return "";           // not parsed yet; try again next tick
            var audio = media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Audio);
            if (audio is { } a)
            {
                _trackInfo = $"{FourCc(a.Codec)} {a.Data.Audio.Channels}ch";
                _trackInfoResolved = true;
            }
        }
        catch
        {
            // Tracks can be unavailable mid-transition; a missing label is not
            // worth risking anything for.
            _trackInfoResolved = true;
        }
        return _trackInfo;
    }

    private bool IsPointerOverThisView(System.Drawing.Point screenPoint)
    {
        try
        {
            if (!IsLoaded || ActualWidth <= 0) return false;
            var local = PointFromScreen(new Point(screenPoint.X, screenPoint.Y));
            return local.X >= 0 && local.Y >= 0 && local.X < ActualWidth && local.Y < ActualHeight;
        }
        catch
        {
            // PointFromScreen throws if the visual is not connected to a source.
            return false;
        }
    }

    private void FullscreenBtn_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void VideoArea_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) ToggleFullscreen();
        else if (e.ClickCount == 1) TogglePlay();
    }

    private void ToggleFullscreen()
    {
        var window = MainWindow.Instance;
        if (!_fullscreen)
        {
            _prevWindowState = window.WindowState;
            // Order matters to avoid flicker/taskbar glitches: drop to Normal
            // only if currently maximized (style changes don't apply cleanly
            // to a maximized window), then restyle, then maximize borderless.
            if (window.WindowState == WindowState.Maximized)
                window.WindowState = WindowState.Normal;
            window.EnterFullscreenChrome();
            window.WindowStyle = WindowStyle.None;
            window.ResizeMode = ResizeMode.NoResize;
            window.WindowState = WindowState.Maximized;
            window.Activate();
            TopBar.Visibility = Visibility.Collapsed;

            FullscreenGlyph.Text = "\uE73F";
            _fullscreen = true;
            NoteActivity(); // start the idle countdown from now
        }
        else
        {
            ExitFullscreen();
        }
    }

    private void ExitFullscreen()
    {
        var window = MainWindow.Instance;
        // Leave Maximized first so the restored style lays out correctly.
        window.WindowState = WindowState.Normal;
        window.WindowStyle = WindowStyle.SingleBorderWindow;
        window.ResizeMode = ResizeMode.CanResize;
        window.ExitFullscreenChrome();
        // Re-attaching the chrome re-syncs the native window state, which drags
        // the window back to maximised. Restore the real previous state only
        // once that has settled, or it gets overwritten.
        var restoreTo = _prevWindowState;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
            new Action(() => window.WindowState = restoreTo));
        window.Activate();
        TopBar.Visibility = Visibility.Visible;

        // Leaving fullscreen always restores the controls and the cursor.
        ControlBar.Visibility = Visibility.Visible;
        Cursor = null;
        FullscreenGlyph.Text = "\uE740";
        _fullscreen = false;
    }

    // ---- Keyboard shortcuts ------------------------------------------------

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Never steal keystrokes from text entry (party chat).
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        // Keyboard counts as being present, not just the mouse — otherwise
        // seeking with the arrow keys happens behind hidden controls.
        NoteActivity();
        var handled = true;
        switch (e.Key)
        {
            case Key.Space or Key.K: TogglePlay(); break;
            case Key.Left: SeekBy(-5000); break;
            case Key.Right: SeekBy(5000); break;
            case Key.J: SeekBy(-10_000); break;
            case Key.L: SeekBy(10_000); break;
            case Key.Up: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 10); break;
            case Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 10); break;
            case Key.M: MuteBtn_Click(this, null!); break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.N: NextBtn_Click(this, null!); break;
            case Key.P: PrevBtn_Click(this, null!); break;
            case Key.Escape:
                if (_fullscreen) ExitFullscreen();
                else MainWindow.Instance.Navigate(new LibraryView());
                break;
            default: handled = false; break;
        }
        e.Handled = handled;
    }

    // ---- Cast menu ---------------------------------------------------------

    private ContextMenu? _castMenu;

    private void CastBtn_Click(object sender, RoutedEventArgs e)
    {
        _ = AppServices.Cast.RefreshDlnaAsync(); // rescan; menu refreshes live as devices land

        _castMenu = new ContextMenu
        {
            PlacementTarget = CastBtn,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        FillCastMenu(_castMenu);
        _castMenu.IsOpen = true;
    }

    private void FillCastMenu(ContextMenu menu)
    {
        menu.Items.Clear();
        var local = new MenuItem
        {
            Header = "This computer",
            IsChecked = AppServices.Cast.Active is null,
        };
        local.Click += (_, _) => _ = SelectTargetAsync(null);
        menu.Items.Add(local);

        if (AppServices.Cast.Devices.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "Searching for devices…", IsEnabled = false });
        }
        else
        {
            menu.Items.Add(new Separator());
            foreach (var device in AppServices.Cast.Devices)
            {
                var kind = device is DlnaTarget ? "TV / DLNA" : "Chromecast";
                var item = new MenuItem
                {
                    Header = $"{device.Name}  ({kind})",
                    IsChecked = AppServices.Cast.Active == device,
                };
                var captured = device;
                item.Click += (_, _) => _ = SelectTargetAsync(captured);
                menu.Items.Add(item);
            }
        }
    }

    private async Task SelectTargetAsync(CastTarget? target)
    {
        if (_player is null) return;
        var resumeMs = DlnaActive ? (long)_dlnaPos.TotalMilliseconds : Math.Max(0, _player.Time);
        if (DlnaActive) await StopDlnaAsync();

        switch (target)
        {
            case null:
                AppServices.Cast.Active = null;
                StartPlayback(resumeMs);
                break;

            case VlcTarget vlc:
                AppServices.Cast.Active = vlc;
                StartPlayback(resumeMs);
                break;

            case DlnaTarget dlna:
                _player.Stop();
                try
                {
                    _dlnaServer = new MediaHttpServer();
                    var url = _dlnaServer.Start(_movie.Path);
                    await DlnaService.SetUriAndPlayAsync(dlna.Renderer, url, _movie.Name);
                    _dlnaDevice = dlna.Renderer;
                    _dlnaPaused = false;
                    _dlnaPos = TimeSpan.FromMilliseconds(resumeMs);
                    _dlnaDur = TimeSpan.FromMilliseconds(_movie.DurationMs ?? 0);
                    AppServices.Cast.Active = dlna;
                    if (resumeMs > 2000)
                    {
                        await Task.Delay(1200);
                        await DlnaService.SeekAsync(dlna.Renderer, TimeSpan.FromMilliseconds(resumeMs));
                    }
                }
                catch
                {
                    _dlnaServer?.Dispose();
                    _dlnaServer = null;
                    _dlnaDevice = null;
                    AppServices.Cast.Active = null;
                    StartPlayback(resumeMs);
                    MessageBox.Show(
                        $"Couldn't start casting to {dlna.Name}. Make sure the TV is on and allows playback from this PC.",
                        "Cast failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                break;
        }
        UpdateCastUi();
    }

    private async Task StopDlnaAsync()
    {
        var device = _dlnaDevice;
        _dlnaDevice = null;
        if (device is not null)
        {
            try { await DlnaService.StopAsync(device); } catch { }
        }
        _dlnaServer?.Dispose();
        _dlnaServer = null;
    }

    private void UpdateCastUi()
    {
        var active = AppServices.Cast.Active;
        CastBanner.Visibility = active is null ? Visibility.Collapsed : Visibility.Visible;
        CastBannerText.Text = active is null ? "Casting" : $"Casting to {active.Name}";
        CastLabel.Text = active is null ? "Cast" : active.Name;
        CastGlyph.Foreground = active is null
            ? (Brush)FindResource("TextBrush")
            : (Brush)FindResource("AccentBrush");
    }

    // ---- Per-tick UI refresh ----------------------------------------------

    private void UpdateUi()
    {
        _tick++;
        if (DlnaActive)
        {
            PlayGlyph.Text = _dlnaPaused ? "\uE768" : "\uE769";
            if (_tick % 2 == 0 && !_dlnaPollBusy) _ = PollDlnaAsync();
            if (_dlnaDur > TimeSpan.Zero && _seekDrag?.IsDragging != true)
                Seek.Value = _dlnaPos.TotalMilliseconds / _dlnaDur.TotalMilliseconds * Seek.Maximum;
            TimeText.Text = Fmt((long)_dlnaPos.TotalMilliseconds);
            DurationText.Text = Fmt((long)_dlnaDur.TotalMilliseconds);
            InfoText.Text = "on TV";
            SaveResumeThrottled((long)_dlnaPos.TotalMilliseconds, !_dlnaPaused);
            return;
        }

        if (_player is null) return;
        PlayGlyph.Text = _player.IsPlaying ? "\uE769" : "\uE768";
        // Effective duration: the element's own length, or the movie's known
        // duration when streaming (a relayed party stream may report length 0).
        var durMs = _player.Length > 0 ? _player.Length : _movie.DurationMs ?? 0;
        if (durMs > 0 && _seekDrag?.IsDragging != true)
            Seek.Value = Math.Clamp(_player.Time / (double)durMs, 0, 1) * Seek.Maximum;
        TimeText.Text = Fmt(_player.Time);
        // Guests get time remaining rather than total length: they cannot seek,
        // so "how much is left" is the only part of the duration that helps them.
        DurationText.Text = PartyGuest && durMs > 0
            ? "-" + Fmt(Math.Max(0, durMs - _player.Time))
            : Fmt(durMs);

        InfoText.Text = PartyGuest ? "watch party" : ResolveTrackInfo();

        // Volume is set before the audio output exists, so libVLC sometimes
        // never applies it. Re-assert whenever it has drifted from what the UI
        // says -- this is what stops the player and the mute icon disagreeing.
        if (_player.Volume != DesiredVolume) ApplyAudio();

        // Watched-through once we're 90% in (people rarely sit through credits).
        if (durMs > 0 && _player.Time >= durMs * 0.9) CountWatchOnce();

        if (PartyHost) PushPartyState();
        if (PartyGuest) ApplyPartySync();
        if (_party is null) SaveResumeThrottled(_player.Time, _player.IsPlaying);
    }

    /// <summary>
    /// Guest sync: follow the host's beacons. Small drift is corrected with an
    /// inaudible rate nudge; big drift (or a host seek) hard-seeks; play/pause
    /// state follows the host exactly.
    /// </summary>
    private bool _reportedReady;

    private void ApplyPartySync()
    {
        if (_player is null || _party is null) return;

        // Tell the host we've buffered enough to watch (once the stream parsed).
        if (!_reportedReady && _player.Length > 0)
        {
            _reportedReady = true;
            _ = _party.SendReadyAsync();
        }

        if (!_party.HasBeacon) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var expected = _party.BeaconPositionMs +
                       (_party.BeaconPlaying ? now - _party.BeaconAtUnixMs : 0);

        // Follow play/pause state, and say who did it — otherwise the film just
        // stops and the guest has no idea whether it's the host or their network.
        if (_lastSeenHostPlaying is { } was && was != _party.BeaconPlaying)
            ShowToast(_party.BeaconPlaying
                ? $"{_party.HostName} resumed"
                : $"{_party.HostName} paused", null, null);
        _lastSeenHostPlaying = _party.BeaconPlaying;

        if (_party.BeaconPlaying && !_player.IsPlaying) _player.Play();
        else if (!_party.BeaconPlaying && _player.IsPlaying) _player.Pause();

        if (!_party.BeaconPlaying) return;

        var drift = expected - _player.Time;
        if (Math.Abs(drift) > 2000)
        {
            _player.Time = Math.Max(0, expected);
            _player.SetRate(1f);
        }
        else if (Math.Abs(drift) > 300)
        {
            _player.SetRate(drift > 0 ? 1.05f : 0.95f);
        }
        else
        {
            _player.SetRate(1f);
        }
    }

    private void SaveResumeThrottled(long positionMs, bool playing)
    {
        if (!playing || positionMs < 5000 || _tick % 10 != 0) return;
        SaveResume(positionMs);
    }

    private void SaveResume(long positionMs)
    {
        var s = AppServices.Settings;
        s.ResumePositions[_movie.Path] = positionMs;
        s.LastWatchedAt[_movie.Path] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        s.Save();
    }

    private bool _countedWatch;

    /// <summary>
    /// Count a viewing once the movie is effectively finished (90% in, or the
    /// end is reached). Party guests don't count — it isn't their copy.
    /// </summary>
    private void CountWatchOnce()
    {
        if (_countedWatch || _party is not null) return;
        _countedWatch = true;
        var s = AppServices.Settings;
        s.WatchCounts.TryGetValue(_movie.Path, out var count);
        s.WatchCounts[_movie.Path] = count + 1;
        _movie.WatchCount = count + 1;
        s.Save();
    }

    private async Task PollDlnaAsync()
    {
        var device = _dlnaDevice;
        if (device is null) return;
        _dlnaPollBusy = true;
        try
        {
            var info = await DlnaService.GetPositionAsync(device);
            if (info is { } p && _dlnaDevice is not null)
            {
                _dlnaPos = p.Position;
                if (p.Duration > TimeSpan.Zero) _dlnaDur = p.Duration;
            }
        }
        finally
        {
            _dlnaPollBusy = false;
        }
    }

    private static string Fmt(long ms)
    {
        if (ms < 0) ms = 0;
        var t = TimeSpan.FromMilliseconds(ms);
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    private static string FourCc(uint codec)
    {
        var s = System.Text.Encoding.ASCII.GetString(BitConverter.GetBytes(codec)).TrimEnd('\0', ' ');
        return s.All(c => c >= 32 && c < 127) ? s : codec.ToString("X8");
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        MainWindow.Instance.PreviewKeyDown -= OnKeyDown;
        if (_overlayWindow is not null) _overlayWindow.PreviewKeyDown -= OnKeyDown;
        AppServices.Cast.Devices.CollectionChanged -= OnCastDevicesChanged;
        // Deliberately NOT disposing _party. Navigation disposes this view, so
        // tearing the session down here ended the room and dropped every guest
        // simply because the host went back to the library. The party is owned
        // by AppServices and only AppServices.LeaveParty may end it.
        if (_fullscreen) ExitFullscreen();

        // Persist final resume position.
        var pos = DlnaActive ? (long)_dlnaPos.TotalMilliseconds : _player?.Time ?? 0;
        if (pos > 5000 && _party is null) SaveResume(pos);

        if (DlnaActive)
        {
            if (AppServices.Cast.Active is DlnaTarget) AppServices.Cast.Active = null;
            _ = StopDlnaAsync();
        }
        if (_player is not null)
        {
            var p = _player;
            _player = null;
            Video.MediaPlayer = null;
            p.Stop();
            p.Dispose();
        }
    }
}
