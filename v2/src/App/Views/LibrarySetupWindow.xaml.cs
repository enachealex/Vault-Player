using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using VideoPlayer.App.Services;

namespace VideoPlayer.App.Views;

/// <summary>
/// The one screen that answers "where are my films?".
///
/// Three routes, and the app is useful under all of them: host here, connect to
/// a server the user already runs, or nothing at all. What they have in common
/// is the thing worth being clear about — the films sit on hardware the user
/// owns in every case. This app never uploads them anywhere.
///
/// Underneath, all three come down to one setting: which address the app talks
/// to. Hosting here additionally starts the server that ships in this
/// installation and points the app at 127.0.0.1.
/// </summary>
public partial class LibrarySetupWindow : Window
{
    /// <summary>Where the self-hosting instructions live, when no server is configured yet.</summary>
    private const string PublicSite = "https://party.thejumpvault.com";

    private bool _loading = true;

    public LibrarySetupWindow()
    {
        InitializeComponent();

        var settings = AppServices.Settings;
        FolderText.Text = settings.HostFilmsFolder ?? "No folder chosen yet";
        ShareBox.IsChecked = settings.HostShareOnNetwork;
        AutoStartBox.IsChecked = settings.HostAutoStart;
        ServerBox.Text = settings.LibraryMode == "own-server" ? settings.AuthServer ?? "" : "";

        switch (settings.LibraryMode)
        {
            case "this-pc": ThisPcChoice.IsChecked = true; break;
            case "own-server": OwnServerChoice.IsChecked = true; break;
            case "none": NoneChoice.IsChecked = true; break;
            default: ThisPcChoice.IsChecked = true; break;   // the route most people want
        }

        _loading = false;
        AppServices.Library.Changed += OnServerChanged;
        Closed += (_, _) => AppServices.Library.Changed -= OnServerChanged;

        ApplyMode();
        RefreshStatus();
    }

    private void OnServerChanged() => Dispatcher.Invoke(RefreshStatus);

    // ---- Mode ---------------------------------------------------------------

    private string SelectedMode =>
        ThisPcChoice.IsChecked == true ? "this-pc"
        : OwnServerChoice.IsChecked == true ? "own-server"
        : "none";

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        ApplyMode();
        RefreshStatus();
    }

    /// <summary>Grey out the routes not chosen, so only one panel invites input.</summary>
    private void ApplyMode()
    {
        var mode = SelectedMode;
        ThisPcBody.IsEnabled = mode == "this-pc";
        ThisPcBody.Opacity = mode == "this-pc" ? 1 : 0.45;
        OwnServerBody.IsEnabled = mode == "own-server";
        OwnServerBody.Opacity = mode == "own-server" ? 1 : 0.45;
    }

    // ---- Hosting on this PC -------------------------------------------------

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the folder your films are in" };
        var current = AppServices.Settings.HostFilmsFolder;
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)) dlg.InitialDirectory = current;
        if (dlg.ShowDialog(this) != true) return;

        AppServices.Settings.HostFilmsFolder = dlg.FolderName;
        AppServices.Settings.Save();
        FolderText.Text = dlg.FolderName;

        // A folder change while running means the server is still indexing the
        // old one. Restart rather than leave the screen claiming otherwise.
        if (AppServices.Library.IsRunning)
        {
            AppServices.Library.Stop();
            _ = StartAsync();
        }
        else
        {
            RefreshStatus();
        }
    }

    private void Share_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.HostShareOnNetwork = ShareBox.IsChecked == true;
        AppServices.Settings.Save();

        // This decides which addresses the server binds, which it only does at
        // startup — so a running server has to come back for it to mean anything.
        if (AppServices.Library.IsRunning)
        {
            AppServices.Library.Stop();
            _ = StartAsync();
        }
        else
        {
            RefreshStatus();
        }
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AppServices.Settings.HostAutoStart = AutoStartBox.IsChecked == true;
        AppServices.Settings.Save();
    }

    private void Start_Click(object sender, RoutedEventArgs e) => _ = StartAsync();

    private async System.Threading.Tasks.Task StartAsync()
    {
        var folder = AppServices.Settings.HostFilmsFolder;
        if (string.IsNullOrWhiteSpace(folder))
        {
            StatusText.Text = "Choose the folder your films are in first.";
            AddressRow.Visibility = Visibility.Collapsed;
            return;
        }

        StartButton.IsEnabled = false;
        StatusText.Text = "Starting…";
        AddressRow.Visibility = Visibility.Collapsed;

        var ok = await AppServices.Library.StartAsync(folder, AppServices.Settings.HostShareOnNetwork);
        StartButton.IsEnabled = true;

        if (ok)
        {
            // Hosting here only means anything if the app also talks to here.
            UseServer(LibraryServer.LocalAddress, "this-pc");
        }
        RefreshStatus();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        AppServices.Library.Stop();
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (SelectedMode != "this-pc")
        {
            StatusText.Text = AppServices.Library.IsRunning
                ? "Still hosting from this PC. Press Stop if you no longer want to."
                : "Not hosting from this PC.";
            AddressRow.Visibility = Visibility.Collapsed;
            FooterNote.Text = SelectedMode == "own-server"
                ? Describe(AppServices.Settings.AuthServer)
                : "Local files only.";
            StopButton.IsEnabled = AppServices.Library.IsRunning;
            return;
        }

        var error = AppServices.Library.LastError;
        if (AppServices.Library.IsRunning)
        {
            var lan = AppServices.Settings.HostShareOnNetwork ? LibraryServer.LanAddress() : null;
            StatusText.Text = "Running. Your films are being indexed and are ready to watch.";
            if (lan is not null)
            {
                AddressText.Text = lan;
                AddressRow.Visibility = Visibility.Visible;
            }
            else
            {
                AddressRow.Visibility = Visibility.Collapsed;
                if (AppServices.Settings.HostShareOnNetwork)
                    StatusText.Text += " This PC has no network address right now, so other devices can't reach it yet.";
            }
            FooterNote.Text = "Hosting from this PC.";

            // Said here and not only in a log, because the failure it causes
            // looks nothing like its cause: films play perfectly on this PC and
            // every one of them is refused by the television.
            var missing = LibraryServer.MissingTools();
            if (missing.Length > 0)
                StatusText.Text += Environment.NewLine + Environment.NewLine
                    + "This installation is missing " + string.Join(" and ", missing)
                    + ", so films will not play on a phone or a television. Reinstalling should restore them.";
        }
        else
        {
            StatusText.Text = error ?? "Not running.";
            AddressRow.Visibility = Visibility.Collapsed;
            FooterNote.Text = "Not hosting.";
        }
        StopButton.IsEnabled = AppServices.Library.IsRunning;
    }

    private static string Describe(string? address) =>
        string.IsNullOrWhiteSpace(address) ? "No server configured." : "Using " + address;

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(AddressText.Text); } catch { /* another app holds the clipboard */ }
    }

    // ---- A server they run themselves ---------------------------------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        ServerHint.Text = "Checking…";

        var (ok, message) = await LibraryServer.ProbeAsync(ServerBox.Text);
        ServerHint.Text = message;
        ConnectButton.IsEnabled = true;
        if (!ok) return;

        var address = ServerBox.Text.Trim().TrimEnd('/');
        if (!address.Contains("://")) address = "http://" + address;
        ServerBox.Text = address;

        // A library hosted here would now be answering for nobody, and would
        // still hold the port a party wants. Stop it.
        AppServices.Library.Stop();
        UseServer(address, "own-server");
        RefreshStatus();
    }

    private void SelfHostHelp_Click(object sender, RoutedEventArgs e) => Open(Site() + "/setup");

    private void TvHelp_Click(object sender, RoutedEventArgs e)
    {
        // The instructions have to come from the server the television will
        // actually talk to, because the address in them is that server's.
        var host = AppServices.Library.IsRunning ? LibraryServer.LanAddress() : null;
        Open((host ?? Site()) + "/tv/install");
    }

    /// <summary>
    /// Where to send somebody for instructions: their own server when one is
    /// answering, ours otherwise.
    ///
    /// Their own comes first because it is the one that still works with the
    /// internet down — which is a state somebody setting up a home library gets
    /// into more often than most.
    /// </summary>
    private static string Site()
    {
        if (AppServices.Library.IsRunning) return LibraryServer.LocalAddress;
        var configured = AppServices.Settings.AuthServer;
        return string.IsNullOrWhiteSpace(configured) || configured.Contains("127.0.0.1")
            ? PublicSite
            : configured.TrimEnd('/');
    }

    private static void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open your browser.\n\n{url}\n\n{ex.Message}",
                "Vault Movies", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Shared -------------------------------------------------------------

    /// <summary>
    /// Point the app at <paramref name="address"/>.
    ///
    /// Signing out is part of this and not an inconvenience to be avoided: an
    /// account lives on one server, and its refresh token means nothing to a
    /// different one. Carrying it across would leave somebody looking at a
    /// signed-in name while every request behind it failed.
    /// </summary>
    private static void UseServer(string address, string mode)
    {
        var settings = AppServices.Settings;
        var changed = !string.Equals(settings.AuthServer?.TrimEnd('/'), address.TrimEnd('/'),
                                     StringComparison.OrdinalIgnoreCase);
        if (changed && AppServices.Account.IsSignedIn) AppServices.Account.SignOut();

        settings.AuthServer = address;
        settings.LibraryMode = mode;
        settings.SetupDone = true;
        settings.Save();
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppServices.Settings;
        var mode = SelectedMode;

        if (mode == "none")
        {
            AppServices.Library.Stop();
            if (AppServices.Account.IsSignedIn && settings.AuthServer?.Contains("127.0.0.1") == true)
                AppServices.Account.SignOut();
            settings.AuthServer = null;      // back to the public relay for parties
            settings.LibraryMode = "none";
        }
        else
        {
            // "this-pc" and "own-server" are committed by Start and Connect,
            // which are the points at which they are known to work. Recording
            // them here as well would let somebody tick a route, never make it
            // work, and be left pointed at an address that answers to nothing.
            settings.LibraryMode = mode;
        }

        settings.SetupDone = true;
        settings.Save();
        Close();
    }
}
