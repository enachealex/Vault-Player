using System;
using System.Threading;
using LibVLCSharp.Shared;

namespace VideoPlayer.App;

/// <summary>
/// Headless proof of what a finished film does, and of the only thing that
/// brings it back.
///
/// A film that reached its end stopped responding to everything: the play
/// button, the spacebar, a click on the picture and the whole seek bar. The
/// controls looked completely alive, which is what made it baffling rather
/// than merely annoying. The cause is that libVLC treats Ended as terminal
/// and ignores <c>Play()</c> on a player in that state — no exception, no
/// return value, nothing.
///
/// That is a claim about somebody else's state machine, so it is checked here
/// rather than believed: play a clip to its end, confirm Play() really is
/// ignored, then confirm a fresh Media really does revive it. If libVLC ever
/// changes its mind, this says so instead of the workaround quietly becoming
/// superstition.
/// </summary>
public static class ReplayTest
{
    public static int Run(string file, string[]? vlcArgs = null)
    {
        var failures = 0;
        void Check(string what, bool ok)
        {
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")}  {what}");
            Console.Out.Flush();
            if (!ok) failures++;
        }
        void Step(string what) { Console.WriteLine($"  .. {what}"); Console.Out.Flush(); }

        Core.Initialize();
        // No window and no sound: decoding still runs, nothing pops up and
        // nothing interrupts whatever the machine's owner is doing.
        // --no-video rather than --vout=dummy. The dummy video output segfaults
        // during its own teardown a second or so after EndReached, with nothing
        // touching it — reproducible, and nothing to do with the app, which
        // uses a real output into a VideoView. It cost an afternoon once.
        vlcArgs ??= new[] { "--aout=dummy", "--no-video", "--no-video-title-show" };
        Console.WriteLine($"libvlc args: {string.Join(" ", vlcArgs)}");
        using var libvlc = new LibVLC(vlcArgs);
        using var player = new MediaPlayer(libvlc);

        var ended = new ManualResetEventSlim(false);
        player.EndReached += (_, _) => ended.Set();

        using (var media = new Media(libvlc, file, FromType.FromPath))
            player.Play(media);

        Check("playback starts", WaitFor(() => player.State == VLCState.Playing && player.Time > 0, 15000));

        // EndReached fires on a libVLC thread, and libVLC forbids calling back
        // into it from there — the app dispatches, and so does this.
        Check("playback reaches the end", ended.Wait(60000));

        Step("sleeping 1500ms to let libVLC settle after EndReached");
        Thread.Sleep(1500);

        Step("reading State");
        var endState = player.State;
        Step($"State read back as {endState}");
        Check($"state is terminal after the end (was {endState})",
              endState is VLCState.Ended or VLCState.Stopped);

        Step("reading IsPlaying");
        var endPlaying = player.IsPlaying;
        Step($"IsPlaying read back as {endPlaying}");

        Step("calling Play() on the finished player");

        // The bug, stated as a test: this is the call every control used to
        // make, and it does nothing whatsoever.
        player.Play();
        Step("Play() returned");
        Thread.Sleep(1500);
        Check($"Play() alone does NOT resume a finished film (state {player.State}, playing {player.IsPlaying})",
              !player.IsPlaying);

        // It keeps reporting a length, which is worth knowing: the dead seek
        // bar was never a missing duration, it was Position and Time being
        // ignored like everything else in this state.
        Check($"a finished player still reports its length (was {player.Length})", player.Length > 0);
        player.Time = 0;
        Thread.Sleep(500);
        Check("setting Time does NOT revive it either", !player.IsPlaying);

        // The fix, in exactly the shape StartPlayback uses it: Stop first, then
        // a fresh Media. Stopping an already-finished player is the one new
        // call the fix introduces, so it is the one most worth proving safe.
        Step("Stop() on the finished player");
        player.Stop();
        Thread.Sleep(300);
        using (var media = new Media(libvlc, file, FromType.FromPath))
            player.Play(media);
        Check("Stop() + a fresh Media DOES restart it",
              WaitFor(() => player.State == VLCState.Playing && player.Time > 0, 15000));

        player.Stop();
        Console.WriteLine(failures == 0 ? "\nall checks passed" : $"\n{failures} check(s) failed");
        return failures;
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(100);
        }
        return false;
    }
}
