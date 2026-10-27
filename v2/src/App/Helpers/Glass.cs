using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace VideoPlayer.App.Helpers;

/// <summary>
/// Real glass for dialogs: Windows 11's acrylic backdrop behind the window,
/// a dark title bar on top of it, and the app's tint laid over the blur so
/// text stays readable whatever is behind the dialog.
///
/// Set in XAML on the window itself: <c>ui:Glass.Window="True"</c>.
///
/// Before this, every dialog had a white system title bar sitting on a dark
/// window, which was the loudest thing on the screen in every one of them.
/// The dark caption alone fixes that, and works back to Windows 10 2004; the
/// acrylic needs Windows 11 22H2, and older builds simply keep the dialog's
/// own solid background, which is exactly what they had before.
/// </summary>
public static class Glass
{
    public static readonly DependencyProperty WindowProperty =
        DependencyProperty.RegisterAttached("Window", typeof(bool), typeof(Glass),
            new PropertyMetadata(false, OnWindowChanged));

    public static bool GetWindow(DependencyObject d) => (bool)d.GetValue(WindowProperty);
    public static void SetWindow(DependencyObject d, bool value) => d.SetValue(WindowProperty, value);

    private static void OnWindowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window window || e.NewValue is not true) return;
        // XAML sets this during InitializeComponent, long before there is a
        // window handle to talk to DWM about.
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero) Apply(window);
        else window.SourceInitialized += (_, _) => Apply(window);
    }

    /// <summary>The first build with DWMWA_SYSTEMBACKDROP_TYPE (Windows 11 22H2).</summary>
    private static bool SupportsBackdrop => Environment.OSVersion.Version.Build >= 22621;

    private static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var on = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));

            if (!SupportsBackdrop) return;

            // The order matters: WPF paints an opaque background into the
            // window's surface by default, which would cover the backdrop
            // entirely. Clear that first, then let the frame (and its
            // backdrop) extend under the whole client area.
            if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target })
                target.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(hwnd, ref margins);
            var backdrop = DWMSBT_TRANSIENTWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) != 0)
                return;   // refused: keep the solid background the XAML gave it

            if (window.TryFindResource("GlassWindowTintBrush") is Brush tint)
                window.Background = tint;
        }
        catch
        {
            // Purely cosmetic. A dialog that cannot be glass is still a dialog.
        }
    }

    // ---- DWM ------------------------------------------------------------------

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_TRANSIENTWINDOW = 3;   // acrylic

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);
}
