using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace VideoPlayer.App.Helpers;

/// <summary>Live drag state for a slider driven by <see cref="SliderDragBehavior"/>.</summary>
public sealed class SliderDragHandle
{
    public bool IsDragging { get; internal set; }
}

/// <summary>
/// Rock-solid click-and-drag for WPF sliders. The built-in
/// IsMoveToPointEnabled behaviour is unreliable (track clicks "step" toward
/// the cursor and never become a drag), so this owns the mouse completely:
/// press anywhere = jump there, move = smooth drag, release = commit.
/// </summary>
public static class SliderDragBehavior
{
    public static SliderDragHandle Attach(Slider slider, Action<double>? onPreview, Action<double> onCommit)
    {
        var handle = new SliderDragHandle();

        void Update(MouseEventArgs e)
        {
            var width = slider.ActualWidth;
            if (width <= 0) return;
            var fraction = Math.Clamp(e.GetPosition(slider).X / width, 0.0, 1.0);
            slider.Value = slider.Minimum + fraction * (slider.Maximum - slider.Minimum);
            onPreview?.Invoke(slider.Value);
        }

        slider.PreviewMouseLeftButtonDown += (_, e) =>
        {
            handle.IsDragging = true;
            slider.CaptureMouse();
            Update(e);
            e.Handled = true;
        };

        slider.PreviewMouseMove += (_, e) =>
        {
            if (handle.IsDragging) Update(e);
        };

        slider.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!handle.IsDragging) return;
            handle.IsDragging = false;
            slider.ReleaseMouseCapture();
            Update(e);
            onCommit(slider.Value);
            e.Handled = true;
        };

        // Safety: if capture is lost (alt-tab etc.) commit where we were.
        slider.LostMouseCapture += (_, _) =>
        {
            if (!handle.IsDragging) return;
            handle.IsDragging = false;
            onCommit(slider.Value);
        };

        return handle;
    }
}
