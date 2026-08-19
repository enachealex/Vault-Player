using System.Windows;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>The upload queue: what's sending, what's done, what failed and why.</summary>
public partial class UploadsWindow : Window
{
    public UploadsWindow()
    {
        InitializeComponent();
        Refresh();
        AppServices.Uploads.Changed += OnChanged;
        Unloaded += (_, _) => AppServices.Uploads.Changed -= OnChanged;
    }

    private void OnChanged() => Dispatcher.Invoke(Refresh);

    private void Refresh()
    {
        var jobs = AppServices.Uploads.Jobs;
        JobList.ItemsSource = null;      // per-job progress binds via INotifyPropertyChanged;
        JobList.ItemsSource = jobs;      // the reset covers add/remove and state flips
        EmptyText.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
