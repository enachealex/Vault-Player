using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoPlayer.App.Helpers;
using VideoPlayer.App.Models;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>One sideways row on the Home page: "Continue watching", a shelf, "All films".</summary>
public sealed record BrowseRow(string Title, IReadOnlyList<MovieItem> Items);

public partial class LibraryView : UserControl, IDisposable
{
    private enum SortMode { NameAsc, NameDesc, LongestFirst, ShortestFirst, MostWatched, RecentlyWatched }

    private static readonly (SortMode Mode, string Label)[] Sorts =
    {
        (SortMode.NameAsc, "Name (A–Z)"),
        (SortMode.NameDesc, "Name (Z–A)"),
        (SortMode.MostWatched, "Most watched"),
        (SortMode.RecentlyWatched, "Recently watched"),
        (SortMode.LongestFirst, "Longest first"),
        (SortMode.ShortestFirst, "Shortest first"),
    };

    private static readonly StringComparer ByName = StringComparer.CurrentCultureIgnoreCase;

    private bool _suppressCollectionChange;
    private readonly CancellationTokenSource _cts = new();
    private List<MovieItem> _movies = new();

    // The sources the library is built from, kept apart so each can be
    // refreshed on its own: the Vault library arrives over the network long
    // after the folder has been scanned, and re-scanning the folder to show it
    // would be both slow and wrong.
    private List<MovieItem> _folderMovies = new();
    private List<MovieItem> _serverMovies = new();

    private SortMode _sort = SortMode.NameAsc;
    private bool _ready;
    private string _tab = "home";

    /// <summary>
    /// When set, picking a film hands it back here instead of playing it. Used
    /// by Watch Party so choosing what to host is the same browsing experience
    /// as watching alone — rows, search and filters — rather than a bare list
    /// of file names.
    /// </summary>
    private readonly Action<MovieItem>? _onPick;

    public LibraryView() : this(null) { }

    /// <summary>
    /// Width of a tile in the grid pages, worked out from the grid's own width
    /// so whole columns fill it and its right edge lines up with the header's.
    /// </summary>
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(LibraryView), new PropertyMetadata(240.0));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        private set => SetValue(CardWidthProperty, value);
    }

    /// <summary>A grid tile's height: 16:9 of whatever width it has.</summary>
    public static readonly DependencyProperty CardFrameHeightProperty = DependencyProperty.Register(
        nameof(CardFrameHeight), typeof(double), typeof(LibraryView), new PropertyMetadata(135.0));

    public double CardFrameHeight
    {
        get => (double)GetValue(CardFrameHeightProperty);
        private set => SetValue(CardFrameHeightProperty, value);
    }

    private const double MinCardWidth = 230, CardGap = 16;

    private void Grid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || sender is not FrameworkElement list) return;
        // A page that is not showing has no width; it would size every tile to nothing.
        var width = list.ActualWidth;
        if (width < MinCardWidth) return;
        // The list is one gap wider than the content (a negative margin), so the
        // last tile's own right margin hangs outside and the tile meets the edge.
        var columns = Math.Max(1, (int)(width / (MinCardWidth + CardGap)));
        // Whole pixels: a fractional width blurs every tile's edge, and rounding
        // up would wrap the last tile onto the next row.
        CardWidth = Math.Floor(width / columns - CardGap);
        CardFrameHeight = Math.Round(CardWidth * 9 / 16);
    }

    public LibraryView(Action<MovieItem>? onPick)
    {
        InitializeComponent();
        _onPick = onPick;
        _ambientDwell.Tick += AmbientDwell_Tick;
        _heroTimer.Tick += (_, _) => ShowFeatured(_featuredIndex + 1);
        SortBox.ItemsSource = Sorts.Select(s => s.Label).ToList();
        SortBox.SelectedIndex = 0;
        _ready = true;
        AppServices.Account.Changed += OnAccountChanged;
        Loaded += (_, _) => HookTiltWheel();

        if (_onPick is not null)
        {
            PickModeBanner.Visibility = Visibility.Visible;
            // Nothing else here applies while choosing: the party banner would
            // be about the very session being set up.
            PartyBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            RefreshPartyBanner();
        }

        var last = AppServices.Settings.LastFolder;
        if (last is not null && Directory.Exists(last)) LoadFolder(last);
        else LoadShortcutsOnly(); // falls through to the empty state if there are none

        // The server library lands whenever the network answers; the folder is
        // already on screen by then.
        _ = LoadServerLibraryAsync();
    }

    private void LoadFolder(string folder)
    {
        try
        {
            var movies = MovieLibrary.Scan(folder);

            // Attach saved progress + watch counts so tiles can show them.
            var resume = AppServices.Settings.ResumePositions;
            var counts = AppServices.Settings.WatchCounts;
            var durations = AppServices.Settings.Durations;
            foreach (var movie in movies)
            {
                if (resume.TryGetValue(movie.StoreKey, out var ms)) movie.ResumeMs = ms;
                if (counts.TryGetValue(movie.StoreKey, out var c)) movie.WatchCount = c;
                // Cached duration shows runtimes instantly; skips a libVLC re-parse.
                if (durations.TryGetValue(movie.FilmKey, out var d) && d > 0) movie.DurationMs = d;
            }
            // Overlay anything synced from another machine (matched by film-key).
            AppServices.Account.ApplyTo(movies);

            // Kept separate from the composed list: the duration and thumbnail
            // pipeline below reads files off disk, and would choke on the URLs
            // behind shortcuts and server films.
            _folderMovies = movies;

            FolderBtnText.Text = "Change folder";
            RefreshCountsLine();

            Compose("No videos in this folder", "Pick a different folder that contains video files.");
            if (_folderMovies.Count > 0) _ = RunMetadataPipelineAsync(movies);

            AppServices.Settings.LastFolder = folder;
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            ShowEmpty("Couldn't read that folder", ex.Message);
        }
    }

    private string _emptyTitle = "", _emptyText = "";

    /// <summary>
    /// Rebuild every page from the folder, the streaming shortcuts and the
    /// Vault library. They share one list on purpose: search, sort and the
    /// rows then work across everything you can watch, rather than making you
    /// remember which of three places a film came from.
    /// </summary>
    private void Compose(string emptyTitle, string emptyText)
    {
        _emptyTitle = emptyTitle;
        _emptyText = emptyText;
        _movies = Deduplicate(_folderMovies
            .Concat(StreamingServices.AsMovieItems(AppServices.Settings.Shortcuts))
            .Concat(PickableServerMovies())
            .ToList());

        RefreshCollectionBox();
        RefreshAll();
    }

    /// <summary>Every page, from the current list. Cheap enough to run on each sync tick.</summary>
    private void RefreshAll()
    {
        RefreshFeatured();
        RefreshHome();
        ApplyFilter();
        RefreshMyList();
        RefreshSearch();
        UpdateEmptyState();
    }

    /// <summary>
    /// One tile per film, even when the same film is in your folder AND on the
    /// Vault.
    ///
    /// The two are genuinely the same film — FilmKey is name-plus-size precisely
    /// so a server copy and a local rip resolve to one identity — and showing
    /// both was always noise. The local copy wins, because it plays off the
    /// disk instead of streaming, but it borrows what the catalogue knows about
    /// the film (its tidy title, year, arrival date and codecs), so owning a
    /// copy never makes a film look worse.
    ///
    /// Shortcuts are exempt: their key degrades to "&lt;url-segment&gt;|0", so they
    /// would all collide with each other and all but one would vanish.
    /// </summary>
    private static List<MovieItem> Deduplicate(List<MovieItem> movies)
    {
        var server = new Dictionary<string, MovieItem>(StringComparer.Ordinal);
        foreach (var m in movies.Where(m => m.IsOnServer)) server.TryAdd(m.FilmKey, m);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MovieItem>(movies.Count);

        // Local first so it claims the key, then everything else.
        foreach (var movie in movies.Where(m => !m.IsOnServer).Concat(movies.Where(m => m.IsOnServer)))
        {
            if (!movie.IsShortcut && movie.SizeBytes > 0 && !seen.Add(movie.FilmKey)) continue;
            if (movie.Kind == MovieKind.Local && server.TryGetValue(movie.FilmKey, out var twin))
            {
                movie.Title ??= twin.Title;
                movie.Year ??= twin.Year;
                if (movie.AddedAt == 0) movie.AddedAt = twin.AddedAt;
                movie.VideoCodec ??= twin.VideoCodec;
                movie.AudioCodec ??= twin.AudioCodec;
            }
            result.Add(movie);
        }
        return result.OrderBy(m => m.DisplayTitle, ByName).ToList();
    }

    /// <summary>
    /// Server films are hidden while choosing what to host. A host answers byte
    /// ranges by reading the film off its own disk, and a server film is a URL,
    /// not a file — so offering one here would produce a party that cannot
    /// stream. Hosting straight from the server is the next piece of work.
    /// </summary>
    private IEnumerable<MovieItem> PickableServerMovies() =>
        _onPick is null ? _serverMovies : Enumerable.Empty<MovieItem>();

    /// <summary>The "12 movies · 3 streaming · 40 on Vault" line on the Films page.</summary>
    private string CountsLine()
    {
        var parts = new List<string>();
        // "0 movies" is only worth saying when a folder was actually chosen and
        // turned out to be empty; otherwise it just reads as a complaint.
        if (_folderMovies.Count > 0 || AppServices.Settings.LastFolder is not null)
            parts.Add($"{_folderMovies.Count} on this PC");

        var shortcuts = AppServices.Settings.Shortcuts.Count;
        if (shortcuts > 0) parts.Add($"{shortcuts} streaming");

        var server = PickableServerMovies().Count();
        if (server > 0) parts.Add($"{server} on Vault");
        else if (AppServices.Catalog.ServerScanning) parts.Add("Vault library still indexing");

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Pull the server's library in, then fetch its stills. Failure is quiet:
    /// signed out, offline, or talking to a relay too old to have a catalogue
    /// all mean the same thing here — there are no server films to show, and
    /// the folder library is unaffected.
    /// </summary>
    private async Task LoadServerLibraryAsync()
    {
        var items = await AppServices.Catalog.RefreshAsync(_cts.Token);
        if (_cts.IsCancellationRequested) return;

        var hadFilms = _serverMovies.Count > 0;
        _serverMovies = items.ToList();

        // Same overlays the folder films get, so a film streamed from the server
        // shows its progress and watch count too.
        var resume = AppServices.Settings.ResumePositions;
        var counts = AppServices.Settings.WatchCounts;
        foreach (var movie in _serverMovies)
        {
            if (resume.TryGetValue(movie.StoreKey, out var ms)) movie.ResumeMs = ms;
            if (counts.TryGetValue(movie.StoreKey, out var c)) movie.WatchCount = c;
        }
        AppServices.Account.ApplyTo(_serverMovies);

        // Shown whenever there is a server to ask, not only when it already
        // answered with films. A library that has just been set up has none
        // yet, and the person who has this second copied their first film onto
        // it — hiding the one control that would make it appear left them with
        // nothing to do but restart the server.
        VaultBtn.Visibility = (_serverMovies.Count > 0 || AppServices.Account.IsSignedIn) && _onPick is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Nothing to say and nothing to take away — leave the pages alone. The
        // `hadFilms` half matters on sign-out: the films are gone from `items`
        // and the pages still have them on screen.
        if (_serverMovies.Count == 0 && !hadFilms)
        {
            // The list may still have changed (signing in to an empty server).
            RefreshMyList();
            return;
        }

        RefreshCountsLine();
        Compose("Nothing to watch yet",
            "Choose a folder with your films in it, or add some to the Vault server.");

        // The featured films first: they are the biggest pictures on screen.
        var first = _featured.Where(m => m.IsOnServer).ToList();
        await AppServices.Catalog.LoadArtAsync(first.Concat(_serverMovies.Except(first)), _cts.Token);
    }

    /// <summary>
    /// Ask the server to re-index its disk, then show the result. Adding a film
    /// to the media disk is otherwise invisible until the server restarts, and
    /// the person who just copied one over is exactly who wants it now.
    /// </summary>
    private async void VaultBtn_Click(object sender, RoutedEventArgs e)
    {
        VaultBtn.IsEnabled = false;
        VaultBtnText.Text = "Refreshing…";
        try
        {
            await AppServices.Catalog.RequestRescanAsync();

            // A sweep of a big library takes a while, and the server does it in
            // the background. Give it a moment, then show whatever is ready —
            // the count line says so when indexing is still going.
            await Task.Delay(2500, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            await LoadServerLibraryAsync();
        }
        catch (OperationCanceledException)
        {
            // Navigated away mid-refresh.
        }
        finally
        {
            if (!_cts.IsCancellationRequested)
            {
                VaultBtnText.Text = "Vault";
                VaultBtn.IsEnabled = true;
            }
        }
    }

    /// <summary>The counts and the folder, under the Films page's title.</summary>
    private void RefreshCountsLine()
    {
        var folder = AppServices.Settings.LastFolder;
        var counts = CountsLine();
        var where = folder is not null && Directory.Exists(folder) ? folder : "no folder selected";
        FolderSubtitle.Text = counts.Length > 0 ? $"{counts}  ·  {where}" : where;
    }

    private async Task RunMetadataPipelineAsync(List<MovieItem> movies)
    {
        // Only parse what isn't already cached — a returning library is instant.
        var need = movies.Where(m => m.DurationMs is null or 0).ToList();
        if (need.Count > 0)
        {
            await MovieLibrary.ProbeDurationsAsync(need, _cts.Token);
            if (_cts.IsCancellationRequested) return;
            PersistDurations(need);
            RefreshHome(); // durations make "x min left" and progress meaningful
            if (_sort is SortMode.LongestFirst or SortMode.ShortestFirst) ApplyFilter();
        }
        // Stills still run every time; ThumbnailService already caches to disk.
        await ThumbnailService.EnsureThumbsAsync(movies, _cts.Token);
    }

    /// <summary>Remember freshly probed durations so the next visit skips the parse.</summary>
    private static void PersistDurations(IEnumerable<MovieItem> movies)
    {
        var cache = AppServices.Settings.Durations;
        var added = false;
        foreach (var m in movies)
            if (m.DurationMs is > 0 && m.SizeBytes > 0)
            {
                cache[m.FilmKey] = m.DurationMs.Value;
                added = true;
            }
        if (added) AppServices.Settings.Save();
    }

    // ---- What goes on the Home page ------------------------------------------

    /// <summary>When a film was last watched here or on any other machine, for ordering rows.</summary>
    private static long LastWatched(MovieItem m)
    {
        var s = AppServices.Settings;
        var here = s.LastWatchedAt.TryGetValue(m.StoreKey, out var t) ? t : 0;
        var synced = !m.IsShortcut && s.SyncedLibrary.TryGetValue(m.FilmKey, out var row) ? row.LastWatchedAt : 0;
        return Math.Max(here, synced);
    }

    /// <summary>Started and not finished: past the first few seconds, short of the credits.</summary>
    private static bool InProgress(MovieItem m) =>
        !m.IsShortcut && m.ResumeMs > 5000
        && (m.DurationMs is not > 0 || m.ResumeMs < m.DurationMs.Value - 15000);

    private static bool NeverStarted(MovieItem m) => m.ResumeMs <= 0 && m.WatchCount == 0;

    private IEnumerable<MovieItem> Films => _movies.Where(m => !m.IsShortcut);

    /// <summary>A film by its identity, from whichever copy is on screen.</summary>
    private MovieItem? ByKey(string filmKey) =>
        _movies.FirstOrDefault(m => !m.IsShortcut && string.Equals(m.FilmKey, filmKey, StringComparison.Ordinal));

    private List<MovieItem> MyListFilms() =>
        AppServices.Catalog.MyList.Select(ByKey).OfType<MovieItem>().ToList();

    /// <summary>
    /// The rows, in the order every Vault app uses: Continue watching, Recently
    /// added, My List, the account's shelves, Watch again, All films. A row
    /// with nothing in it is left out rather than shown empty.
    /// </summary>
    private void RefreshHome()
    {
        var rows = new List<BrowseRow>();
        void Add(string title, IEnumerable<MovieItem> films)
        {
            var list = films.ToList();
            if (list.Count > 0) rows.Add(new BrowseRow(title, list));
        }

        var inProgress = Films.Where(InProgress)
            .OrderByDescending(LastWatched).ThenByDescending(m => m.ResumeMs)
            .Take(20).ToList();
        Add("Continue watching", inProgress);
        // Only the server knows when a film arrived; local files are never "recent".
        Add("Recently added", Films.Where(m => m.AddedAt > 0).OrderByDescending(m => m.AddedAt).Take(20));
        Add("My List", MyListFilms());
        foreach (var shelf in AppServices.Catalog.Collections.OrderBy(c => c.SortOrder))
            Add(shelf.Name, shelf.Items.Select(i => ByKey(i.FilmKey)).OfType<MovieItem>().Distinct());
        Add("Watch again", Films.Where(m => m.WatchCount > 0 && !InProgress(m)).OrderByDescending(LastWatched).Take(20));
        Add("All films", _movies.OrderBy(m => m.DisplayTitle, ByName));

        RowsList.ItemsSource = rows;
        if (inProgress.Count > 0) OfferAmbient(inProgress[0]);
    }

    // ---- The featured film -----------------------------------------------------

    private readonly DispatcherTimer _heroTimer = new() { Interval = TimeSpan.FromSeconds(9) };
    private static readonly Duration HeroFade = new(TimeSpan.FromMilliseconds(900));
    private List<MovieItem> _featured = new();
    private string _featuredPool = "";
    private int _featuredIndex = -1;
    private bool _heroAOnTop;
    private MovieItem? _heroFilm;

    /// <summary>
    /// Choose the films the featured slot cycles through: new arrivals first,
    /// then films never started, shuffled per visit so the top of the screen is
    /// not the same film every time. Films with no still to show are skipped,
    /// since the slot is mostly picture — a server film without art, or a
    /// streaming shortcut. Local films qualify: their frame grab is the still.
    ///
    /// The choice is kept while it still holds, because this runs on every
    /// background sync and re-shuffling then would yank the picture away.
    /// </summary>
    private void RefreshFeatured()
    {
        var pool = Films.Where(m => m.IsOnServer ? m.ArtUrl is not null : m.Kind == MovieKind.Local).ToList();
        var byPath = pool.ToDictionary(m => m.Path, StringComparer.Ordinal);

        // Same pool as last time: swap in the current instances and carry on.
        // A pool that changed (the Vault library arriving after the folder)
        // gets a fresh pick, or the folder's films would hold the slot.
        var signature = string.Join("|", byPath.Keys.Order(StringComparer.Ordinal));
        if (_featured.Count > 0 && signature == _featuredPool && _featured.All(m => byPath.ContainsKey(m.Path)))
        {
            _featured = _featured.Select(m => byPath[m.Path]).ToList();
        }
        else
        {
            var month = 30L * 24 * 3600 * 1000;
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var fresh = Shuffle(pool.Where(m => m.AddedAt > 0 && now - m.AddedAt < month && NeverStarted(m)));
            var unwatched = Shuffle(pool.Where(m => NeverStarted(m) && !fresh.Contains(m)));
            var picks = fresh.Concat(unwatched).ToList();
            if (picks.Count < 5) picks.AddRange(Shuffle(pool.Where(m => !picks.Contains(m))));
            _featured = picks.Take(5).ToList();
            _featuredIndex = -1;
            _featuredPool = signature;
        }

        HeroDots.Children.Clear();
        for (var i = 0; i < _featured.Count; i++)
        {
            var index = i;
            var dot = new Border
            {
                Width = 18, Height = 4, Margin = new Thickness(0, 0, 6, 0),
                CornerRadius = (CornerRadius)FindResource("IndicatorRadius"),
                Background = (Brush)FindResource("IndicatorOffBrush"),
                Cursor = Cursors.Hand,
                // A generous hit area round a 4px mark.
                BorderBrush = Brushes.Transparent,
            };
            dot.MouseLeftButtonDown += (_, _) => ShowFeatured(index);
            HeroDots.Children.Add(dot);
        }
        HeroDots.Visibility = _featured.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        Hero.Visibility = _featured.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // Without a featured film the rows start at the top, below the tab bar.
        RowsList.Margin = _featured.Count > 0 ? new Thickness(0, -128, 0, 24) : new Thickness(0, 84, 0, 24);

        if (_featured.Count == 0)
        {
            _heroTimer.Stop();
            _heroFilm = null;
            return;
        }
        ShowFeatured(_featuredIndex < 0 ? 0 : _featuredIndex, force: true);
        _heroTimer.Stop();
        if (_featured.Count > 1) _heroTimer.Start();
    }

    private static List<MovieItem> Shuffle(IEnumerable<MovieItem> films)
    {
        var list = films.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>Put one featured film up, cross-fading from the last.</summary>
    private void ShowFeatured(int index, bool force = false)
    {
        if (_featured.Count == 0) return;
        index = ((index % _featured.Count) + _featured.Count) % _featured.Count;
        var film = _featured[index];
        if (!force && ReferenceEquals(film, _heroFilm)) return;

        if (_heroFilm is not null) _heroFilm.PropertyChanged -= HeroFilm_PropertyChanged;
        _featuredIndex = index;
        _heroFilm = film;
        film.PropertyChanged += HeroFilm_PropertyChanged;

        // A click on a mark restarts the clock, so the chosen film gets its full turn.
        if (_heroTimer.IsEnabled) { _heroTimer.Stop(); _heroTimer.Start(); }

        HeroKicker.Text = InProgress(film) ? "CONTINUE WATCHING"
            : film.AddedAt > 0 && NeverStarted(film)
              && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - film.AddedAt < 30L * 24 * 3600 * 1000
                ? "NEW IN YOUR VAULT"
            : film.IsOnServer ? "FROM YOUR VAULT" : "ON THIS PC";
        HeroTitleText.Text = film.DisplayTitle;
        FillMeta(HeroMeta, film, 15);
        HeroPlayText.Text = _onPick is not null ? "Host this film" : InProgress(film) ? "Resume" : "Play";

        for (var i = 0; i < HeroDots.Children.Count; i++)
            ((Border)HeroDots.Children[i]).Background =
                (Brush)FindResource(i == index ? "IndicatorOnBrush" : "IndicatorOffBrush");

        // Faded while the page is up; set outright before it is, so the first
        // picture (and a snapshot) does not start from black.
        SetHeroPicture(film.Thumbnail, animate: IsLoaded);
    }

    private void HeroFilm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MovieItem.Thumbnail) || !ReferenceEquals(sender, _heroFilm)) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (ReferenceEquals(sender, _heroFilm)) SetHeroPicture(_heroFilm!.Thumbnail, animate: true);
        });
    }

    /// <summary>
    /// New still on top, fading in over the old one, which then fades out: the
    /// picture is never left half-dark between two films.
    /// </summary>
    private void SetHeroPicture(ImageSource? still, bool animate)
    {
        var top = _heroAOnTop ? HeroArtA : HeroArtB;
        if (ReferenceEquals(top.Source, still) && top.Opacity > 0) return;

        var incoming = _heroAOnTop ? HeroArtB : HeroArtA;
        var outgoing = top;
        _heroAOnTop = !_heroAOnTop;
        incoming.Source = still;
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        Fade(incoming, still is null ? 0 : 1, animate);
        Fade(outgoing, 0, animate);
    }

    private static void Fade(UIElement layer, double to, bool animate)
    {
        if (!animate)
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = to;
            return;
        }
        layer.BeginAnimation(OpacityProperty, new DoubleAnimation(to, HeroFade) { EasingFunction = new QuadraticEase() });
    }

    private void HeroPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_heroFilm is { } film) Play(film, fromStart: false);
    }

    private void HeroInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_heroFilm is { } film) OpenDetails(film);
    }

    /// <summary>
    /// Year • runtime, then the outlined tags: CC when the film has captions,
    /// 4K or HD. A local file contributes what it has, which may be only the
    /// runtime and a tag.
    /// </summary>
    private void FillMeta(Panel panel, MovieItem film, double size)
    {
        panel.Children.Clear();
        var text = (Brush)FindResource("TextBrush");
        var words = new[] { film.Year?.ToString(CultureInfo.InvariantCulture), film.RuntimeText }
            .Where(w => !string.IsNullOrEmpty(w)).ToList();
        if (film.IsShortcut && film.Service is { } service) words.Add(service);
        if (words.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("  •  ", words), FontSize = size, Foreground = text,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            });

        void AddTag(string label) => panel.Children.Add(new Border
        {
            Style = (Style)FindResource("MetaTag"),
            Child = new TextBlock { Text = label, Style = (Style)FindResource("MetaTagText") },
        });
        if (film.HasCaptions) AddTag("CC");
        if (film.QualityTag is { } quality) AddTag(quality);
    }

    // ---- Pages -------------------------------------------------------------

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!_ready || (sender as RadioButton)?.Tag is not string tab) return;
        ShowPage(tab);
    }

    /// <summary>Show one of Home, Films, My List or Search. Public for the UI snapshots.</summary>
    public void ShowPage(string tab)
    {
        _tab = tab;
        var radio = tab switch { "films" => FilmsTab, "mylist" => MyListTab, "search" => SearchTab, _ => HomeTab };
        if (radio.IsChecked != true) radio.IsChecked = true;

        HomePage.Visibility = tab == "home" ? Visibility.Visible : Visibility.Collapsed;
        FilmsPage.Visibility = tab == "films" ? Visibility.Visible : Visibility.Collapsed;
        MyListPage.Visibility = tab == "mylist" ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = tab == "search" ? Visibility.Visible : Visibility.Collapsed;
        UpdateEmptyState();
        UpdateTopShade(CurrentPage);

        if (tab == "search") Dispatcher.BeginInvoke(DispatcherPriority.Input, () => SearchBox.Focus());
    }

    private ScrollViewer CurrentPage => _tab switch
    {
        "films" => FilmsPage, "mylist" => MyListPage, "search" => SearchPage, _ => HomePage,
    };

    private void Page_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (ReferenceEquals(sender, CurrentPage)) UpdateTopShade(CurrentPage);
    }

    /// <summary>The shade behind the tab bar fades in over the first few pixels of scrolling.</summary>
    private void UpdateTopShade(ScrollViewer page) =>
        TopShade.Opacity = Math.Clamp(page.VerticalOffset / 60.0, 0, 1);

    private void Pages_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Most of the window, leaving the first row in view under it — the
        // featured film invites you in, the row shows there is more.
        Hero.Height = Math.Max(400, Math.Round(Pages.ActualHeight * 0.7));
    }

    private void UpdateEmptyState()
    {
        var nothing = _movies.Count == 0;
        EmptyState.Visibility = nothing && _tab == "home" ? Visibility.Visible : Visibility.Collapsed;
        if (nothing)
        {
            EmptyTitle.Text = _emptyTitle;
            EmptyText.Text = _emptyText;
        }
    }

    private void ShowEmpty(string title, string text)
    {
        _emptyTitle = title;
        _emptyText = text;
        UpdateEmptyState();
        if (_movies.Count > 0)
        {
            // A failed folder with films still listed from elsewhere: say so.
            MessageBox.Show(Window.GetWindow(this), text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Films page: sort + shelf ----------------------------------------------

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SortBox.SelectedIndex < 0) return;
        _sort = Sorts[SortBox.SelectedIndex].Mode;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        IEnumerable<MovieItem> view = _movies;

        // A shelf narrows the grid to its films. Matched on FilmKey rather than
        // on anything stored on the item: Compose rebuilds the list on each
        // refresh, so a flag written onto an instance would not survive.
        if (SelectedCollection() is { } shelf)
        {
            var keys = shelf.Items.Select(i => i.FilmKey).ToHashSet(StringComparer.Ordinal);
            view = view.Where(m => keys.Contains(m.FilmKey));
        }

        var watchedAt = AppServices.Settings.LastWatchedAt;

        // "Most watched" and "Recently watched" are real filters, not just orderings:
        // they show only films actually watched, rather than padding the grid with
        // everything sitting at zero.
        if (_sort == SortMode.MostWatched) view = view.Where(m => m.WatchCount > 0);
        // A film counts as "watched" if it has a timestamp, a saved position, or a
        // completed view — older libraries predate the timestamp, so fall back.
        else if (_sort == SortMode.RecentlyWatched)
            view = view.Where(m => LastWatched(m) > 0 || m.ResumeMs > 0 || m.WatchCount > 0);

        view = _sort switch
        {
            SortMode.NameDesc => view.OrderByDescending(m => m.DisplayTitle, ByName),
            SortMode.LongestFirst => view.OrderByDescending(m => m.DurationMs ?? 0),
            SortMode.ShortestFirst => view.OrderBy(m => m.DurationMs ?? long.MaxValue),
            SortMode.MostWatched => view.OrderByDescending(m => m.WatchCount).ThenBy(m => m.DisplayTitle, ByName),
            SortMode.RecentlyWatched => view.OrderByDescending(LastWatched).ThenBy(m => m.DisplayTitle, ByName),
            _ => view.OrderBy(m => m.DisplayTitle, ByName),
        };

        var result = view.ToList();
        MovieList.ItemsSource = result;
        FilterCount.Text = result.Count == _movies.Count ? $"{result.Count}" : $"{result.Count} of {_movies.Count}";

        FilmsEmpty.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilmsEmpty.Text = _movies.Count == 0
            ? "No films yet. Choose a folder with your films in it, or sign in to your Vault."
            : _sort is SortMode.MostWatched or SortMode.RecentlyWatched && SelectedCollection() is null
                ? "Nothing watched yet. Finish a film and it'll show up here — it counts once you reach the last 10%."
                : "Nothing here yet.";
    }

    // ---- My List page ----------------------------------------------------------

    private void RefreshMyList()
    {
        var films = MyListFilms();
        MyListList.ItemsSource = films;
        MyListEmpty.Visibility = films.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MyListEmpty.Text = AppServices.Account.IsSignedIn
            ? "Nothing saved yet. Open any film and choose My List to keep it here."
            : "Sign in to your Vault to keep a list of films to watch next, the same on every device.";
    }

    /// <summary>Whether a film can go on My List: a film the signed-in server has.</summary>
    private static bool CanSaveToList(MovieItem movie) =>
        movie is { IsShortcut: false, SizeBytes: > 0 }
        && AppServices.Account.IsSignedIn
        && AppServices.Catalog.Items.Any(x => x.FilmKey == movie.FilmKey);

    // ---- Search page -------------------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchBtn.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshSearch();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    /// <summary>Type into the search page. Public for the UI snapshots.</summary>
    public void SetSearch(string text) => SearchBox.Text = text;

    /// <summary>
    /// An empty box lists everything A to Z, so the page is a way to browse as
    /// well as to find. A query matches the title shown or the file name, so a
    /// half-remembered release name still finds the film.
    /// </summary>
    private void RefreshSearch()
    {
        var query = SearchBox.Text.Trim();
        var all = _movies.OrderBy(m => m.DisplayTitle, ByName);
        var result = query.Length == 0
            ? all.ToList()
            : all.Where(m => m.DisplayTitle.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                          || m.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                          || (!m.IsShortcut && Path.GetFileName(m.Path).Contains(query, StringComparison.CurrentCultureIgnoreCase)))
                 .ToList();

        SearchList.ItemsSource = result;
        SearchHeader.Text = query.Length == 0
            ? "All films, A to Z"
            : $"{result.Count} {(result.Count == 1 ? "film matches" : "films match")} “{query}”";
        SearchHeader.Visibility = query.Length == 0 && result.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SearchEmpty.Visibility = result.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchEmpty.Text = query.Length == 0
            ? "No films yet."
            : "Nothing matches that. Try a different word, or part of the file name.";
    }

    // ---- Rows that scroll sideways -----------------------------------------------

    private void Row_MouseEnter(object sender, MouseEventArgs e) => UpdateArrows(sender as Grid);
    private void Row_MouseLeave(object sender, MouseEventArgs e) => UpdateArrows(sender as Grid);

    private void Row_ScrollChanged(object sender, ScrollChangedEventArgs e) =>
        UpdateArrows((sender as FrameworkElement)?.Parent as Grid);

    /// <summary>An arrow shows only while the pointer is on the row, and only towards more films.</summary>
    private static void UpdateArrows(Grid? row)
    {
        if (row is null) return;
        var scroll = row.Children.OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null) return;
        var hover = row.IsMouseOver;
        foreach (var arrow in row.Children.OfType<Button>())
        {
            var show = arrow.Name == "PrevArrow"
                ? scroll.HorizontalOffset > 1
                : scroll.HorizontalOffset < scroll.ScrollableWidth - 1;
            arrow.Visibility = hover && show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>A page of tiles at a time, gliding rather than jumping.</summary>
    private void RowArrow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ScrollViewer scroll } arrow) return;
        var step = Math.Max(240, scroll.ViewportWidth * 0.85) * (arrow.Name == "PrevArrow" ? -1 : 1);
        ScrollRowTo(scroll, scroll.HorizontalOffset + step);
    }

    private static void ScrollRowTo(ScrollViewer scroll, double target)
    {
        target = Math.Clamp(target, 0, scroll.ScrollableWidth);
        scroll.BeginAnimation(ScrollAnimator.HorizontalOffsetProperty, new DoubleAnimation(
            scroll.HorizontalOffset, target, new Duration(TimeSpan.FromMilliseconds(380)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    /// <summary>
    /// Shift+wheel moves a row sideways. A plain wheel scrolls the page, as
    /// anywhere else — but a ScrollViewer swallows every wheel event over it
    /// even when it cannot scroll that way, so the row hands it up by hand.
    /// </summary>
    private void Row_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroll) return;
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            scroll.BeginAnimation(ScrollAnimator.HorizontalOffsetProperty, null);
            scroll.ScrollToHorizontalOffset(scroll.HorizontalOffset - e.Delta);
            return;
        }
        (scroll.Parent as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = MouseWheelEvent,
            Source = scroll,
        });
    }

    // A touchpad's sideways swipe and a tilting wheel arrive as WM_MOUSEHWHEEL,
    // which WPF has no event for. Listen for it on the window and move the row
    // under the pointer.
    private const int WmMouseHWheel = 0x020E;
    private HwndSource? _hwnd;

    private void HookTiltWheel()
    {
        if (_hwnd is not null) return;
        _hwnd = PresentationSource.FromVisual(this) as HwndSource;
        _hwnd?.AddHook(TiltWheelHook);
    }

    private IntPtr TiltWheelHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmMouseHWheel || !IsLoaded) return IntPtr.Zero;
        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        for (var node = Mouse.DirectlyOver as DependencyObject; node is not null;
             node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node))
        {
            if (node is ScrollViewer { Name: "RowScroll" } row)
            {
                row.BeginAnimation(ScrollAnimator.HorizontalOffsetProperty, null);
                row.ScrollToHorizontalOffset(row.HorizontalOffset + delta);
                handled = true;
                break;
            }
            if (ReferenceEquals(node, this)) break;
        }
        return IntPtr.Zero;
    }

    // ---- Opening, playing ------------------------------------------------------

    private void Tile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieItem movie) return;

        // Choosing something to host: hand it back rather than playing it.
        if (_onPick is not null)
        {
            _onPick(movie);
            return;
        }

        // Streaming titles open in the in-app browser — signed in, ready to
        // find. Playing a protected one hands off from there.
        if (movie.IsShortcut)
        {
            OpenStreamingBrowser(movie.Path, movie.Service);
            return;
        }

        OpenDetails(movie);
    }

    /// <summary>
    /// Play a film here (or hand it to the party being set up). Resuming uses
    /// the furthest point this account knows, including one synced from
    /// another machine; starting over forgets it first.
    /// </summary>
    private void Play(MovieItem movie, bool fromStart)
    {
        if (_onPick is not null)
        {
            _onPick(movie);
            return;
        }
        if (movie.IsShortcut)
        {
            OpenStreamingBrowser(movie.Path, movie.Service);
            return;
        }

        var resume = AppServices.Settings.ResumePositions;
        if (fromStart)
        {
            resume.Remove(movie.StoreKey);
            movie.ResumeMs = 0;
        }
        else if (InProgress(movie) && !resume.ContainsKey(movie.StoreKey))
        {
            // The player reads this store; a position that only exists in the
            // synced copy would otherwise start the film from the top.
            resume[movie.StoreKey] = movie.ResumeMs;
        }

        // Only local files and server films belong in the playlist —
        // next/previous must never land on a shortcut the player can't open.
        MainWindow.Instance.Navigate(new PlayerView(movie, _movies.Where(m => !m.IsShortcut).ToList()));
    }

    // ---- Details ---------------------------------------------------------------

    private MovieItem? _details;
    private IInputElement? _focusBeforeDetails;

    /// <summary>The details sheet for one film. Public for the UI snapshots.</summary>
    public void OpenDetails(MovieItem movie)
    {
        if (_details is null) _focusBeforeDetails = Keyboard.FocusedElement;
        _details = movie;
        _heroTimer.Stop();

        var still = movie.Thumbnail;
        DetailsBlur.Source = still;
        SheetStill.Background = still is null ? null : new ImageBrush(still) { Stretch = Stretch.UniformToFill };
        // No still: a shorter head, so the title is not floating in empty glass.
        SheetStill.Height = still is null ? 150 : 330;

        DetailsTitle.Text = movie.DisplayTitle;
        FillMeta(DetailsMeta, movie, 14);

        var going = InProgress(movie);
        DetailsPlayText.Text = _onPick is not null ? "Host this film" : going ? "Resume" : "Play";
        DetailsPlayBtn.ToolTip = going && _onPick is null ? $"Carry on from {Clock(movie.ResumeMs)}" : null;
        AutomationProperties.SetName(DetailsPlayBtn, DetailsPlayText.Text);
        StartOverBtn.Visibility = going && _onPick is null ? Visibility.Visible : Visibility.Collapsed;
        DetailsProgress.Visibility = going ? Visibility.Visible : Visibility.Collapsed;
        DetailsProgressBar.Value = movie.ProgressFraction;
        DetailsLeftText.Text = movie.ResumeText;
        DetailsCaptions.Visibility = movie.HasCaptions ? Visibility.Visible : Visibility.Collapsed;
        UpdateMyListButton();

        FillFacts(movie);

        var more = MoreLike(movie);
        MoreList.ItemsSource = more;
        MoreHeader.Visibility = more.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        Details.Visibility = Visibility.Visible;
        SheetScroll.ScrollToTop();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => DetailsPlayBtn.Focus());
    }

    private void CloseDetails()
    {
        if (_details is null) return;
        _details = null;
        Details.Visibility = Visibility.Collapsed;
        DetailsBlur.Source = null;
        SheetStill.Background = null;
        if (_featured.Count > 1) _heroTimer.Start();
        if (_focusBeforeDetails is UIElement { IsVisible: true } back) back.Focus();
        _focusBeforeDetails = null;
    }

    private void DetailsClose_Click(object sender, RoutedEventArgs e) => CloseDetails();

    private void DetailsBackdrop_MouseDown(object sender, MouseButtonEventArgs e) => CloseDetails();

    private void Library_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _details is not null)
        {
            CloseDetails();
            e.Handled = true;
        }
    }

    private void DetailsPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_details is { } film) Play(film, fromStart: false);
    }

    private void StartOver_Click(object sender, RoutedEventArgs e)
    {
        if (_details is { } film) Play(film, fromStart: true);
    }

    private void UpdateMyListButton()
    {
        if (_details is not { } film) return;
        var can = CanSaveToList(film);
        MyListBtn.Visibility = can ? Visibility.Visible : Visibility.Collapsed;
        var on = AppServices.Catalog.IsOnMyList(film.FilmKey);
        // Add and Accept: a plus to save it, a tick once it is saved.
        MyListGlyph.Text = on ? "" : "";
        MyListText.Text = on ? "On My List" : "My List";
        MyListBtn.ToolTip = on ? "Take this film off My List" : "Save this film to My List, on every device";
        AutomationProperties.SetName(MyListBtn, on ? "Remove from My List" : "Add to My List");
    }

    private async void MyListToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_details is not { } film) return;
        var catalog = AppServices.Catalog;
        var on = !catalog.IsOnMyList(film.FilmKey);
        MyListBtn.IsEnabled = false;
        try
        {
            if (!await catalog.SetOnMyListAsync(film.FilmKey, on, _cts.Token))
            {
                if (_cts.IsCancellationRequested) return;
                MessageBox.Show(Window.GetWindow(this), catalog.LastError ?? "Couldn't change My List.",
                    "My List", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (!_cts.IsCancellationRequested)
            {
                MyListBtn.IsEnabled = true;
                UpdateMyListButton();
                RefreshHome();
                RefreshMyList();
            }
        }
    }

    /// <summary>Label-and-value facts under the buttons, leaving out anything unknown.</summary>
    private void FillFacts(MovieItem movie)
    {
        DetailsFacts.Children.Clear();
        DetailsFacts.RowDefinitions.Clear();

        var shelves = AppServices.Catalog.Collections
            .Where(c => c.Items.Any(i => string.Equals(i.FilmKey, movie.FilmKey, StringComparison.Ordinal)))
            .Select(c => c.Name).ToList();
        var format = string.Join(" · ", new[] { movie.VideoCodec, movie.AudioCodec }
            .Where(c => !string.IsNullOrEmpty(c)).Select(c => c!.ToUpperInvariant()));
        if (format.Length == 0 && !movie.IsShortcut && !movie.IsOnServer)
            format = Path.GetExtension(movie.Path).TrimStart('.').ToUpperInvariant();

        var facts = new List<(string Label, string Value)>
        {
            ("Watched", movie.WatchCount switch { <= 0 => "Not yet", 1 => "Once", 2 => "Twice", var n => $"{n} times" }),
            ("Shelves", string.Join(", ", shelves)),
            ("Added", movie.AddedAt > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(movie.AddedAt).LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                : ""),
            ("Format", format),
            ("File size", movie.SizeBytes <= 0 ? ""
                : movie.SizeBytes >= 1_000_000_000 ? $"{movie.SizeBytes / 1e9:0.0} GB" : $"{movie.SizeBytes / 1e6:0} MB"),
            ("Plays from", movie.IsShortcut ? movie.Service ?? "A streaming service"
                : movie.IsOnServer ? "Your Vault server" : "This PC — " + Path.GetDirectoryName(movie.Path)),
        };

        var muted = (Brush)FindResource("TextMutedBrush");
        var text = (Brush)FindResource("TextBrush");
        var row = 0;
        foreach (var (label, value) in facts.Where(f => f.Value.Length > 0))
        {
            DetailsFacts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, FontSize = 13, Foreground = muted, Margin = new Thickness(0, 0, 24, 8) };
            var v = new TextBlock
            {
                Text = value, FontSize = 13, Foreground = text, Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            DetailsFacts.Children.Add(l);
            DetailsFacts.Children.Add(v);
            row++;
        }
    }

    /// <summary>Films on the same shelves first, then the newest arrivals, then the rest A to Z.</summary>
    private List<MovieItem> MoreLike(MovieItem movie)
    {
        var shelfKeys = AppServices.Catalog.Collections
            .Where(c => c.Items.Any(i => string.Equals(i.FilmKey, movie.FilmKey, StringComparison.Ordinal)))
            .SelectMany(c => c.Items.Select(i => i.FilmKey))
            .ToHashSet(StringComparer.Ordinal);
        return Films
            .Where(m => !ReferenceEquals(m, movie) && m.FilmKey != movie.FilmKey)
            .OrderByDescending(m => shelfKeys.Contains(m.FilmKey))
            .ThenByDescending(m => m.AddedAt)
            .ThenBy(m => m.DisplayTitle, ByName)
            .Take(12)
            .ToList();
    }

    private static string Clock(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ---- Active party banner -----------------------------------------------

    private void RefreshPartyBanner()
    {
        var party = AppServices.CurrentParty;
        if (party is null)
        {
            PartyBanner.Visibility = Visibility.Collapsed;
            return;
        }
        PartyBannerText.Text = party.IsHost
            ? $"You're hosting room {party.RoomCode} — it stays open while you browse"
            : $"You're in {party.HostName}'s party, room {party.RoomCode}";
        PartyBanner.Visibility = Visibility.Visible;
    }

    private void ReturnToParty_Click(object sender, RoutedEventArgs e)
    {
        var party = AppServices.CurrentParty;
        var movie = AppServices.CurrentPartyMovie;
        if (party is null || movie is null) return;
        MainWindow.Instance.Navigate(new PlayerView(movie, null, party));
    }

    private void LeaveParty_Click(object sender, RoutedEventArgs e)
    {
        var party = AppServices.CurrentParty;
        if (party is null) return;
        var message = party.IsHost
            ? "End the party? Everyone watching with you will be disconnected."
            : "Leave the party?";
        if (MessageBox.Show(message, "Watch Party", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes) return;
        AppServices.LeaveParty();
        RefreshPartyBanner();
    }

    private void HomeBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new HomeView());

    private void ChangeFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose your movies folder" };
        if (AppServices.Settings.LastFolder is { } last && Directory.Exists(last))
            dlg.InitialDirectory = last;
        if (dlg.ShowDialog() == true) LoadFolder(dlg.FolderName);
    }

    // ---- The film behind the glass -------------------------------------------
    //
    // Pointing at a film puts its still behind the whole window, the way a
    // television's home screen takes on whatever tile is highlighted. It waits
    // for the pointer to settle first: sweeping across a row would otherwise
    // flicker through every film on the way past.

    private readonly DispatcherTimer _ambientDwell = new() { Interval = TimeSpan.FromMilliseconds(260) };
    private MovieItem? _ambientNext;
    private bool _ambientChosen;

    private void MovieCard_Hover(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not MovieItem movie) return;
        _ambientChosen = true;
        _ambientNext = movie;
        _ambientDwell.Stop();
        _ambientDwell.Start();
    }

    private void AmbientDwell_Tick(object? sender, EventArgs e)
    {
        _ambientDwell.Stop();
        if (_ambientNext?.Thumbnail is { } still) MainWindow.Instance.SetAmbient(still);
    }

    /// <summary>
    /// Before anyone has pointed at anything, the film they were last watching
    /// sets the scene. Its still may not be decoded yet, so wait for it —
    /// unless the viewer has started browsing by the time it arrives.
    /// </summary>
    private void OfferAmbient(MovieItem movie)
    {
        if (_ambientChosen) return;
        if (movie.Thumbnail is { } still) { MainWindow.Instance.SetAmbient(still); return; }

        void OnChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(MovieItem.Thumbnail) || movie.Thumbnail is null) return;
            movie.PropertyChanged -= OnChanged;
            Dispatcher.BeginInvoke(() =>
            {
                if (!_ambientChosen && IsLoaded) MainWindow.Instance.SetAmbient(movie.Thumbnail);
            });
        }
        movie.PropertyChanged += OnChanged;
    }

    // ---- Streaming shortcuts -----------------------------------------------

    /// <summary>Browse a service in-app, or save a title you own as a tile.</summary>
    private void StreamingBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var service in StreamingServices.BrowsableServices)
        {
            var item = new MenuItem { Header = $"Browse {service}", Tag = service };
            item.Click += BrowseService_Click;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "Add a title you own…" };
        add.Click += AddShortcutBtn_Click;
        menu.Items.Add(add);

        menu.PlacementTarget = StreamingBtn;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BrowseService_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not string service) return;
        OpenStreamingBrowser(StreamingServices.BrowseUrlFor(service), service);
    }

    /// <summary>
    /// Sign in and browse inside the app; playing a protected title hands off to
    /// the real browser (see StreamingBrowserWindow).
    /// </summary>
    private void OpenStreamingBrowser(string url, string? service)
    {
        var window = new StreamingBrowserWindow(url, service) { Owner = Window.GetWindow(this) };
        window.Show();
    }

    private void AddShortcutBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddShortcutWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        AppServices.Settings.Shortcuts.Add(dlg.Result);
        AppServices.Settings.Save();
        ReloadLibrary();
    }

    // ---- The tile menu: upload, shortcuts, shelves ------------------------------

    /// <summary>
    /// Tailor the tile menu to the tile: "Upload to Vault" only makes sense on
    /// a local file, only for the server owner, and only when there's a server
    /// library to upload into. "Remove shortcut" only on shortcuts.
    /// </summary>
    private void CardMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var movie = (menu.PlacementTarget as FrameworkElement)?.Tag as MovieItem
            ?? (menu.Items[0] as MenuItem)?.Tag as MovieItem;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.Visibility = (item.Header as string) switch
            {
                "Upload to Vault" => movie is { Kind: MovieKind.Local }
                                     && AppServices.Account is { IsSignedIn: true, IsOwner: true }
                    ? Visibility.Visible : Visibility.Collapsed,
                "Remove shortcut" => movie is { IsShortcut: true } ? Visibility.Visible : Visibility.Collapsed,
                "Add to collection" => CanCollect(movie) ? Visibility.Visible : Visibility.Collapsed,
                _ => item.Visibility,
            };
            if (item.Header as string == "Add to collection" && CanCollect(movie))
                BuildCollectMenu(item, movie!);
        }
        // Nothing applies (a local film, signed out): no empty menu.
        if (menu.Items.OfType<MenuItem>().All(i => i.Visibility != Visibility.Visible)) menu.IsOpen = false;
    }

    /// <summary>
    /// Whether a film can go on a shelf at all.
    ///
    /// Shelves hold films the server has, identified by FilmKey. A streaming
    /// shortcut is a URL with no size, and a film handed over inside a party is
    /// a session URL with no size — both degrade FilmKey to something like
    /// "watch|0", which collides with every other such item. Offering the menu
    /// for those would produce shelves full of entries that mean nothing.
    /// </summary>
    private static bool CanCollect(MovieItem? movie) =>
        movie is { IsShortcut: false, SizeBytes: > 0 }
        && AppServices.Account.IsSignedIn
        && AppServices.Catalog.Items.Any(x => x.FilmKey == movie.FilmKey);

    /// <summary>
    /// Rebuild the shelf submenu each time the menu opens: which shelves exist,
    /// and which already hold this film, both change under us.
    /// </summary>
    private void BuildCollectMenu(MenuItem parent, MovieItem movie)
    {
        parent.Items.Clear();
        foreach (var shelf in AppServices.Catalog.Collections)
        {
            var on = shelf.Items.Any(i => string.Equals(i.FilmKey, movie.FilmKey, StringComparison.Ordinal));
            var entry = new MenuItem { Header = shelf.Name, IsCheckable = true, IsChecked = on, Tag = movie };
            var id = shelf.Id;
            // Checking adds, unchecking removes — one control for both, so a
            // film already on a shelf can be taken off from the same place.
            entry.Click += async (_, _) => await ToggleCollectionAsync(id, movie, !on);
            parent.Items.Add(entry);
        }
        if (parent.Items.Count > 0) parent.Items.Add(new Separator());
        var make = new MenuItem { Header = "New collection…", Tag = movie };
        make.Click += async (_, _) => await NewCollectionAsync(movie);
        parent.Items.Add(make);
    }

    private async Task ToggleCollectionAsync(long id, MovieItem movie, bool add)
    {
        var catalog = AppServices.Catalog;
        var result = add
            ? await catalog.AddToCollectionAsync(id, movie.FilmKey)
            : await catalog.RemoveFromCollectionAsync(id, movie.FilmKey);
        if (result is null && catalog.LastError is { Length: > 0 } why)
            MessageBox.Show(Window.GetWindow(this), why, "Collections",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        RefreshCollectionBox();
        ApplyFilter();
        RefreshHome();
    }

    private async Task NewCollectionAsync(MovieItem movie)
    {
        var name = NameCollectionWindow.Ask(Window.GetWindow(this));
        if (name is null) return;

        var catalog = AppServices.Catalog;
        var made = await catalog.CreateCollectionAsync(name);
        if (made is null)
        {
            MessageBox.Show(Window.GetWindow(this), catalog.LastError ?? "Couldn't create that collection.",
                "Collections", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        await ToggleCollectionAsync(made.Id, movie, add: true);
    }

    // ---- The shelf picker on the Films page ------------------------------------

    /// <summary>The chosen shelf, or null for "All films".</summary>
    private CollectionDto? SelectedCollection() =>
        CollectionBox.SelectedItem is ComboBoxItem { Tag: CollectionDto dto } ? dto : null;

    /// <summary>
    /// Rebuild the picker from the current shelves, keeping the chosen one
    /// selected where it survives. This runs on every background sync tick, so
    /// it must not steal the user's selection out from under them.
    /// </summary>
    private void RefreshCollectionBox()
    {
        var shelves = AppServices.Catalog.Collections;
        var wasId = SelectedCollection()?.Id;

        _suppressCollectionChange = true;
        CollectionBox.Items.Clear();
        CollectionBox.Items.Add(new ComboBoxItem { Content = "All films", Tag = null });
        foreach (var shelf in shelves)
            CollectionBox.Items.Add(new ComboBoxItem
            {
                Content = $"{shelf.Name}  ({shelf.Items.Length})",
                Tag = shelf,
            });

        var restored = 0;
        if (wasId is { } id)
        {
            for (var i = 1; i < CollectionBox.Items.Count; i++)
                if (((ComboBoxItem)CollectionBox.Items[i]!).Tag is CollectionDto d && d.Id == id)
                { restored = i; break; }
        }
        CollectionBox.SelectedIndex = restored;
        _suppressCollectionChange = false;

        // Nothing to choose between until a shelf exists; an empty picker is
        // just clutter on a library nobody has organised.
        var show = shelves.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CollectionBox.Visibility = show;
        CollectionIcon.Visibility = show;
    }

    private void CollectionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCollectionChange) return;
        ApplyFilter();
    }

    private void UploadToVault_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not MovieItem movie) return;
        if (AppServices.Uploads.Enqueue(movie))
            new UploadsWindow { Owner = Window.GetWindow(this) }.Show();
        else
            MessageBox.Show("That film is already queued or uploading.", "Uploads",
                MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RemoveShortcut_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as MenuItem)?.Tag is not MovieItem movie || !movie.IsShortcut) return;

        var shortcuts = AppServices.Settings.Shortcuts;
        var match = shortcuts.FirstOrDefault(
            s => s.Title == movie.Name && s.Service == movie.Service);
        if (match is null) return;

        shortcuts.Remove(match);
        AppServices.Settings.Save();
        ReloadLibrary();
    }

    /// <summary>Re-reads the library after the shortcut list changes.</summary>
    private void ReloadLibrary()
    {
        var folder = AppServices.Settings.LastFolder;
        if (folder is not null && Directory.Exists(folder)) LoadFolder(folder);
        else LoadShortcutsOnly();
    }

    /// <summary>
    /// Library with no folder chosen — shortcuts and the Vault library, if
    /// either has anything in it. Someone who only ever streams from the server
    /// never has to point the app at a folder at all.
    /// </summary>
    private void LoadShortcutsOnly()
    {
        _folderMovies = new List<MovieItem>();
        RefreshCountsLine();

        Compose("No folder selected",
            "Choose the folder where your movies live — only video files will be listed.");
    }

    /// <summary>Tracks sign-in transitions; this event also fires on every routine sync.</summary>
    private bool _wasSignedIn = AppServices.Account.IsSignedIn;

    /// <summary>When a background sync updates the library, re-overlay and refresh.</summary>
    private void OnAccountChanged() => Dispatcher.Invoke(() =>
    {
        // Signing in or out changes whether there is a server library at all, so
        // it has to be re-fetched rather than just re-overlaid. Only on the
        // transition though: this event also fires after every routine sync, and
        // re-listing the whole catalogue each time would be pure waste.
        if (AppServices.Account.IsSignedIn != _wasSignedIn)
        {
            _wasSignedIn = AppServices.Account.IsSignedIn;
            _ = LoadServerLibraryAsync();
        }

        if (_movies.Count == 0) return;
        AppServices.Account.ApplyTo(_movies);
        RefreshHome();
        ApplyFilter();
        RefreshMyList();
    });

    public void Dispose()
    {
        _ambientDwell.Stop();
        _heroTimer.Stop();
        if (_heroFilm is not null) _heroFilm.PropertyChanged -= HeroFilm_PropertyChanged;
        _hwnd?.RemoveHook(TiltWheelHook);
        _cts.Cancel();
        AppServices.Account.Changed -= OnAccountChanged;
    }
}
