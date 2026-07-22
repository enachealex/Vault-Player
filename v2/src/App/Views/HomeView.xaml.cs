using System.Windows;
using System.Windows.Controls;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        RefreshAccount();
        AppServices.Account.Changed += RefreshAccount;
        Unloaded += (_, _) => AppServices.Account.Changed -= RefreshAccount;
    }

    private void RefreshAccount() => Dispatcher.Invoke(() =>
        AccountLabel.Text = AppServices.Account.IsSignedIn
            ? AppServices.Account.Name ?? "Account"
            : "Sign in");

    private void AccountBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AccountWindow { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();
        RefreshAccount();
    }

    private void SoloBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new LibraryView());

    private void PartyBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new PartyView());
}
