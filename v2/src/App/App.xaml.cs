using System;
using System.IO;
using System.Windows;
using Velopack;

namespace VideoPlayer.App;

public partial class App : Application
{
    private static bool _headless;

    public App()
    {
        Services.StartupTrace.Mark("App ctor entry");

        // Must run before anything else touches the app: this handles the
        // install/update/uninstall hooks and can exit the process outright.
        VelopackApp.Build().Run();
        Services.StartupTrace.Mark("VelopackApp.Run");

        // A film stopping dead with no explanation is the worst outcome, so
        // record what happened and keep going where we safely can.
        //
        // NOTE: this catches managed exceptions only. Access violations inside
        // libvlc.dll terminate the process outright and never reach here — for
        // those, a crash dump is the only evidence (see docs/crashes.md).
        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash("UI thread", e.Exception);
            // A headless run has nobody to read a dialog, and a dialog would
            // take the screen from whoever is at the machine.
            if (_headless)
            {
                Console.WriteLine($"FAIL  unhandled: {e.Exception.GetType().Name}: {e.Exception.Message}");
                e.Handled = true;
                return;
            }
            MessageBox.Show(
                $"Something went wrong, but the app is still running.\n\n{e.Exception.Message}",
                "Vault Movies", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogCrash("background thread", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("task", e.Exception);
            e.SetObserved();
        };
    }

    private static void LogCrash(string where, Exception? ex)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VideoPlayerV2", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{where}]{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing useful to do if even logging fails.
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Headless verification mode: `--selftest <video> [outPath]` plays the
        // file without any UI, checks decode/seek behaviour, writes a JSON
        // report, and exits. Used to prove codec support automatically.
        var i = Array.IndexOf(e.Args, "--selftest");
        if (i >= 0 && i + 1 < e.Args.Length)
        {
            var file = e.Args[i + 1];
            var outPath = i + 2 < e.Args.Length
                ? e.Args[i + 2]
                : Path.Combine(AppContext.BaseDirectory, "selftest-result.json");
            var exitCode = SelfTest.Run(file, outPath);
            Shutdown(exitCode);
            return;
        }

        // Headless: prove what a finished film does, and what revives it.
        //   --check-replay <video>
        var rp = Array.IndexOf(e.Args, "--check-replay");
        if (rp >= 0 && rp + 1 < e.Args.Length)
        {
            // Off the startup thread, like --list-dlna above: this one sleeps
            // while it waits for a film to finish, and libVLC's own callbacks
            // have nowhere to land on a blocked WPF dispatcher.
            var file = e.Args[rp + 1];
            var vlcArgs = e.Args.Length > rp + 2 ? e.Args[(rp + 2)..] : null;
            var code = System.Threading.Tasks.Task.Run(() => ReplayTest.Run(file, vlcArgs))
                .GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        // Headless DLNA scan: --list-dlna [out.json] [seconds]
        var d = Array.IndexOf(e.Args, "--list-dlna");
        if (d >= 0)
        {
            var outPath = d + 1 < e.Args.Length
                ? e.Args[d + 1]
                : Path.Combine(AppContext.BaseDirectory, "dlna.json");
            var seconds = d + 2 < e.Args.Length && int.TryParse(e.Args[d + 2], out var ds) ? ds : 5;
            // Task.Run: keep the SSDP awaits off the (blocked) startup thread.
            var code = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var found = await Services.DlnaService.DiscoverAsync(seconds);
                    var json = System.Text.Json.JsonSerializer.Serialize(
                        new { devices = found.Select(f => new { f.Name, ControlUrl = f.ControlUrl.ToString() }) },
                        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(outPath, json);
                    return 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(outPath, "{\"error\": " + System.Text.Json.JsonSerializer.Serialize(ex.ToString()) + "}");
                    return 1;
                }
            }).GetAwaiter().GetResult();
            Shutdown(code);
            return;
        }

        // Headless: construct every window and exit 0 if they all load.
        //
        // XAML failures are runtime failures. A duplicated attribute, a stray
        // text node, or a StaticResource whose key does not exist all compile
        // perfectly and then throw the first time somebody opens that screen —
        // twice now, in this app. This makes that a check rather than a
        // discovery.
        if (Array.IndexOf(e.Args, "--check-windows") >= 0)
        {
            var failures = 0;
            foreach (var (name, make) in new (string, Func<Window>)[]
            {
                ("SettingsWindow", () => new Views.SettingsWindow()),
                ("LibrarySetupWindow", () => new Views.LibrarySetupWindow()),
                ("AccountWindow", () => new Views.AccountWindow()),
                ("CaptionStyleWindow", () => new Views.CaptionStyleWindow()),
                ("FriendsWindow", () => new Views.FriendsWindow()),
                ("UploadsWindow", () => new Views.UploadsWindow()),
                ("ManageUsersWindow", () => new Views.ManageUsersWindow()),
                ("AddShortcutWindow", () => new Views.AddShortcutWindow()),
                ("InviteWindow", () => new Views.InviteWindow()),
                ("NameCollectionWindow", () => new Views.NameCollectionWindow()),
                ("StreamingBrowserWindow", () => new Views.StreamingBrowserWindow("about:blank")),
            })
            {
                try
                {
                    var window = make();
                    window.Close();
                    Console.WriteLine($"ok    {name}");
                }
                catch (Exception ex)
                {
                    failures++;
                    Console.WriteLine($"FAIL  {name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
            Shutdown(failures);
            return;
        }

        // Headless: draw the main screens and every dialog to PNGs, then exit.
        //   --snapshot-ui <folder> [path to PlayerView.xaml]
        var snap = Array.IndexOf(e.Args, "--snapshot-ui");
        if (snap >= 0 && snap + 1 < e.Args.Length)
        {
            _headless = true;
            Shutdown(UiSnapshot.Run(e.Args[snap + 1], snap + 2 < e.Args.Length ? e.Args[snap + 2] : null));
            return;
        }

        // A real window, for the few things --snapshot-ui cannot draw: the
        // acrylic behind a dialog and the title bar are the compositor's, not
        // WPF's. Opens without activating, closes itself, and is meant to be
        // launched through run-quiet onto a second monitor.
        //   --preview-window <home|library> [dialog] [seconds]
        var pw = Array.IndexOf(e.Args, "--preview-window");
        if (pw >= 0 && pw + 1 < e.Args.Length)
        {
            _headless = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var dialog = pw + 2 < e.Args.Length && !int.TryParse(e.Args[pw + 2], out _) ? e.Args[pw + 2] : null;
            var seconds = int.TryParse(e.Args[^1], out var sec) ? sec : 12;

            var main = new MainWindow { ShowActivated = false };
            if (e.Args[pw + 1] == "library") main.Navigate(new Views.LibraryView());
            main.Show();

            // After the launcher has had time to park the main window, so the
            // dialog opens centred over it rather than where it started.
            var later = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            later.Tick += (_, _) =>
            {
                later.Stop();
                Window? box = dialog switch
                {
                    "settings" => new Views.SettingsWindow(),
                    "account" => new Views.AccountWindow(),
                    "library-setup" => new Views.LibrarySetupWindow(),
                    _ => null,
                };
                if (box is null) return;
                box.Owner = main;
                box.ShowActivated = false;
                box.Show();
            };
            later.Start();

            var done = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            done.Tick += (_, _) => { done.Stop(); Shutdown(0); };
            done.Start();
            return;
        }

        // Headless: build the libVLC plugin cache into the bundled plugins
        // folder, then exit. Run once at pack time so shipped copies start fast.
        if (Array.IndexOf(e.Args, "--gen-plugin-cache") >= 0)
        {
            try { Services.AppServices.GeneratePluginCache(); Shutdown(0); }
            catch { Shutdown(1); }
            return;
        }

        // Headless cast-device scan: --list-renderers [out.json] [seconds]
        var r = Array.IndexOf(e.Args, "--list-renderers");
        if (r >= 0)
        {
            var outPath = r + 1 < e.Args.Length
                ? e.Args[r + 1]
                : Path.Combine(AppContext.BaseDirectory, "renderers.json");
            var seconds = r + 2 < e.Args.Length && int.TryParse(e.Args[r + 2], out var s) ? s : 8;
            Shutdown(RendererProbe.Run(outPath, seconds));
            return;
        }

        Services.StartupTrace.Mark("OnStartup entry");

        // libVLC's first initialisation reads hundreds of native plugin files;
        // on a cold disk that is ~12 seconds. Doing it here on the UI thread used
        // to block the window from appearing at all for that whole time. Warm it
        // on a background thread instead so the app opens immediately — the Lazy
        // is thread-safe, so this warm-up (or whoever opens the first film) builds
        // the engine exactly once. Media/MediaPlayer objects still live on the UI
        // thread, which is the affinity that actually matters.
        System.Threading.Tasks.Task.Run(() =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try { _ = Services.AppServices.LibVlc; } catch { /* surfaced when first used */ }
            Services.StartupTrace.Note($"libVLC ready in background ({sw.ElapsedMilliseconds} ms after start)");
        });
        Services.StartupTrace.Mark("libVLC warm-up scheduled");

        // Offer ourselves under "Open with" and claim vaultmovies:// links.
        // Re-run every launch because a Velopack update changes the executable path.
        Services.FileAssociations.Register();
        Services.FileAssociations.RegisterProtocol();
        Services.StartupTrace.Mark("FileAssociations.Register");

        // Launched with a film (Open with, drag onto the exe, or a shell
        // association)? Go straight to it rather than the home screen.
        var opened = Array.Find(e.Args, Services.FileAssociations.IsPlayableFile);

        // Launched from an invite link (LAN Party chat, Discord, browser)?
        var invite = Array.Find(e.Args,
            a => a.StartsWith("vaultmovies:", StringComparison.OrdinalIgnoreCase));

        _ = StartAsync(opened, invite);
    }

    /// <summary>
    /// Open an invite link. When it carries a LAN Party assertion, redeem it
    /// first so the party is joined as that person rather than as a nickname.
    /// A failed sign-in is never fatal — the room still opens as a guest, which
    /// is exactly what happens for anyone who isn't a LAN Party user.
    /// </summary>
    private static async System.Threading.Tasks.Task OpenInviteAsync(string server, string code, string? sso)
    {
        if (sso is not null)
        {
            var outcome = await Services.AppServices.Account.SignInWithLanPartyAsync(sso);
            if (outcome.Status != Services.AccountService.AuthStatus.Success)
                Services.StartupTrace.Note($"LAN Party sign-in skipped: {outcome.Message}");
        }
        VideoPlayer.App.MainWindow.Instance.Navigate(new Views.PartyView(server, code));
    }

    /// <summary>Play a file handed to us by the shell, with its folder as the playlist.</summary>
    private static void OpenFile(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(path);
            var playlist = folder is not null && Directory.Exists(folder)
                ? Services.MovieLibrary.Scan(folder)
                : new System.Collections.Generic.List<Models.MovieItem>();

            // Prefer the scanned entry so Next/Previous walk the folder.
            var movie = playlist.Find(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase))
                        ?? new Models.MovieItem
                        {
                            Name = Path.GetFileNameWithoutExtension(path),
                            Path = path,
                            SizeBytes = new FileInfo(path).Length,
                        };

            // Qualified: inside an Application subclass, "MainWindow" alone
            // binds to WPF's own property rather than our shell window.
            VideoPlayer.App.MainWindow.Instance.Navigate(new Views.PlayerView(movie, playlist));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't open that file.\n\n{ex.Message}",
                "Vault Movies", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Show the loading screen, let it run the update check, then open the app.
    /// If the user accepts an update the process restarts and never gets here.
    /// </summary>
    private static async System.Threading.Tasks.Task StartAsync(string? fileToOpen, string? inviteUri = null)
    {
        var hasInvite = inviteUri is not null
            && Services.InviteLink.TryParse(inviteUri, out _, out _, out var assertion)
            && assertion is not null;

        var splash = new Views.SplashWindow();
        splash.Show();
        Services.StartupTrace.Mark("splash shown");
        try
        {
            // A sign-in assertion expires in minutes, and an update prompt can
            // sit here indefinitely waiting to be answered. Redeem the link
            // first; the update is offered on the next ordinary launch.
            if (!hasInvite) await splash.RunAsync();
            Services.StartupTrace.Mark("update check done");
        }
        finally
        {
            new MainWindow().Show();
            Services.StartupTrace.Mark("MainWindow shown");
            Services.StartupTrace.Flush();
            splash.Close();
            if (fileToOpen is not null) OpenFile(fileToOpen);
            else if (inviteUri is not null
                     && Services.InviteLink.TryParse(inviteUri, out var server, out var code, out var sso))
                _ = OpenInviteAsync(server, code, sso);
            // Pull any library changes made on another machine. Fire-and-forget:
            // signed-out or offline just no-ops, and the app is already usable.
            _ = Services.AppServices.Account.SyncAsync();

            // Bring up a library hosted on this PC, if that is what was chosen.
            // Fire-and-forget as well: it takes a couple of seconds and nothing
            // on screen is waiting for it. No-ops for everybody else.
            _ = Services.AppServices.Library.StartFromSettingsAsync();

            if (fileToOpen is null && inviteUri is null) OfferSetup();
        }
    }

    /// <summary>
    /// On a genuinely fresh install, ask where the films are before the user has
    /// to go looking for the setting.
    ///
    /// Deliberately narrow. Anyone with a server address, an account, or a
    /// folder they have opened before already has an answer to this question,
    /// and being asked it again by an update would be a worse first impression
    /// than never being asked at all. Those people find it under Settings.
    /// </summary>
    private static void OfferSetup()
    {
        var settings = Services.AppServices.Settings;
        if (settings.SetupDone) return;
        if (settings.LibraryMode is not null) return;
        if (!string.IsNullOrWhiteSpace(settings.AuthServer)) return;
        if (settings.RefreshToken is not null) return;
        if (!string.IsNullOrWhiteSpace(settings.LastFolder)) return;

        try
        {
            new Views.LibrarySetupWindow { Owner = VideoPlayer.App.MainWindow.Instance }.ShowDialog();
        }
        catch (Exception ex)
        {
            // Never let the welcome get in the way of the app opening.
            LogCrash("setup", ex);
        }
    }

    /// <summary>
    /// Take the hosted library down with the app.
    ///
    /// It is a separate process, so without this it would outlive the window
    /// that started it, keep the port, and keep serving from a folder the user
    /// may since have unchosen — with no visible sign that anything is running.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { Services.AppServices.Library.Stop(); } catch { }
        base.OnExit(e);
    }
}
