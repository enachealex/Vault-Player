using System.Windows;
using System.Windows.Controls;

namespace VideoPlayer.App.Helpers;

/// <summary>
/// A ScrollViewer's offset as something a storyboard can animate.
///
/// HorizontalOffset is read-only, so a row cannot glide to its next page of
/// tiles on its own; this attached property forwards every animated value to
/// ScrollToHorizontalOffset. Each animation starts from where the row really
/// is, so a wheel scroll in between never makes it jump back.
/// </summary>
public static class ScrollAnimator
{
    public static readonly DependencyProperty HorizontalOffsetProperty = DependencyProperty.RegisterAttached(
        "HorizontalOffset", typeof(double), typeof(ScrollAnimator),
        new PropertyMetadata(0.0, (d, e) => (d as ScrollViewer)?.ScrollToHorizontalOffset((double)e.NewValue)));

    public static double GetHorizontalOffset(DependencyObject d) => (double)d.GetValue(HorizontalOffsetProperty);
    public static void SetHorizontalOffset(DependencyObject d, double value) => d.SetValue(HorizontalOffsetProperty, value);
}
