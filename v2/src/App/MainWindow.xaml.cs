using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Shell;
using VideoPlayer.App.Services;
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
        // Restore the pin before anything is shown, so a window the viewer left
        // on top comes back on top rather than behind whatever they opened next.
        SetAlwaysOnTop(AppServices.Settings.AlwaysOnTop);
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

    // ---- Keep on top -------------------------------------------------------

    /// <summary>
    /// Pin the window above every other application, or let it go again.
    ///
    /// Deliberately not touched by the fullscreen switch: fullscreen restyles
    /// this window and reorders it, and a viewer who asked for the film to stay
    /// on top means it in both states. Topmost survives that on its own, so the
    /// only thing needed is to leave it alone.
    /// </summary>
    public void SetAlwaysOnTop(bool on)
    {
        Topmost = on;
        PinBtn.Opacity = on ? 1.0 : 0.55;
        PinBtn.ToolTip = on
            ? "This window stays on top of other apps (Ctrl+T)"
            : "Keep this window on top of other apps (Ctrl+T)";
        // Filled pin when on, outline when off — the glyph carries the state
        // for anyone who cannot see the opacity difference.
        PinBtn.Content = on ? "" : "";
        AppServices.Settings.AlwaysOnTop = on;
        AppServices.Settings.Save();
    }

    public void ToggleAlwaysOnTop() => SetAlwaysOnTop(!Topmost);

    private void PinBtn_Click(object sender, RoutedEventArgs e) => ToggleAlwaysOnTop();

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
