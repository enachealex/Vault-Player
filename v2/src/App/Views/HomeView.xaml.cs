using System.Windows;
using System.Windows.Controls;

namespace VideoPlayer.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void SoloBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new LibraryView());

    private void PartyBtn_Click(object sender, RoutedEventArgs e) =>
        MainWindow.Instance.Navigate(new PartyView());
}
