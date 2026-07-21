using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// Adjusts how text captions look — size, thickness and background box. The
/// sample updates instantly; the film reloads a beat after the sliders settle
/// (via <paramref name="onApply"/>) so the change is visible on the video too,
/// without thrashing the player on every tick of a drag.
/// </summary>
public partial class CaptionStyleWindow : Window
{
    private readonly Action? _onApply;
    private readonly DispatcherTimer _applyDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private bool _loading;

    public CaptionStyleWindow(Action? onApply = null)
    {
        InitializeComponent();
        _onApply = onApply;
        _applyDebounce.Tick += (_, _) => { _applyDebounce.Stop(); _onApply?.Invoke(); };

        LoadFromSettings();

        SizeSlider.ValueChanged += (_, _) => OnChanged();
        ThicknessSlider.ValueChanged += (_, _) => OnChanged();
        BgSlider.ValueChanged += (_, _) => OnChanged();
    }

    private void LoadFromSettings()
    {
        var s = AppServices.Settings;
        _loading = true;
        SizeSlider.Value = s.SubtitleScalePct;
        ThicknessSlider.Value = s.SubtitleOutline;
        BgSlider.Value = s.SubtitleBackgroundOpacity;
        _loading = false;
        UpdateSampleAndLabels();
    }

    private void OnChanged()
    {
        if (_loading) return;
        var s = AppServices.Settings;
        s.SubtitleScalePct = (int)SizeSlider.Value;
        s.SubtitleOutline = (int)ThicknessSlider.Value;
        // Heavier outline reads as bolder text; tie the two so one slider covers
        // "thickness" rather than exposing a separate bold switch.
        s.SubtitleBold = s.SubtitleOutline >= 3;
        s.SubtitleBackgroundOpacity = (int)BgSlider.Value;
        s.Save();

        UpdateSampleAndLabels();

        // Reload the film once the user pauses, not on every slider step.
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private void UpdateSampleAndLabels()
    {
        var s = AppServices.Settings;
        SizeValue.Text = $"{s.SubtitleScalePct}%";
        ThicknessValue.Text = s.SubtitleOutline == 0 ? "None" : s.SubtitleOutline.ToString();
        BgValue.Text = s.SubtitleBackgroundOpacity == 0
            ? "Off"
            : $"{(int)Math.Round(s.SubtitleBackgroundOpacity / 255.0 * 100)}%";

        // Approximate the libVLC look in the sample: size scales the 18pt base,
        // thickness thickens the outline (drawn as a stroke), bg fills the box.
        SampleText.FontSize = 18 * s.SubtitleScalePct / 100.0;
        SampleText.FontWeight = s.SubtitleBold ? FontWeights.Bold : FontWeights.Normal;
        SampleText.Effect = s.SubtitleOutline > 0
            ? new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Colors.Black,
                ShadowDepth = 0,
                BlurRadius = s.SubtitleOutline * 1.5,
                Opacity = 1,
            }
            : null;
        SampleBox.Background = s.SubtitleBackgroundOpacity > 0
            ? new SolidColorBrush(Color.FromArgb((byte)s.SubtitleBackgroundOpacity, 0, 0, 0))
            : Brushes.Transparent;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var s = AppServices.Settings;
        s.SubtitleScalePct = 110;
        s.SubtitleOutline = 4;
        s.SubtitleBold = true;
        s.SubtitleBackgroundOpacity = 0;
        s.Save();
        LoadFromSettings();
        _applyDebounce.Stop();
        _applyDebounce.Start();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        _applyDebounce.Stop();
        _onApply?.Invoke();   // make sure the final state is applied
        Close();
    }
}
