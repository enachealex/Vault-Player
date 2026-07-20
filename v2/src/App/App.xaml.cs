using System;
using System.IO;
using System.Windows;
using Velopack;

namespace VideoPlayer.App;

public partial class App : Application
{
    public App()
    {
        // Must run before anything else touches the app: this handles the
        // install/update/uninstall hooks and can exit the process outright.
        VelopackApp.Build().Run();

        // A film stopping dead with no explanation is the worst outcome, so
        // record what happened and keep going where we safely can.
        //
        // NOTE: this catches managed exceptions only. Access violations inside
        // libvlc.dll terminate the process outright and never reach here — for
        // those, a crash dump is the only evidence (see docs/crashes.md).
        DispatcherUnhandledException += (_, e) =>
        {
            LogCrash("UI thread", e.Exception);
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

        // Initialize the libVLC engine deterministically on the UI thread before
        // any background probing can race its lazy construction.
        _ = Services.AppServices.LibVlc;

        // Offer ourselves under "Open with". Re-run every launch because a
        // Velopack update changes the executable path.
        Services.FileAssociations.Register();

        // Launched with a film (Open with, drag onto the exe, or a shell
        // association)? Go straight to it rather than the home screen.
        var opened = Array.Find(e.Args, Services.FileAssociations.IsPlayableFile);

        _ = StartAsync(opened);
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
    private static async System.Threading.Tasks.Task StartAsync(string? fileToOpen)
    {
        var splash = new Views.SplashWindow();
        splash.Show();
        try
        {
            await splash.RunAsync();
        }
        finally
        {
            new MainWindow().Show();
            splash.Close();
            if (fileToOpen is not null) OpenFile(fileToOpen);
        }
    }
}
