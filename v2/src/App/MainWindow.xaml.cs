using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shell;
using VideoPlayer.App.Views;

namespace VideoPlayer.App;

/// <summary>
/// Shell window: hosts one view at a time (Home, Library, Party, Player) and
/// disposes the outgoing view so players/timers never leak across navigation.
/// Draws its own title bar — see MainWindow.xaml for the chrome setup.
/// </summary>
public partial class MainWindow : Window
{
    public static MainWindow Instance { get; private set; } = null!;

    private const int GlyphMaximise = 0xE922;
    private const int GlyphRestore = 0xE923;

    private WindowChrome? _savedChrome;
    private bool _fullscreen;

    public MainWindow()
    {
        InitializeComponent();
        Instance = this;
        Navigate(new HomeView());
        Closed += (_, _) => (Host.Content as IDisposable)?.Dispose();
    }

    public void Navigate(UserControl view)
    {
        (Host.Content as IDisposable)?.Dispose();
        // Cleared on every navigation so it can't outlive the view that set it;
        // the incoming view re-sets it from its own Loaded handler.
        TitleSubtext.Text = "";
        Host.Content = view;
    }

    /// <summary>Secondary line in the title bar, e.g. the movie now playing.</summary>
    public void SetTitleContext(string text) => TitleSubtext.Text = text;

    // ---- Fullscreen --------------------------------------------------------

    /// <summary>
    /// Drop the title bar and the custom chrome for fullscreen playback. The
    /// chrome has to go entirely, not just be hidden: its caption strip would
    /// still eat clicks over the video.
    /// </summary>
    public void EnterFullscreenChrome()
    {
        _savedChrome = WindowChrome.GetWindowChrome(this);
        WindowChrome.SetWindowChrome(this, null);
        TitleBar.Visibility = Visibility.Collapsed;
        _fullscreen = true;
    }

    public void ExitFullscreenChrome()
    {
        _fullscreen = false;
        if (_savedChrome is not null) WindowChrome.SetWindowChrome(this, _savedChrome);
        TitleBar.Visibility = Visibility.Visible;
    }

    // ---- Caption buttons ---------------------------------------------------

    private void SettingsBtn_Click(object sender, RoutedEventArgs e) =>
        new Views.SettingsWindow { Owner = this }.ShowDialog();

    private void MinBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void MaxBtn_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximised = WindowState == WindowState.Maximized;

        // A WindowChrome window maximises to the monitor plus its resize
        // border, overhanging every edge by a few pixels. The overhang itself
        // is harmless (it sits under the always-on-top taskbar), but content
        // would be clipped, so inset by exactly that much. Fullscreen has no
        // chrome and must stay flush.
        RootBorder.Margin = maximised && !_fullscreen
            ? SystemParameters.WindowResizeBorderThickness
            : new Thickness(0);

        MaxBtn.Content = char.ConvertFromUtf32(maximised ? GlyphRestore : GlyphMaximise);
        MaxBtn.ToolTip = maximised ? "Restore" : "Maximise";
        // Keep the accessible name honest — the glyph alone isn't announced.
        AutomationProperties.SetName(MaxBtn, maximised ? "Restore" : "Maximise");
    }
}
