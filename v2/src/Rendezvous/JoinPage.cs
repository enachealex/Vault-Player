namespace VideoPlayer.Rendezvous;

/// <summary>
/// The page behind an invite link. On a phone, tablet or TV browser it IS the
/// player: it follows the host's clock and plays the relayed film, so joining
/// movie night needs nothing installed. Where the film can't play in a browser
/// (see WebPlayback on the host) it explains why and points at the desktop app.
///
/// Deliberately one self-contained file with no external requests — it is often
/// opened on a phone over mobile data, and every extra round trip is a delay
/// before the film appears.
/// </summary>
public static class JoinPage
{
    public static string Render(string code, string server, string downloadUrl) => Template
        .Replace("__CODE__", code)
        .Replace("__SERVER__", server)
        .Replace("__DOWNLOAD__", downloadUrl);

    // __CODE__ is validated against ^MOVIE-[A-Z0-9]{4}$ before it reaches here
    // and __SERVER__ is URL-escaped by the caller, so neither can break out.
    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#1A0D09">
<title>Movie night - Vault Movies</title>
<style>
  :root {
    --bg: #1A0D09; --deep: #0E0604; --line: #43281F;
    --text: #F6ECE5; --muted: #D9C0B3; --accent: #C8442A; --warm: #F0A070;
  }
  * { box-sizing: border-box; }
  html, body { height: 100%; }
  body {
    margin: 0; background: var(--bg); color: var(--text);
    font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
    display: flex; flex-direction: column;
    padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left);
    -webkit-font-smoothing: antialiased;
  }
  .card { margin: auto; padding: 34px 26px; max-width: 420px; text-align: center; }
  .dot { width: 26px; height: 26px; border-radius: 8px; background: var(--accent); margin: 0 auto 20px; }
  h1 { font-size: 21px; margin: 0 0 8px; font-weight: 700; }
  .code {
    font-family: Consolas, ui-monospace, monospace; font-size: 30px; font-weight: 800;
    letter-spacing: 2px; color: var(--accent); margin: 14px 0 6px;
  }
  p { color: var(--muted); font-size: 14.5px; line-height: 1.55; margin: 8px 0; }
  .btn {
    display: inline-block; margin-top: 18px; padding: 14px 30px; border-radius: 12px;
    background: var(--accent); color: #fff; text-decoration: none; font-weight: 700;
    font-size: 16px; border: none; cursor: pointer; font-family: inherit;
  }
  .alt { display: block; margin-top: 16px; color: var(--warm); font-size: 13px; }

  #stage { display: none; flex: 1; min-height: 0; flex-direction: column; background: #000; }
  video { width: 100%; height: 100%; flex: 1; min-height: 0; background: #000; object-fit: contain; }
  #bar {
    display: flex; align-items: center; gap: 12px; padding: 10px 14px;
    background: var(--deep); border-top: 1px solid var(--line); font-size: 13px;
  }
  #bar .t { font-weight: 700; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  #bar .s { color: var(--warm); white-space: nowrap; margin-left: auto; font-size: 12px; }
  #gate {
    position: fixed; inset: 0; display: flex; flex-direction: column; align-items: center;
    justify-content: center; background: rgba(14,6,4,.94); cursor: pointer;
    padding: 24px; text-align: center;
  }
  #gate .play {
    width: 78px; height: 78px; border-radius: 50%; background: var(--accent);
    display: grid; place-items: center; margin-bottom: 16px;
  }
  #gate .play::after {
    content: ''; border-left: 24px solid #fff; border-top: 15px solid transparent;
    border-bottom: 15px solid transparent; margin-left: 6px;
  }
  #gate h2 { margin: 0; font-size: 20px; }
  #gate .who { color: var(--warm); font-size: 14px; margin-top: 4px; }
  .hide { display: none !important; }
</style>
</head>
<body>

<div id="card" class="card">
  <div class="dot"></div>
  <h1 id="title">Movie night</h1>
  <div class="code">__CODE__</div>
  <p id="msg">Checking the room...</p>
  <div id="actions"></div>
</div>

<div id="stage">
  <video id="v" playsinline webkit-playsinline x-webkit-airplay="allow" preload="auto" controls></video>
  <div id="bar">
    <span class="t" id="btitle"></span>
    <span class="s" id="bsync"></span>
  </div>
</div>

<div id="gate" class="hide">
  <div class="play"></div>
  <h2 id="gtitle">Movie night</h2>
  <div class="who" id="gwho">Tap to watch with everyone</div>
</div>

<script>
(function () {
  var CODE = "__CODE__";
  var SERVER = "__SERVER__";
  var DOWNLOAD = "__DOWNLOAD__";
  var launch = "vaultmovies://join?code=" + encodeURIComponent(CODE) + "&server=" + encodeURIComponent(SERVER);

  var card = document.getElementById('card'), stage = document.getElementById('stage');
  var gate = document.getElementById('gate'), v = document.getElementById('v');
  var msg = document.getElementById('msg'), actions = document.getElementById('actions');
  var title = document.getElementById('title');
  var btitle = document.getElementById('btitle'), bsync = document.getElementById('bsync');
  var gtitle = document.getElementById('gtitle'), gwho = document.getElementById('gwho');

  var skew = 0;         // this device's clock minus the relay's
  var started = false;  // tapped once; browsers block audio before a gesture
  var mode = '';

  function appLinks(primary) {
    var open = primary ? '<a class="btn" href="' + launch + '">Open in Vault Movies</a>' : '';
    return open + '<a class="alt" href="' + DOWNLOAD + '">Need the app? Download for Windows</a>';
  }

  // Follow the host. The relay reports its own clock, so the difference between
  // this device's time and the host's cancels out instead of skewing playback.
  function sync(r) {
    if (!started || r.positionMs == null) return;
    var nowOnRelay = Date.now() - skew;
    var elapsed = (r.playing && r.atUnixMs) ? Math.max(0, nowOnRelay - r.atUnixMs) : 0;
    var target = (r.positionMs + elapsed) / 1000;
    if (isFinite(target)) {
      var drift = v.currentTime - target;
      // A big gap is a seek or a stall: jump. A small one is buffering lag, and
      // jumping for that stutters constantly - so nudge the rate instead and let
      // it close on its own, which is what the desktop client does.
      if (Math.abs(drift) > 2) { v.currentTime = target; v.playbackRate = 1; }
      else if (Math.abs(drift) > 0.3) { v.playbackRate = drift < 0 ? 1.05 : 0.95; }
      else if (v.playbackRate !== 1) { v.playbackRate = 1; }
    }
    if (r.playing && v.paused) v.play().catch(function () {});
    if (!r.playing && !v.paused) { v.pause(); v.playbackRate = 1; }
    bsync.textContent = r.playing ? 'In sync with ' + (r.host || 'the host') : 'Paused by the host';
  }

  function toPlayer(r) {
    if (mode !== 'play') {
      mode = 'play';
      card.style.display = 'none';
      stage.style.display = 'flex';
      v.src = '/stream/' + encodeURIComponent(CODE);
      gate.classList.remove('hide');
      gtitle.textContent = r.title || 'Movie night';
      gwho.textContent = r.host ? 'Tap to watch with ' + r.host : 'Tap to watch with everyone';
    }
    btitle.textContent = r.title || 'Movie night';
    sync(r);
  }

  function toCard(heading, text, html) {
    mode = 'card';
    stage.style.display = 'none';
    card.style.display = 'block';
    title.textContent = heading;
    msg.textContent = text;
    actions.innerHTML = html;
  }

  gate.addEventListener('click', function () {
    started = true;
    gate.classList.add('hide');
    v.play().catch(function () {});
    poll();
  });

  function poll() {
    fetch('/room/' + encodeURIComponent(CODE), { cache: 'no-store' })
      .then(function (res) {
        if (res.status === 404) {
          toCard('That room has ended', 'Ask for a fresh link when they start again.', appLinks(false));
          return null;
        }
        if (!res.ok) throw new Error('bad');
        return res.json();
      })
      .then(function (r) {
        if (!r) return;
        if (r.serverNowMs) skew = Date.now() - r.serverNowMs;
        if (r.service) {
          toCard(r.title || 'Movie night',
            'Everyone is watching this on ' + r.service + ' in their own app.', appLinks(true));
        } else if (r.webPlayable) {
          toPlayer(r);
        } else {
          toCard(r.title || 'Movie night',
            (r.webReason || 'This film needs the Vault Movies app.') +
            ' Open it there and it plays at full quality.', appLinks(true));
        }
      })
      .catch(function () {
        if (mode !== 'play') toCard('Cannot reach the room', 'Check your connection and try again.', appLinks(true));
      });
  }

  poll();
  setInterval(poll, 3000);
})();
</script>
</body>
</html>
""";
}
