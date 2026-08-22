using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// App settings. Currently the audio output device, chosen from libVLC's list so
/// sound can be pointed at the right speakers or headphones. The choice is
/// applied to any film playing now and remembered for future ones.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        BuildDeviceList();
        ShowLibrary();
    }

    /// <summary>Say where films are coming from, in the terms the setup screen used.</summary>
    private void ShowLibrary()
    {
        var settings = AppServices.Settings;
        switch (settings.LibraryMode)
        {
            case "this-pc":
                LibraryModeText.Text = "Hosted on this PC";
                LibraryWhereText.Text = AppServices.Library.IsRunning
                    ? settings.HostFilmsFolder ?? ""
                    : "Not running — " + (settings.HostFilmsFolder ?? "no folder chosen");
                break;
            case "own-server":
                LibraryModeText.Text = "On a server you run";
                LibraryWhereText.Text = settings.AuthServer ?? "";
                break;
            case "none":
                LibraryModeText.Text = "Local files only";
                LibraryWhereText.Text = "No library server.";
                break;
            default:
                LibraryModeText.Text = "Not set up yet";
                LibraryWhereText.Text = "Choose where your films are served from.";
                break;
        }
    }

    private void Library_Click(object sender, RoutedEventArgs e)
    {
        new LibrarySetupWindow { Owner = this }.ShowDialog();
        ShowLibrary();
    }

    private void BuildDeviceList()
    {
        DeviceList.Children.Clear();
        var selected = AppServices.Audio.SelectedId;
        var devices = AppServices.Audio.Devices();

        foreach (var device in devices)
            DeviceList.Children.Add(MakeRow(device, isSelected: device.Id == selected));

        // Only "System default" came back — enumeration is unavailable (rare).
        AudioHint.Text = devices.Count <= 1
            ? "No selectable devices were found; the system default is being used."
            : "Applies to the film playing now and every film after.";
    }

    /// <summary>A clickable device row with a tick on the chosen one.</summary>
    private Border MakeRow(AudioService.Device device, bool isSelected)
    {
        var name = new TextBlock
        {
            Text = device.Name,
            FontSize = 13.5,
            Foreground = (Brush)FindResource("TextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 0);

        var tick = new TextBlock
        {
            Text = char.ConvertFromUtf32(0xE73E), // CheckMark glyph
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = (Brush)FindResource("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(tick, 1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(name);
        grid.Children.Add(tick);

        var row = new Border
        {
            Background = isSelected ? (Brush)FindResource("SurfaceAltBrush") : Brushes.Transparent,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            Child = grid,
        };
        AutomationProperties.SetName(row, device.Name);
        row.MouseLeftButtonUp += (_, _) =>
        {
            AppServices.Audio.Select(device.Id);
            BuildDeviceList();   // redraw ticks
        };
        return row;
    }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();
}
