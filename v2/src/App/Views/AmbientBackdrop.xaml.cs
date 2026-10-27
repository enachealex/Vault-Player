using System;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace VideoPlayer.App.Views;

/// <summary>
/// The film behind the glass. Shows a softened copy of one still, crossfading
/// to the next when asked, and the default dusk when given nothing.
///
/// The blur is baked into a small bitmap once per film rather than applied as a
/// live BlurEffect. A live blur over the whole window re-runs every time
/// anything above it changes — every hover, every card lifting — and a library
/// is nothing but hovers. A 384-pixel image that is already soft, stretched to
/// the window with smooth scaling, looks the same and costs nothing to keep.
/// </summary>
public partial class AmbientBackdrop : UserControl
{
    private static readonly Duration Fade = new(TimeSpan.FromMilliseconds(650));

    // Keyed on the source so revisiting a film reuses its softened copy. Weak,
    // so a library that is closed takes its stills with it.
    private static readonly ConditionalWeakTable<ImageSource, BitmapSource> Softened = new();

    private ImageSource? _current;
    private bool _aOnTop;

    public AmbientBackdrop()
    {
        InitializeComponent();
    }

    /// <summary>The still being shown, or null for the dusk.</summary>
    public ImageSource? Current => _current;

    /// <summary>
    /// Fade to this still, or back to the dusk when null. <paramref name="animate"/>
    /// is off only for snapshots, which are drawn before any clock would tick.
    /// </summary>
    public void Show(ImageSource? still, bool animate = true)
    {
        if (ReferenceEquals(still, _current)) return;
        _current = still;

        if (still is null)
        {
            FadeTo(ArtA, 0, animate);
            FadeTo(ArtB, 0, animate);
            return;
        }

        var soft = Soften(still);
        if (soft is null) return;

        var incoming = _aOnTop ? ArtB : ArtA;
        var outgoing = _aOnTop ? ArtA : ArtB;
        _aOnTop = !_aOnTop;

        incoming.Source = soft;
        // Whichever is arriving goes on top, so the fade is always new-over-old
        // rather than sometimes old-revealing-new.
        Panel.SetZIndex(incoming, 1);
        Panel.SetZIndex(outgoing, 0);
        FadeTo(incoming, 1, animate);
        FadeTo(outgoing, 0, animate);
    }

    private static void FadeTo(UIElement layer, double opacity, bool animate)
    {
        if (!animate)
        {
            layer.BeginAnimation(OpacityProperty, null);
            layer.Opacity = opacity;
            return;
        }
        layer.BeginAnimation(OpacityProperty,
            new DoubleAnimation(opacity, Fade) { EasingFunction = new QuadraticEase() });
    }

    /// <summary>
    /// A small, already-blurred copy of the still. Drawn slightly larger than
    /// it is kept and then cropped, because a blur fades to transparent at the
    /// edges of whatever it is given and would leave a dark frame round the
    /// window.
    /// </summary>
    private static BitmapSource? Soften(ImageSource still)
    {
        if (Softened.TryGetValue(still, out var cached)) return cached;
        if (still is not BitmapSource { PixelWidth: > 0 } bitmap) return null;
        if (bitmap.IsDownloading) return null;

        // Big enough that stretching it to a full window stays smooth rather
        // than showing the blocks it is made of; small enough to be free.
        const int width = 384, height = 216, bleed = 30;
        try
        {
            var image = new Image
            {
                Source = bitmap,
                Stretch = Stretch.UniformToFill,
                Width = width + 2 * bleed,
                Height = height + 2 * bleed,
                Effect = new BlurEffect { Radius = 16, KernelType = KernelType.Gaussian },
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Measure(new Size(image.Width, image.Height));
            image.Arrange(new Rect(0, 0, image.Width, image.Height));

            var target = new RenderTargetBitmap(width + 2 * bleed, height + 2 * bleed, 96, 96, PixelFormats.Pbgra32);
            target.Render(image);
            var soft = new CroppedBitmap(target, new Int32Rect(bleed, bleed, width, height));
            soft.Freeze();
            Softened.AddOrUpdate(still, soft);
            return soft;
        }
        catch
        {
            // A still that will not render costs the ambience, never the app.
            return null;
        }
    }
}
