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

        new MainWindow().Show();
    }
}
