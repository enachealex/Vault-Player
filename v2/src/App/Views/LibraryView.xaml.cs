using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using VideoPlayer.App.Models;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

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

    private bool _suppressCollectionChange;
    private readonly CancellationTokenSource _cts = new();
    private List<MovieItem> _movies = new();

    // The three sources the grid is built from, kept apart so each can be
    // refreshed on its own: the Vault library arrives over the network long
    // after the folder has been scanned, and re-scanning the folder to show it
    // would be both slow and wrong.
    private List<MovieItem> _folderMovies = new();
    private List<MovieItem> _serverMovies = new();

    private SortMode _sort = SortMode.NameAsc;
    private bool _ready;

    /// <summary>
    /// When set, picking a film hands it back here instead of playing it. Used
    /// by Watch Party so choosing what to host is the same browsing experience
    /// as watching alone — posters, continue watching, search and filters —
    /// rather than a bare list of file names.
    /// </summary>
    private readonly Action<MovieItem>? _onPick;

    public LibraryView() : this(null) { }

    /// <summary>
    /// Width of a card in the grid, worked out from the grid's own width so
    /// whole columns fill it and its right edge lines up with the header's.
    /// Fixed-width cards left a ragged gap down the right of up to a card.
    /// </summary>
    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(LibraryView), new PropertyMetadata(220.0));

    public double CardWidth
    {
        get => (double)GetValue(CardWidthProperty);
        private set => SetValue(CardWidthProperty, value);
    }

    /// <summary>The picture's height on a grid card: 16:9 of whatever width the card has.</summary>
    public static readonly DependencyProperty CardFrameHeightProperty = DependencyProperty.Register(
        nameof(CardFrameHeight), typeof(double), typeof(LibraryView), new PropertyMetadata(116.0));

    public double CardFrameHeight
    {
        get => (double)GetValue(CardFrameHeightProperty);
        private set => SetValue(CardFrameHeightProperty, value);
    }

    private const double MinCardWidth = 220, CardGap = 16;
    /// <summary>The card's padding and edge, both sides: what is not picture.</summary>
    private const double CardChrome = 14;

    private void MovieList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        // The list is one gap wider than the content (a negative margin), so the
        // last card's own right margin hangs outside and the card meets the edge.
        var width = MovieList.ActualWidth;
        var columns = Math.Max(1, (int)(width / (MinCardWidth + CardGap)));
        // Whole pixels: a fractional width blurs every card's edge, and rounding
        // up would wrap the last card onto the next row.
        CardWidth = Math.Floor(width / columns - CardGap);
        CardFrameHeight = Math.Round((CardWidth - CardChrome) * 9 / 16);
    }

    public LibraryView(Action<MovieItem>? onPick)
    {
        InitializeComponent();
        _onPick = onPick;
        _ambientDwell.Tick += AmbientDwell_Tick;
        SortBox.ItemsSource = Sorts.Select(s => s.Label).ToList();
        SortBox.SelectedIndex = 0;
        _ready = true;
        AppServices.Account.Changed += OnAccountChanged;

        if (_onPick is not null)
        {
            PickModeBanner.Visibility = Visibility.Visible;
            // Streaming shortcuts can be hosted too, but nothing else here
            // applies while choosing: the party banner would be about the very
            // session being set up.
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

            // Attach saved progress + watch counts so cards can show them.
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

            FolderTitle.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;
            FolderBtnText.Text = "Change folder";
            FolderSubtitle.Text = CountsLine() + $"  ·  {folder}";

            Compose("No videos in this folder", "Pick a different folder that contains video files.");
            if (_movies.Count > 0) _ = RunMetadataPipelineAsync(movies);

            AppServices.Settings.LastFolder = folder;
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            ShowEmpty("Couldn't read that folder", ex.Message);
        }
    }

    /// <summary>
    /// Rebuild the grid from the folder, the streaming shortcuts and the Vault
    /// library. They share one list on purpose: search, sort and continue-
    /// watching then work across everything you can watch, rather than making
    /// you remember which of three places a film came from.
    /// </summary>
    private void Compose(string emptyTitle, string emptyText)
    {
        _movies = _folderMovies
            .Concat(StreamingServices.AsMovieItems(AppServices.Settings.Shortcuts))
            .Concat(PickableServerMovies())
            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _movies = Deduplicate(_movies);

        if (_movies.Count == 0)
        {
            Toolbar.Visibility = Visibility.Collapsed;
            AllHeader.Visibility = Visibility.Collapsed;
            MovieList.ItemsSource = null;
            ShowEmpty(emptyTitle, emptyText);
            return;
        }

        Toolbar.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        RefreshContinueRow();
        RefreshCollectionBox();
        ApplyFilter();
    }

    /// <summary>
    /// One card per film, even when the same film is in your folder AND on the
    /// Vault.
    ///
    /// The two are genuinely the same film — FilmKey is name-plus-size precisely
    /// so a server copy and a local rip resolve to one identity — and showing
    /// both was always noise. Collections make it worse than noise: a shelf of
    /// four films renders eight cards, which reads as the feature being broken.
    ///
    /// The local copy wins, because it plays off the disk instead of streaming.
    /// Shortcuts are exempt: their key degrades to "&lt;url-segment&gt;|0", so they
    /// would all collide with each other and all but one would vanish.
    /// </summary>
    private static List<MovieItem> Deduplicate(List<MovieItem> movies)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<MovieItem>(movies.Count);

        // Local first so it claims the key, then everything else in the order
        // it already had — the list arrives sorted by name and must stay so.
        foreach (var movie in movies.Where(m => !m.IsOnServer).Concat(movies.Where(m => m.IsOnServer)))
        {
            if (!movie.IsShortcut && movie.SizeBytes > 0 && !seen.Add(movie.FilmKey)) continue;
            result.Add(movie);
        }
        return result.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Server films are hidden while choosing what to host. A host answers byte
    /// ranges by reading the film off its own disk, and a server film is a URL,
    /// not a file — so offering one here would produce a party that cannot
    /// stream. Hosting straight from the server is the next piece of work.
    /// </summary>
    private IEnumerable<MovieItem> PickableServerMovies() =>
        _onPick is null ? _serverMovies : Enumerable.Empty<MovieItem>();

    /// <summary>The "12 movies · 3 streaming · 40 on Vault" line under the title.</summary>
    private string CountsLine()
    {
        var parts = new List<string>();
        // "0 movies" is only worth saying when a folder was actually chosen and
        // turned out to be empty; otherwise it just reads as a complaint.
        if (_folderMovies.Count > 0 || AppServices.Settings.LastFolder is not null)
            parts.Add($"{_folderMovies.Count} movie{(_folderMovies.Count == 1 ? "" : "s")}");

        var shortcuts = AppServices.Settings.Shortcuts.Count;
        if (shortcuts > 0) parts.Add($"{shortcuts} streaming");

        var server = PickableServerMovies().Count();
        if (server > 0) parts.Add($"{server} on Vault");
        else if (AppServices.Catalog.ServerScanning) parts.Add("Vault library still indexing");

        return string.Join("  ·  ", parts);
    }

    /// <summary>
    /// Pull the server's library in, then fetch its posters. Failure is quiet:
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

        // Nothing to say and nothing to take away — leave the grid alone. The
        // `hadFilms` half matters on sign-out: the films are gone from `items`
        // and the grid still has them on screen.
        // Shown whenever there is a server to ask, not only when it already
        // answered with films. A library that has just been set up has none
        // yet, and the person who has this second copied their first film onto
        // it — hiding the one control that would make it appear left them with
        // nothing to do but restart the server.
        VaultBtn.Visibility = (_serverMovies.Count > 0 || AppServices.Account.IsSignedIn) && _onPick is null
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (_serverMovies.Count == 0 && !hadFilms) return;

        RefreshCountsLine();
        Compose("Nothing to watch yet",
            "Choose a folder with your films in it, or add some to the Vault server.");

        await AppServices.Catalog.LoadArtAsync(_serverMovies, _cts.Token);
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

    /// <summary>Refresh just the counts, leaving whatever the loader put in the title.</summary>
    private void RefreshCountsLine()
    {
        var folder = AppServices.Settings.LastFolder;
        FolderSubtitle.Text = folder is not null && Directory.Exists(folder)
            ? CountsLine() + $"  ·  {folder}"
            : CountsLine() + "  ·  no folder selected";
    }

    private async Task RunMetadataPipelineAsync(List<MovieItem> movies)
    {
        // Only parse what isn't already cached — a returning library is instant.
        var need = movies.Where(m => m.DurationMs is null or 0).ToList();
        if (need.Count > 0)
        {
            await MovieLibrary.ProbeDurationsAsync(need, _cts.Token);
            PersistDurations(need);
            RefreshContinueRow(); // durations make "x min left" meaningful
            if (_sort is SortMode.LongestFirst or SortMode.ShortestFirst) ApplyFilter();
        }
        // Posters still run every time; ThumbnailService already caches to disk.
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

    /// <summary>Movies with a saved position, most-recently-watched first.</summary>
    private void RefreshContinueRow()
    {
        var watchedAt = AppServices.Settings.LastWatchedAt;
        var inProgress = _movies
            .Where(m => m.ResumeMs > 5000)
            .Where(m => m.DurationMs is not > 0 || m.ResumeMs < m.DurationMs.Value - 15000)
            .OrderByDescending(m => watchedAt.TryGetValue(m.StoreKey, out var t) ? t : 0)
            .ThenByDescending(m => m.ResumeMs)
            .Take(12)
            .ToList();

        ContinueList.ItemsSource = inProgress;
        ContinuePanel.Visibility = inProgress.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (inProgress.Count > 0) OfferAmbient(inProgress[0]);
        // AllHeader is owned by ApplyFilter — it names the active view, not just this section.
    }

    // ---- Search + sort -----------------------------------------------------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchBtn.Visibility = SearchBox.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ApplyFilter();
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || SortBox.SelectedIndex < 0) return;
        _sort = Sorts[SortBox.SelectedIndex].Mode;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_movies.Count == 0) return;
        var query = SearchBox.Text.Trim();
        IEnumerable<MovieItem> view = _movies;

        if (query.Length > 0)
            view = view.Where(m => m.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        // A shelf narrows the grid to its films. Matched on FilmKey rather than
        // on anything stored on the item: Compose rebuilds every MovieItem from
        // scratch on each refresh, so a flag written onto an instance would be
        // gone by the next background sync tick.
        //
        // Matching by key also means a film you own locally AND have on the
        // server resolves from whichever copy is in the grid, without the shelf
        // needing to know which one you are looking at.
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
            view = view.Where(m => watchedAt.ContainsKey(m.StoreKey) || m.ResumeMs > 0 || m.WatchCount > 0);

        view = _sort switch
        {
            SortMode.NameDesc => view.OrderByDescending(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.LongestFirst => view.OrderByDescending(m => m.DurationMs ?? 0),
            SortMode.ShortestFirst => view.OrderBy(m => m.DurationMs ?? long.MaxValue),
            SortMode.MostWatched => view.OrderByDescending(m => m.WatchCount)
                                        .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.RecentlyWatched => view.OrderByDescending(m => watchedAt.TryGetValue(m.StoreKey, out var t) ? t : 0)
                                            .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => view.OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
        };

        var result = view.ToList();
        MovieList.ItemsSource = result;

        // Header names the active view; the count makes it obvious when films are hidden.
        AllHeader.Text = _sort switch
        {
            SortMode.MostWatched => "Most watched",
            SortMode.RecentlyWatched => "Recently watched",
            _ => "All movies",
        };
        AllHeader.Visibility = Visibility.Visible;
        FilterCount.Text = result.Count == _movies.Count ? "" : $"{result.Count} of {_movies.Count}";

        if (result.Count == 0 && query.Length == 0
            && _sort is SortMode.MostWatched or SortMode.RecentlyWatched)
        {
            EmptyGlyph.Text = char.ConvertFromUtf32(0xE714);
            ShowEmpty("Nothing watched yet",
                "Finish a film and it'll show up here — it counts once you reach the last 10%.");
        }
        else if (result.Count == 0 && query.Length > 0)
        {
            EmptyGlyph.Text = "\uE721";
            ShowEmpty($"Nothing matches “{query}”", "Try a different word, or clear the search.");
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowEmpty(string title, string text)
    {
        EmptyTitle.Text = title;
        EmptyText.Text = text;
        EmptyState.Visibility = Visibility.Visible;
    }

    // ---- Navigation --------------------------------------------------------

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
        if (dlg.ShowDialog() == true)
        {
            SearchBox.Clear();
            LoadFolder(dlg.FolderName);
        }
    }

    private void MovieCard_Click(object sender, RoutedEventArgs e)
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

        // Only local files belong in the playlist — next/previous must never
        // land on a shortcut the player can't open.
        MainWindow.Instance.Navigate(
            new PlayerView(movie, _movies.Where(m => !m.IsShortcut).ToList()));
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

    /// <summary>Browse a service in-app, or save a title you own as a card.</summary>
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

    // ---- Uploading to the server -------------------------------------------

    /// <summary>
    /// Tailor the card menu to the card: "Upload to Vault" only makes sense on
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

    // ---- The shelf picker in the toolbar -----------------------------------

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
    /// Library view with no folder chosen — shortcuts and the Vault library, if
    /// either has anything in it. Someone who only ever streams from the server
    /// never has to point the app at a folder at all.
    /// </summary>
    private void LoadShortcutsOnly()
    {
        _folderMovies = new List<MovieItem>();
        FolderTitle.Text = "Your movies";
        FolderSubtitle.Text = CountsLine() + "  ·  no folder selected";

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
        RefreshContinueRow();
        ApplyFilter();
    });

    public void Dispose()
    {
        _ambientDwell.Stop();
        _cts.Cancel();
        AppServices.Account.Changed -= OnAccountChanged;
    }
}
