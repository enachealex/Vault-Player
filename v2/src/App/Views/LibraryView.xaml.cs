using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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

    private readonly CancellationTokenSource _cts = new();
    private List<MovieItem> _movies = new();
    private SortMode _sort = SortMode.NameAsc;
    private bool _ready;

    public LibraryView()
    {
        InitializeComponent();
        SortBox.ItemsSource = Sorts.Select(s => s.Label).ToList();
        SortBox.SelectedIndex = 0;
        _ready = true;

        var last = AppServices.Settings.LastFolder;
        if (last is not null && Directory.Exists(last)) LoadFolder(last);
        else LoadShortcutsOnly(); // falls through to the empty state if there are none
    }

    private void LoadFolder(string folder)
    {
        try
        {
            var movies = MovieLibrary.Scan(folder);

            // Attach saved progress + watch counts so cards can show them.
            var resume = AppServices.Settings.ResumePositions;
            var counts = AppServices.Settings.WatchCounts;
            foreach (var movie in movies)
            {
                if (resume.TryGetValue(movie.Path, out var ms)) movie.ResumeMs = ms;
                if (counts.TryGetValue(movie.Path, out var c)) movie.WatchCount = c;
            }

            // Streaming shortcuts sit in the same list so search and sort just work.
            // They're kept out of `movies` (the local-only list) because the
            // duration/thumbnail pipeline below would choke on a URL.
            _movies = movies.Concat(StreamingServices.AsMovieItems(AppServices.Settings.Shortcuts))
                            .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                            .ToList();

            FolderTitle.Text = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : folder;
            var shortcutCount = _movies.Count - movies.Count;
            FolderSubtitle.Text = $"{movies.Count} movie{(movies.Count == 1 ? "" : "s")}"
                + (shortcutCount > 0 ? $"  ·  {shortcutCount} streaming" : "")
                + $"  ·  {folder}";
            FolderBtnText.Text = "Change folder";

            if (_movies.Count == 0)
            {
                Toolbar.Visibility = Visibility.Collapsed;
                AllHeader.Visibility = Visibility.Collapsed;
                MovieList.ItemsSource = null;
                ShowEmpty("No videos in this folder", "Pick a different folder that contains video files.");
            }
            else
            {
                Toolbar.Visibility = Visibility.Visible;
                EmptyState.Visibility = Visibility.Collapsed;
                RefreshContinueRow();
                ApplyFilter();
                _ = RunMetadataPipelineAsync(movies);
            }

            AppServices.Settings.LastFolder = folder;
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            ShowEmpty("Couldn't read that folder", ex.Message);
        }
    }

    private async Task RunMetadataPipelineAsync(List<MovieItem> movies)
    {
        // Durations first (thumbnail frame timing uses them), then posters.
        await MovieLibrary.ProbeDurationsAsync(movies, _cts.Token);
        RefreshContinueRow(); // durations make "x min left" meaningful
        if (_sort is SortMode.LongestFirst or SortMode.ShortestFirst) ApplyFilter();
        await ThumbnailService.EnsureThumbsAsync(movies, _cts.Token);
    }

    /// <summary>Movies with a saved position, most-recently-watched first.</summary>
    private void RefreshContinueRow()
    {
        var watchedAt = AppServices.Settings.LastWatchedAt;
        var inProgress = _movies
            .Where(m => m.ResumeMs > 5000)
            .Where(m => m.DurationMs is not > 0 || m.ResumeMs < m.DurationMs.Value - 15000)
            .OrderByDescending(m => watchedAt.TryGetValue(m.Path, out var t) ? t : 0)
            .ThenByDescending(m => m.ResumeMs)
            .Take(12)
            .ToList();

        ContinueList.ItemsSource = inProgress;
        ContinuePanel.Visibility = inProgress.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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

        var watchedAt = AppServices.Settings.LastWatchedAt;

        // "Most watched" and "Recently watched" are real filters, not just orderings:
        // they show only films actually watched, rather than padding the grid with
        // everything sitting at zero.
        if (_sort == SortMode.MostWatched) view = view.Where(m => m.WatchCount > 0);
        // A film counts as "watched" if it has a timestamp, a saved position, or a
        // completed view — older libraries predate the timestamp, so fall back.
        else if (_sort == SortMode.RecentlyWatched)
            view = view.Where(m => watchedAt.ContainsKey(m.Path) || m.ResumeMs > 0 || m.WatchCount > 0);

        view = _sort switch
        {
            SortMode.NameDesc => view.OrderByDescending(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.LongestFirst => view.OrderByDescending(m => m.DurationMs ?? 0),
            SortMode.ShortestFirst => view.OrderBy(m => m.DurationMs ?? long.MaxValue),
            SortMode.MostWatched => view.OrderByDescending(m => m.WatchCount)
                                        .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
            SortMode.RecentlyWatched => view.OrderByDescending(m => watchedAt.TryGetValue(m.Path, out var t) ? t : 0)
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

        if (movie.IsShortcut)
        {
            if (!StreamingServices.Launch(movie.Path))
                MessageBox.Show(Window.GetWindow(this),
                    $"Couldn't open the link for “{movie.Name}”.",
                    "Video Player", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Only local files belong in the playlist — next/previous must never
        // land on a shortcut the player can't open.
        MainWindow.Instance.Navigate(
            new PlayerView(movie, _movies.Where(m => !m.IsShortcut).ToList()));
    }

    // ---- Streaming shortcuts -----------------------------------------------

    private void AddShortcutBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddShortcutWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() != true || dlg.Result is null) return;

        AppServices.Settings.Shortcuts.Add(dlg.Result);
        AppServices.Settings.Save();
        ReloadLibrary();
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

    /// <summary>Library view when there are shortcuts but no folder chosen yet.</summary>
    private void LoadShortcutsOnly()
    {
        _movies = StreamingServices.AsMovieItems(AppServices.Settings.Shortcuts)
                                   .OrderBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase)
                                   .ToList();
        if (_movies.Count == 0)
        {
            Toolbar.Visibility = Visibility.Collapsed;
            AllHeader.Visibility = Visibility.Collapsed;
            MovieList.ItemsSource = null;
            ShowEmpty("No folder selected",
                "Choose the folder where your movies live — only video files will be listed.");
            return;
        }

        FolderTitle.Text = "Your movies";
        FolderSubtitle.Text = $"{_movies.Count} streaming shortcut{(_movies.Count == 1 ? "" : "s")}  ·  no folder selected";
        Toolbar.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;
        RefreshContinueRow();
        ApplyFilter();
    }

    public void Dispose() => _cts.Cancel();
}
