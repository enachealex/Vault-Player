using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VideoPlayer.App.Models;
using VideoPlayer.App.Views;

namespace VideoPlayer.App;

/// <summary>
/// Headless pictures of the interface: <c>--snapshot-ui &lt;folder&gt; [PlayerView.xaml]</c>
/// renders the main screens and every dialog to PNGs and exits, without
/// opening a single window. Given the player's XAML source, it draws that too.
///
/// A restyle is checked by looking at it, and looking at it used to mean
/// launching the app — which takes the screen, the keyboard and whatever the
/// person at the machine was doing. Rendering off-screen with
/// RenderTargetBitmap takes nothing. What it cannot show is anything the
/// operating system draws: the acrylic behind a dialog, the title bar, and
/// the video itself, which is a native window WPF never sees. Dialogs are
/// drawn over the app's own backdrop to stand in for the acrylic.
/// </summary>
public static class UiSnapshot
{
    private const int Width = 1280, Height = 800;

    public static int Run(string outDir, string? playerXaml = null)
    {
        Directory.CreateDirectory(outDir);
        var failures = 0;

        void Try(string name, Action shot)
        {
            try { shot(); Console.WriteLine($"ok    {name}"); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The real shell, lifted out of its window so it can be laid out and
        // drawn without one. Navigate() and the backdrop still work, because
        // they only touch the elements, never the window.
        var shell = new MainWindow();
        var root = (FrameworkElement)shell.Content;
        shell.Content = null;
        var frame = Frame(root, Width, Height);

        Try("home", () => Save(frame, Path.Combine(outDir, "home.png")));

        var library = new LibraryView();
        Try("library", () =>
        {
            shell.Navigate(library);
            // The folder scan and the server catalogue both land later; give
            // them a moment so the picture is of a library, not a spinner.
            Pump(TimeSpan.FromSeconds(6));
            var still = FirstStill(library);
            if (still is not null) shell.Ambient.Show(still, animate: false);
            Save(frame, Path.Combine(outDir, "library.png"));
        });

        // IsMouseOver cannot be set from outside, so the lift a hover gives a
        // card is applied by hand to the first card of each row. A scroll edge
        // that clips it shows up here as a missing top border.
        Try("library-hover", () =>
        {
            foreach (var list in new[] { library.ContinueList, library.MovieList })
                if (FirstDescendant<Button>(list) is { } card) card.RenderTransform = new TranslateTransform(0, -4);
            Save(frame, Path.Combine(outDir, "library-hover.png"));
        });

        Try("home-with-film", () =>
        {
            var home = new HomeView();
            shell.Navigate(home);
            Save(frame, Path.Combine(outDir, "home-with-film.png"));
            home.SoloBtn.RenderTransform = new TranslateTransform(0, -4);
            Save(frame, Path.Combine(outDir, "home-hover.png"));
        });

        Try("party", () =>
        {
            shell.Navigate(new PartyView());
            Pump(TimeSpan.FromSeconds(1));
            Save(frame, Path.Combine(outDir, "party.png"));
        });

        // The player cannot be built for real without opening a film, which
        // means sound. Its chrome is drawn from the XAML instead, over a still
        // where the picture would be: handlers stripped (they belong to the
        // code-behind) and the VideoView swapped for a plain Grid. That also
        // proves the markup parses and every resource it names exists, which
        // nothing else checks before somebody presses Play.
        if (playerXaml is not null && File.Exists(playerXaml))
        {
            Try("player", () =>
            {
                var xaml = File.ReadAllText(playerXaml);
                xaml = Regex.Replace(xaml, @"\sx:Class=""[^""]+""", "");
                xaml = Regex.Replace(xaml, @"\s(Click|MouseLeftButtonDown|KeyDown|ContextMenuOpening)=""[A-Za-z0-9_]+""", "");
                xaml = Regex.Replace(xaml, @"\sxmlns:vlc=""[^""]+""", "");
                xaml = xaml.Replace("<vlc:VideoView", "<Grid").Replace("</vlc:VideoView>", "</Grid>");
                var player = (UserControl)XamlReader.Parse(xaml);

                var still = shell.Ambient.Current ?? FirstStill(library);
                if (still is not null && player.FindName("Video") is Grid picture)
                    picture.Background = new ImageBrush(still) { Stretch = Stretch.UniformToFill };
                if (player.FindName("TitleText") is TextBlock title) title.Text = "Avatar Fire and Ash";
                if (player.FindName("TimeText") is TextBlock at) at.Text = "1:12:40";
                if (player.FindName("DurationText") is TextBlock left) left.Text = "3:17:24";
                if (player.FindName("InfoText") is TextBlock info) info.Text = "HEVC · 2160p · E-AC-3 5.1";
                if (player.FindName("Seek") is Slider seek) seek.Value = 370;

                shell.Navigate(player);
                Save(frame, Path.Combine(outDir, "player.png"));
            });
        }

        foreach (var (name, make) in new (string, Func<Window>)[]
        {
            ("settings", () => new SettingsWindow()),
            ("library-setup", () => new LibrarySetupWindow()),
            ("account", () => new AccountWindow()),
            ("caption-style", () => new CaptionStyleWindow()),
            ("friends", () => new FriendsWindow()),
            ("uploads", () => new UploadsWindow()),
            ("manage-users", () => new ManageUsersWindow()),
            ("add-shortcut", () => new AddShortcutWindow()),
            ("name-collection", () => new NameCollectionWindow()),
            ("splash", () => new SplashWindow()),
        })
        {
            Try(name, () =>
            {
                var window = make();
                var content = (FrameworkElement)window.Content;
                var width = double.IsNaN(window.Width) ? 460 : window.Width;
                window.Content = null;

                // Glass dialogs get the tint Helpers.Glass lays over the
                // acrylic; the splash draws its own card on a clear window.
                var dialog = new Grid { Width = width };
                var behind = new AmbientBackdrop();
                if (shell.Ambient.Current is { } still) behind.Show(still, animate: false);
                dialog.Children.Add(behind);
                dialog.Children.Add(new Border
                {
                    Background = window is SplashWindow
                        ? Brushes.Transparent
                        : (Brush)Application.Current.FindResource("GlassWindowTintBrush"),
                    Child = content,
                });
                dialog.Measure(new Size(width, double.PositiveInfinity));
                var height = Math.Ceiling(window is SplashWindow ? window.Height : dialog.DesiredSize.Height);
                Save(Frame(dialog, (int)width, (int)height), Path.Combine(outDir, $"dialog-{name}.png"));                window.Close();
            });
        }

        shell.Close();
        return failures;
    }

    private static ImageSource? FirstStill(LibraryView library) =>
        (library.MovieList.ItemsSource as IEnumerable<MovieItem>)?
            .Select(m => m.Thumbnail).FirstOrDefault(t => t is not null);

    private static T? FirstDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if ((child as T ?? FirstDescendant<T>(child)) is { } found) return found;
        }
        return null;
    }

    /// <summary>A fixed-size container carrying what the window would have given its content.</summary>
    private static Grid Frame(FrameworkElement content, int width, int height)
    {
        var grid = new Grid { Width = width, Height = height };
        TextElement.SetFontFamily(grid, (FontFamily)Application.Current.FindResource("BodyFont"));
        TextElement.SetForeground(grid, (Brush)Application.Current.FindResource("TextBrush"));
        grid.Children.Add(content);
        return grid;
    }

    private static void Save(FrameworkElement element, string path)
    {
        var size = new Size(element.Width, element.Height);
        element.Measure(size);
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        // Let bindings and image decodes that the layout just started finish.
        Pump(TimeSpan.FromMilliseconds(300));
        element.UpdateLayout();

        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        png.Save(file);
    }

    private static void Pump(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = time };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
