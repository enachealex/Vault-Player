namespace VideoPlayer.Rendezvous;

/// <summary>
/// The library as a streaming-service home screen, served at GET /watch for
/// phones, tablets and computers — anywhere a person has a browser and not the
/// desktop app.
///
/// A sibling of the television page, not a variant of it. The TV page is built
/// for a remote and a 2018 browser; this one is built for a mouse or a thumb
/// and a current browser, so it uses grid, backdrop blur and modern script
/// freely. What they share is the API contract in docs/TV-APP-INTEGRATION.md:
/// the same catalogue, the same signed stream URLs, the same resume sync, so a
/// film paused on the television carries on here and the other way round.
///
/// Same-origin for the same reason as /tv: nothing under /api/v1 sends CORS
/// headers, so this page only works when this server serves it.
///
/// Playback, in order of preference:
///   1. webPlayable films open directly — a plain file, nothing to prepare.
///   2. Everything else goes through the server's HLS repackaging. Safari and
///      iOS play that natively; Chrome, Edge and Firefox get hls.js, loaded
///      from cdnjs only when a film actually needs it.
///   3. A film this browser cannot decode at all says so and points at the app.
/// </summary>
public static class WatchPage
{
    public static string Render() => Template;

    /// <summary>
    /// The web app manifest. With it, Chrome on Android and desktop offers
    /// "Install app"; an iPhone's "Add to Home Screen" uses the apple-touch-icon
    /// and apple-mobile-web-app metas in the page instead, which is why both exist.
    /// Scope is the whole site so the /link and /join pages a person reaches
    /// from inside the app stay inside its window.
    /// </summary>
    public const string Manifest = """
{
  "name": "Vault Movies",
  "short_name": "Vault",
  "description": "Your film library, anywhere.",
  "id": "/watch",
  "start_url": "/watch",
  "scope": "/",
  "display": "standalone",
  "orientation": "any",
  "background_color": "#0B0D12",
  "theme_color": "#0B0D12",
  "icons": [
    { "src": "/watch/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any" },
    { "src": "/watch/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any" },
    { "src": "/watch/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
""";

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0B0D12">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="Vault">
<link rel="manifest" href="/watch/manifest.webmanifest">
<link rel="apple-touch-icon" href="/watch/apple-touch-icon.png">
<link rel="icon" type="image/png" href="/watch/favicon-64.png">
<title>Vault Movies</title>
<style>
  /* The same smoked glass over film stills as the desktop app and the TV page.
     Buttons are rounded rectangles, never pills. #005ED9 is focus and nothing
     else; chosen things and progress are white. */
  :root {
    --bg:#0B0D12; --deep:#06070A;
    --glass:rgba(255,255,255,.08); --glass-hi:rgba(255,255,255,.16);
    --line:rgba(255,255,255,.12); --line-hi:rgba(255,255,255,.28);
    --smoke:rgba(16,18,24,.66); --smoke-hi:rgba(28,31,40,.86);
    --text:#F4F6FA; --muted:#A6AFBD; --faint:#6F7887;
    --warm:#F5C77E; --danger:#FF7A7A;
    --focus:#005ED9;
    --radius:12px; --card-radius:10px;
    --gutter:clamp(16px, 4vw, 56px);
    --card-w:clamp(168px, 22vw, 300px);
  }
  * { box-sizing:border-box; }
  html { -webkit-text-size-adjust:100%; }
  html, body { margin:0; background:var(--bg); color:var(--text); }
  body {
    font-family:'Segoe UI', Roboto, system-ui, -apple-system, sans-serif;
    -webkit-font-smoothing:antialiased; min-height:100vh; overflow-x:hidden;
  }
  body.locked { overflow:hidden; }
  button, input { font:inherit; color:inherit; }
  button { cursor:pointer; }
  :focus { outline:none; }
  :focus-visible { outline:3px solid var(--focus); outline-offset:3px; }
  [hidden] { display:none !important; }

  /* ---- shared pieces ---- */
  .btn { display:inline-flex; align-items:center; justify-content:center; gap:10px;
         border:1px solid var(--text); background:var(--text); color:var(--bg);
         border-radius:var(--radius); padding:12px 22px; font-weight:700; font-size:16px;
         line-height:1; white-space:nowrap; }
  .btn:hover { background:#fff; }
  .btn.ghost { background:var(--glass); border-color:var(--line); color:var(--text);
               -webkit-backdrop-filter:blur(14px); backdrop-filter:blur(14px); }
  .btn.ghost:hover { background:var(--glass-hi); border-color:var(--line-hi); }
  .btn[disabled] { opacity:.45; cursor:default; }
  .btn .tri { width:0; height:0; border-left:12px solid currentColor;
              border-top:8px solid transparent; border-bottom:8px solid transparent; }
  .logo { width:30px; height:30px; border-radius:8px; flex:none;
          background:linear-gradient(135deg, #E35A3C, #B9381F);
          display:inline-flex; align-items:center; justify-content:center; }
  .logo::after { content:''; border-left:9px solid #fff; border-top:6px solid transparent;
                 border-bottom:6px solid transparent; margin-left:3px; }
  .err { color:var(--danger); min-height:1.4em; font-size:14px; margin-top:12px; }
  .muted { color:var(--muted); }

  /* ---- sign-in and other centred screens ---- */
  .screen-center { min-height:100vh; display:flex; align-items:center; justify-content:center;
                   padding:24px var(--gutter);
                   background:
                     radial-gradient(ellipse 62% 78% at 78% 28%, rgba(122,60,36,.55), rgba(11,13,18,0) 78%),
                     radial-gradient(ellipse 70% 80% at 12% 94%, rgba(26,58,110,.55), rgba(11,13,18,0) 82%),
                     var(--bg); }
  .panel { width:100%; max-width:400px; padding:32px 28px; border-radius:20px;
           background:var(--smoke); border:1px solid var(--line);
           -webkit-backdrop-filter:blur(24px); backdrop-filter:blur(24px); }
  .panel h1 { font-size:24px; margin:18px 0 6px; }
  .panel p { margin:0 0 20px; color:var(--muted); line-height:1.5; font-size:15px; }
  .field { display:block; margin:0 0 14px; }
  .field span { display:block; font-size:13px; color:var(--muted); margin:0 0 6px; }
  .field input { width:100%; padding:13px 14px; border-radius:var(--radius); font-size:16px;
                 background:rgba(0,0,0,.35); border:1px solid var(--line); }
  .field input:focus { border-color:var(--focus); box-shadow:0 0 0 1px var(--focus); }
  .panel .btn { width:100%; margin-top:6px; }
  .panel .row { display:flex; gap:10px; margin-top:6px; }
  .panel .row .btn { margin:0; }

  /* ---- top bar ---- */
  #top { position:fixed; z-index:20; left:0; right:0; top:0;
         display:flex; align-items:center; gap:18px;
         padding:calc(12px + env(safe-area-inset-top)) var(--gutter) 12px;
         background:linear-gradient(rgba(6,7,10,.85), rgba(6,7,10,0));
         transition:background .25s; }
  #top.solid { background:rgba(11,13,18,.78); border-bottom:1px solid var(--line);
               -webkit-backdrop-filter:blur(18px); backdrop-filter:blur(18px); }
  #top .brand { display:flex; align-items:center; gap:10px; font-weight:800; font-size:19px;
                letter-spacing:.2px; background:none; border:none; padding:4px; border-radius:8px; }
  #top .spacer { flex:1; }
  .search { position:relative; width:min(320px, 42vw); }
  .search input { width:100%; padding:10px 12px 10px 36px; border-radius:var(--radius);
                  background:rgba(0,0,0,.4); border:1px solid var(--line); font-size:15px; }
  .search input:focus { border-color:var(--focus); }
  .search svg { position:absolute; left:11px; top:50%; transform:translateY(-50%); opacity:.7; }
  .who { position:relative; }
  .who > button { display:flex; align-items:center; gap:8px; padding:6px 8px; border-radius:var(--radius);
                  background:var(--glass); border:1px solid var(--line); }
  .avatar { width:28px; height:28px; border-radius:7px; background:#2A3242; display:inline-flex;
            align-items:center; justify-content:center; font-weight:800; font-size:14px; }
  .who .name { max-width:120px; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; font-size:14px; }
  .menu { position:absolute; right:0; top:calc(100% + 8px); min-width:200px; padding:6px;
          background:var(--smoke-hi); border:1px solid var(--line); border-radius:var(--radius);
          -webkit-backdrop-filter:blur(20px); backdrop-filter:blur(20px);
          box-shadow:0 18px 40px rgba(0,0,0,.5); }
  .menu .lbl { padding:8px 10px 6px; font-size:12px; color:var(--faint); }
  .menu button { display:block; width:100%; text-align:left; padding:10px; border:none;
                 background:none; border-radius:8px; font-size:15px; }
  .menu button:hover { background:var(--glass-hi); }

  /* ---- hero ---- */
  #hero { position:relative; min-height:min(78vh, 760px); display:flex; align-items:flex-end;
          padding:120px var(--gutter) clamp(36px, 7vh, 80px); overflow:hidden; }
  #hero .bg { position:absolute; inset:0; background-size:cover; background-position:center 30%;
              opacity:0; transition:opacity .8s; }
  #hero .bg.on { opacity:1; }
  #hero::after { content:''; position:absolute; inset:0; pointer-events:none;
                 background:
                   linear-gradient(90deg, rgba(11,13,18,.92) 0%, rgba(11,13,18,.55) 42%, rgba(11,13,18,0) 75%),
                   linear-gradient(180deg, rgba(11,13,18,0) 55%, var(--bg) 100%); }
  #hero .inner { position:relative; z-index:1; max-width:620px; }
  #hero .kicker { font-size:13px; letter-spacing:1.6px; text-transform:uppercase; color:var(--warm);
                  font-weight:700; margin-bottom:10px; }
  #hero h1 { font-size:clamp(32px, 6vw, 64px); line-height:1.02; margin:0 0 14px; font-weight:800;
             text-shadow:0 2px 24px rgba(0,0,0,.5); }
  #hero .meta { color:var(--muted); font-size:15px; margin-bottom:18px; }
  #hero .bar { height:4px; width:min(320px, 70%); background:rgba(255,255,255,.22); border-radius:2px;
               margin:-6px 0 20px; overflow:hidden; }
  #hero .bar i { display:block; height:100%; background:#fff; }
  #hero .actions { display:flex; flex-wrap:wrap; gap:12px; }

  /* ---- rows ---- */
  main { position:relative; z-index:1; padding-bottom:64px; }
  .shelf { margin:0 0 34px; }
  .shelf h2 { font-size:clamp(17px, 1.8vw, 21px); margin:0 0 12px; padding:0 var(--gutter); font-weight:700; }
  .shelf h2 .count { color:var(--faint); font-weight:600; font-size:.8em; margin-left:8px; }
  .lane { position:relative; }
  .track { display:grid; grid-auto-flow:column; grid-auto-columns:var(--card-w); gap:12px;
           overflow-x:auto; overscroll-behavior-x:contain; scroll-snap-type:x mandatory;
           scroll-padding:0 var(--gutter); padding:6px var(--gutter) 10px; scrollbar-width:none; }
  .track::-webkit-scrollbar { display:none; }
  .track > * { scroll-snap-align:start; }
  .nav { position:absolute; top:6px; bottom:44px; width:calc(var(--gutter) - 6px); min-width:36px;
         border:none; background:rgba(11,13,18,.6); color:#fff; font-size:30px; z-index:2;
         opacity:0; transition:opacity .2s; display:flex; align-items:center; justify-content:center; }
  .nav.prev { left:0; border-radius:0 var(--radius) var(--radius) 0; }
  .nav.next { right:0; border-radius:var(--radius) 0 0 var(--radius); }
  .lane:hover .nav:not([disabled]), .nav:focus-visible { opacity:1; }
  .nav[disabled] { pointer-events:none; }
  @media (hover:none) { .nav { display:none; } }

  /* Fixed-width columns, so a small library's cards stay the size of the
     cards in the rows above instead of stretching across the page. */
  .grid { display:grid; grid-template-columns:repeat(auto-fill, var(--card-w));
          gap:22px 12px; padding:6px var(--gutter); }

  /* ---- a film card ---- */
  .card { display:block; width:100%; padding:0; border:none; background:none; text-align:left; }
  .thumb { position:relative; aspect-ratio:16/9; border-radius:var(--card-radius); overflow:hidden;
           background:linear-gradient(135deg, #1B2130, #12151D); border:1px solid var(--line);
           transition:transform .2s, border-color .2s, box-shadow .2s; }
  .card:hover .thumb { transform:translateY(-3px) scale(1.03); border-color:var(--line-hi);
                       box-shadow:0 14px 30px rgba(0,0,0,.55); }
  .card:focus-visible { outline:none; }
  .card:focus-visible .thumb { outline:3px solid var(--focus); outline-offset:2px; }
  .thumb img { position:absolute; inset:0; width:100%; height:100%; object-fit:cover;
               opacity:0; transition:opacity .4s; }
  .thumb img.on { opacity:1; }
  .thumb .ph { position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
               padding:10px; text-align:center; font-weight:800; font-size:15px; color:var(--muted); }
  .thumb .play { position:absolute; inset:0; display:flex; align-items:center; justify-content:center;
                 background:rgba(0,0,0,.35); opacity:0; transition:opacity .2s; }
  .thumb .play::after { content:''; width:0; height:0; border-left:22px solid #fff;
                        border-top:14px solid transparent; border-bottom:14px solid transparent; margin-left:6px;
                        filter:drop-shadow(0 2px 8px rgba(0,0,0,.6)); }
  .card:hover .thumb .play { opacity:1; }
  .thumb .prog { position:absolute; left:0; right:0; bottom:0; height:4px; background:rgba(255,255,255,.25); }
  .thumb .prog i { display:block; height:100%; background:#fff; }
  .badge { position:absolute; top:8px; left:8px; padding:4px 7px; border-radius:6px; font-size:11px;
           font-weight:700; background:rgba(11,13,18,.82); border:1px solid var(--line); color:var(--warm); }
  .badge.new { color:#fff; border-color:rgba(255,255,255,.5); }
  .cap { padding:8px 2px 0; }
  .cap .t { display:block; font-size:14px; font-weight:600; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  .cap .m { display:block; font-size:12.5px; color:var(--faint); margin-top:2px; }

  .empty { padding:24px var(--gutter); color:var(--muted); line-height:1.6; }
  .notice { margin:0 var(--gutter) 24px; padding:12px 16px; border-radius:var(--radius);
            background:var(--glass); border:1px solid var(--line); color:var(--muted); font-size:14px; }
  #results h2 { padding-top:96px; }

  /* ---- film details ---- */
  #detail { position:fixed; inset:0; z-index:40; display:flex; align-items:flex-start; justify-content:center;
            overflow-y:auto; padding:5vh 16px; background:rgba(4,5,8,.72);
            -webkit-backdrop-filter:blur(6px); backdrop-filter:blur(6px); }
  #detail .sheet { position:relative; width:100%; max-width:860px; border-radius:16px; overflow:hidden;
                   background:var(--smoke-hi); border:1px solid var(--line); box-shadow:0 30px 80px rgba(0,0,0,.6); }
  #detail .art { position:relative; aspect-ratio:16/9; background:#12151D center/cover no-repeat; }
  #detail .art::after { content:''; position:absolute; inset:0;
                        background:linear-gradient(180deg, rgba(0,0,0,0) 45%, rgba(28,31,40,1) 100%); }
  #detail .close { position:absolute; top:12px; right:12px; z-index:2; width:40px; height:40px;
                   border-radius:10px; border:1px solid var(--line); background:rgba(11,13,18,.7);
                   font-size:22px; line-height:1; }
  #detail .body { position:relative; padding:0 clamp(18px, 4vw, 40px) 32px; margin-top:-70px; z-index:1; }
  #detail h2 { font-size:clamp(26px, 4vw, 40px); margin:0 0 10px; }
  #detail .meta { color:var(--muted); font-size:15px; display:flex; flex-wrap:wrap; gap:6px 16px; margin-bottom:16px; }
  #detail .bar { height:4px; max-width:360px; background:rgba(255,255,255,.22); border-radius:2px; overflow:hidden; margin-bottom:6px; }
  #detail .bar i { display:block; height:100%; background:#fff; }
  #detail .left { font-size:13px; color:var(--muted); margin-bottom:18px; }
  #detail .actions { display:flex; flex-wrap:wrap; gap:12px; margin-bottom:18px; }
  #detail .warn { color:var(--warm); font-size:14px; line-height:1.5; margin:0 0 16px; }
  #detail .facts { display:grid; grid-template-columns:auto 1fr; gap:6px 18px; font-size:14px; margin:8px 0 0; }
  #detail .facts dt { color:var(--faint); }
  #detail .facts dd { margin:0; color:var(--muted); }

  /* ---- install tip ---- */
  #installTip { position:fixed; inset:0; z-index:50; display:flex; align-items:center; justify-content:center;
                padding:16px; background:rgba(4,5,8,.72); }
  #installTip .steps { margin:0 0 16px; padding-left:22px; line-height:1.6; font-size:15px; }
  #installTip .steps li { margin-bottom:6px; }
  #installTip .share { vertical-align:-3px; margin:0 2px; }
  #installTip p { font-size:14px; }

  /* ---- player ---- */
  #player { position:fixed; inset:0; z-index:60; background:#000; }
  #player video { width:100%; height:100%; display:block; background:#000; }
  #ptop { position:absolute; left:0; right:0; top:0; display:flex; align-items:center; gap:14px;
          padding:calc(14px + env(safe-area-inset-top)) 18px 40px;
          background:linear-gradient(rgba(0,0,0,.75), rgba(0,0,0,0)); transition:opacity .3s; }
  #ptop.hide { opacity:0; pointer-events:none; }
  #ptop .t { font-weight:700; font-size:17px; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
  #prep { position:absolute; inset:0; display:flex; flex-direction:column; align-items:center;
          justify-content:center; text-align:center; padding:24px; background:#000; }
  #prep .pt { font-size:20px; font-weight:700; margin-bottom:10px; }
  #prep .pp { font-size:44px; font-weight:800; margin-bottom:10px; }
  #prep .ps { color:var(--muted); max-width:440px; line-height:1.5; }
  .spin { width:38px; height:38px; border-radius:50%; border:3px solid rgba(255,255,255,.2);
          border-top-color:#fff; animation:spin 1s linear infinite; margin-bottom:20px; }
  @keyframes spin { to { transform:rotate(360deg); } }

  @media (max-width:640px) {
    :root { --card-w:42vw; }
    #top { gap:10px; }
    #top .brand span { display:none; }
    .search { flex:1; width:auto; }
    .who .name { display:none; }
    #hero { min-height:64vh; padding-top:100px; }
    #hero .meta { font-size:14px; }
    .btn { padding:12px 16px; font-size:15px; }
    .cap .t { font-size:13px; }
  }
</style>
</head>
<body>

<!-- ---- sign in ---- -->
<section id="signin" class="screen-center" hidden>
  <form class="panel" id="signinForm" autocomplete="on">
    <span class="logo" aria-hidden="true"></span>
    <h1>Vault Movies</h1>
    <p>Sign in with your Vault account to watch your library.</p>
    <label class="field"><span>Username or email</span>
      <input id="u" name="username" autocomplete="username" autocapitalize="off" spellcheck="false" required></label>
    <label class="field"><span>Password</span>
      <input id="pw" name="password" type="password" autocomplete="current-password" required></label>
    <button class="btn" type="submit" id="signinBtn">Sign in</button>
    <div class="err" id="signinErr" role="alert"></div>
  </form>
</section>

<!-- ---- one-off messages: waiting for approval, server unreachable ---- -->
<section id="message" class="screen-center" hidden>
  <div class="panel">
    <span class="logo" aria-hidden="true"></span>
    <h1 id="msgTitle"></h1>
    <p id="msgText"></p>
    <div class="row" id="msgRow"></div>
  </div>
</section>

<!-- ---- home ---- -->
<div id="app" hidden>
  <header id="top">
    <button class="brand" id="brand" title="Home"><span class="logo" aria-hidden="true"></span><span>Vault</span></button>
    <div class="spacer"></div>
    <label class="search">
      <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"
           aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-3.5-3.5"/></svg>
      <input id="q" type="search" placeholder="Search your films" aria-label="Search your films" autocomplete="off">
    </label>
    <div class="who">
      <button id="whoBtn" aria-haspopup="true" aria-expanded="false">
        <span class="avatar" id="avatar"></span><span class="name" id="whoName"></span></button>
      <div class="menu" id="whoMenu" hidden>
        <div class="lbl" id="whoLbl"></div>
        <button id="installBtn" hidden>Install app</button>
        <button id="reloadBtn">Refresh library</button>
        <button id="signoutBtn">Sign out</button>
      </div>
    </div>
  </header>

  <section id="hero" hidden>
    <div class="bg" id="heroBg"></div>
    <div class="inner">
      <div class="kicker" id="heroKicker"></div>
      <h1 id="heroTitle"></h1>
      <div class="meta" id="heroMeta"></div>
      <div class="bar" id="heroBar" hidden><i></i></div>
      <div class="actions" id="heroActions"></div>
    </div>
  </section>

  <main>
    <div id="home"></div>
    <div id="results" hidden></div>
  </main>
</div>

<!-- ---- film details ---- -->
<div id="detail" hidden role="dialog" aria-modal="true" aria-labelledby="dTitle">
  <div class="sheet">
    <button class="close" id="dClose" aria-label="Close">&times;</button>
    <div class="art" id="dArt"></div>
    <div class="body">
      <h2 id="dTitle"></h2>
      <div class="meta" id="dMeta"></div>
      <div id="dProg"></div>
      <p class="warn" id="dWarn" hidden></p>
      <div class="actions" id="dActions"></div>
      <dl class="facts" id="dFacts"></dl>
    </div>
  </div>
</div>

<!-- ---- how to install on an iPhone (Safari has no install prompt a page can open) ---- -->
<div id="installTip" hidden role="dialog" aria-modal="true" aria-labelledby="tipTitle">
  <div class="panel">
    <span class="logo" aria-hidden="true"></span>
    <h1 id="tipTitle">Put Vault on your home screen</h1>
    <ol class="steps">
      <li>Tap the <b>Share</b> button <svg class="share" width="16" height="20" viewBox="0 0 16 20" fill="none" stroke="currentColor"
        stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
        <path d="M8 13V1.5M4 5l4-3.8L12 5M5 8H2.5v10.5h11V8H11"/></svg> in Safari&rsquo;s toolbar.</li>
      <li>Scroll down and tap <b>Add to Home Screen</b>.</li>
      <li>Tap <b>Add</b>. Vault opens full screen, like any other app.</li>
    </ol>
    <p class="muted">You&rsquo;ll sign in once more inside the app; after that it remembers you.</p>
    <button class="btn" id="tipClose">Got it</button>
  </div>
</div>

<!-- ---- player ---- -->
<section id="player" hidden>
  <video id="v" controls playsinline preload="auto"></video>
  <div id="prep" hidden>
    <div class="spin" id="prepSpin"></div>
    <div class="pt" id="prepTitle">Getting this film ready</div>
    <div class="pp" id="prepPct"></div>
    <div class="ps" id="prepSub">This happens once per film. Next time it starts straight away.</div>
  </div>
  <div id="ptop">
    <button class="btn ghost" id="pBack">&larr; Back</button>
    <span class="t" id="pTitle"></span>
  </div>
</section>

<script>
(() => {
  'use strict';

  const LS_RT = 'vault.watch.rt';
  // Pinned, and fetched only when a film needs it: Safari plays HLS itself,
  // and a film that is already a web file needs nothing at all.
  const HLS_JS = 'https://cdnjs.cloudflare.com/ajax/libs/hls.js/1.5.17/hls.min.js';

  const S = {
    access: null, refresh: null, expiresAt: 0, me: null,
    items: [], byFilmKey: new Map(), resume: {}, collections: [],
    film: null, hls: null, started: false, saveTimer: null, prepTimer: null,
    heroFilm: null,
  };
  const $ = id => document.getElementById(id);
  const v = $('v');

  // ---- small helpers ---------------------------------------------------

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function clock(ms) {
    const s = Math.max(0, Math.floor(ms / 1000));
    const h = Math.floor(s / 3600), m = Math.floor(s % 3600 / 60), sec = s % 60;
    const mm = String(m).padStart(h ? 2 : 1, '0'), ss = String(sec).padStart(2, '0');
    return h ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
  }
  function runtime(ms) {
    if (!ms) return '';
    const min = Math.round(ms / 60000);
    const h = Math.floor(min / 60), m = min % 60;
    return h ? (m ? `${h}h ${m}m` : `${h}h`) : `${m}m`;
  }
  function size(b) {
    if (!b) return '';
    return b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : Math.round(b / 1e6) + ' MB';
  }
  function dateOf(ms) {
    try { return new Date(ms).toLocaleDateString(undefined, { day:'numeric', month:'short', year:'numeric' }); }
    catch { return ''; }
  }
  function show(id) {
    for (const s of ['signin', 'message', 'app']) $(s).hidden = s !== id;
  }
  function message(title, text, buttons) {
    $('msgTitle').textContent = title;
    $('msgText').textContent = text;
    const row = $('msgRow'); row.innerHTML = '';
    for (const [label, fn, ghost] of buttons) {
      const b = el('button', ghost ? 'btn ghost' : 'btn', label);
      b.addEventListener('click', fn);
      row.appendChild(b);
    }
    show('message');
  }

  // ---- session -----------------------------------------------------------
  //
  // Refresh tokens are single-use, so two refreshes racing with the same token
  // end the session: the second is refused and reads as "signed out". Every
  // caller therefore shares one in-flight refresh. A second tab of this page
  // can still win the race; when that happens it has stored its new token, so
  // a refusal is retried once with whatever is in storage now.

  function readStored() { try { return localStorage.getItem(LS_RT); } catch { return null; } }
  function storeSession(j) {
    S.access = j.accessToken;
    S.refresh = j.refreshToken;
    S.expiresAt = j.expiresAt || 0;
    S.me = j;
    try { localStorage.setItem(LS_RT, j.refreshToken); } catch {}
  }

  let refreshing = null;
  // true = refreshed; false = the session is over; null = could not reach the server.
  function refresh() {
    if (refreshing) return refreshing;
    const attempt = async rt => {
      const r = await fetch('/api/v1/refresh', {
        method:'POST', headers:{ 'Content-Type':'application/json' },
        body: JSON.stringify({ refreshToken: rt })
      });
      if (r.ok) { storeSession(await r.json()); return true; }
      return r.status === 401 ? false : null;
    };
    refreshing = (async () => {
      try {
        const used = S.refresh;
        let ok = await attempt(used);
        if (ok === false) {
          const other = readStored();
          if (other && other !== used) ok = await attempt(other);
        }
        return ok;
      } catch { return null; }
    })().finally(() => { refreshing = null; });
    return refreshing;
  }

  async function api(path, opts = {}) {
    // A token about to lapse is renewed first, rather than sending a batch of
    // poster requests that all come back 401 together.
    if (S.refresh && S.expiresAt && Date.now() > S.expiresAt - 60000) await refresh();
    const go = () => {
      const o = { ...opts, headers: { ...(opts.headers || {}) } };
      if (S.access) o.headers['Authorization'] = 'Bearer ' + S.access;
      if (o.body) o.headers['Content-Type'] = 'application/json';
      return fetch(path, o);
    };
    let r = await go();
    if (r.status === 401 && S.refresh) {
      const ok = await refresh();
      if (ok === false) { signOut(true); throw new Error('signed-out'); }
      if (ok) r = await go();
    }
    return r;
  }

  function signOut(expired) {
    if (!$('player').hidden) stopPlayback(false);
    closeDetail();
    const rt = S.refresh;
    S.access = S.refresh = S.me = null; S.expiresAt = 0;
    try { localStorage.removeItem(LS_RT); } catch {}
    if (rt && !expired) {
      fetch('/api/v1/logout', { method:'POST', headers:{ 'Content-Type':'application/json' },
        body: JSON.stringify({ refreshToken: rt }) }).catch(() => {});
    }
    for (const u of artCache.values()) u.then(x => x && URL.revokeObjectURL(x));
    artCache.clear();
    $('pw').value = '';
    $('signinErr').textContent = expired ? 'Your session ended. Please sign in again.' : '';
    show('signin');
    setTimeout(() => $('u').focus(), 0);
  }

  $('signinForm').addEventListener('submit', async e => {
    e.preventDefault();
    const btn = $('signinBtn');
    btn.disabled = true; $('signinErr').textContent = '';
    try {
      const r = await fetch('/api/v1/login', {
        method:'POST', headers:{ 'Content-Type':'application/json' },
        body: JSON.stringify({ username: $('u').value.trim(), password: $('pw').value })
      });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { $('signinErr').textContent = j.error || 'Could not sign in.'; return; }
      storeSession(j);
      $('pw').value = '';
      enter();
    } catch {
      $('signinErr').textContent = 'Could not reach the server.';
    } finally { btn.disabled = false; }
  });

  // ---- library -----------------------------------------------------------

  async function enter() {
    renderWho();
    let r, cat;
    try {
      r = await api('/api/v1/catalog');
      cat = await r.json().catch(() => ({}));
    } catch (e) {
      if (e.message === 'signed-out') return;
      return message('Can’t reach your library', 'The server didn’t answer. Check your connection and try again.',
        [['Try again', enter], ['Sign out', () => signOut(false), true]]);
    }
    if (r.status === 403) {
      return message('Waiting for approval',
        cat.error || 'Your account is waiting for the owner’s approval. You’ll get in as soon as they say yes.',
        [['Check again', enter], ['Sign out', () => signOut(false), true]]);
    }
    if (!r.ok) {
      return message('Library unavailable', cat.error || 'The library is not available right now.',
        [['Try again', enter], ['Sign out', () => signOut(false), true]]);
    }

    S.items = cat.items || [];
    S.byFilmKey = new Map(S.items.map(it => [it.filmKey, it]));
    S.collections = (cat.collections || []).slice().sort((a, b) => a.sortOrder - b.sortOrder);
    S.scanning = !!cat.scanning;
    S.mediaRootPresent = cat.mediaRootPresent !== false;

    try {
      const lr = await api('/api/v1/library');
      const lib = lr.ok ? await lr.json() : [];
      S.resume = {};
      for (const row of lib) S.resume[row.filmKey] = row;
    } catch {}

    show('app');
    renderHome();
  }

  function renderWho() {
    const me = S.me || {};
    const name = me.displayName || me.username || '';
    $('avatar').textContent = (name[0] || '?').toUpperCase();
    $('whoName').textContent = name;
    $('whoLbl').textContent = 'Signed in as ' + (me.username || name) + (me.isOwner ? ' · owner' : '');
  }

  // Where a film's progress stands, or null if it has none worth showing.
  function progressOf(it) {
    const row = S.resume[it.filmKey];
    if (!row || !row.resumeMs || !it.durationMs) return null;
    const frac = row.resumeMs / it.durationMs;
    if (frac < 0.01 || frac > 0.95) return null;   // barely started, or into the credits
    return { frac, row };
  }

  function continueList() {
    return S.items.filter(progressOf)
      .sort((a, b) => (S.resume[b.filmKey].lastWatchedAt || 0) - (S.resume[a.filmKey].lastWatchedAt || 0));
  }
  function recentList() {
    return S.items.slice().sort((a, b) => b.addedAt - a.addedAt);
  }
  function watchAgainList() {
    return S.items.filter(it => (S.resume[it.filmKey] || {}).watchCount > 0 && !progressOf(it))
      .sort((a, b) => (S.resume[b.filmKey].lastWatchedAt || 0) - (S.resume[a.filmKey].lastWatchedAt || 0));
  }
  const WEEK = 7 * 24 * 3600 * 1000;
  function isNew(it) { return Date.now() - it.addedAt < 2 * WEEK && !S.resume[it.filmKey]; }

  function renderHome() {
    const home = $('home');
    home.innerHTML = '';
    const q = $('q').value.trim();
    if (q) { renderResults(q); return; }
    $('results').hidden = true; home.hidden = false;

    if (!S.items.length) {
      $('hero').hidden = true;
      const box = el('div', 'empty');
      box.style.paddingTop = '110px';
      box.textContent = !S.mediaRootPresent
        ? 'The server can’t see its film folder right now. If the media drive was just unplugged or remounted, check it and refresh.'
        : S.scanning ? 'Your library is still being indexed. Films appear here as they are found — refresh in a minute.'
        : 'No films on the server yet. Add some to its film folder and they’ll show up here.';
      home.appendChild(box);
      return;
    }

    renderHero();
    if (S.scanning) home.appendChild(el('div', 'notice', 'Still indexing the server — some films may not be here yet.'));

    const cont = continueList();
    if (cont.length) home.appendChild(shelf('Continue watching', cont, { progress:true }));
    home.appendChild(shelf('Recently added', recentList().slice(0, 24)));
    for (const c of S.collections) {
      const films = c.items.map(e => S.byFilmKey.get(e.filmKey)).filter(Boolean);
      if (films.length) home.appendChild(shelf(c.name, films));
    }
    const again = watchAgainList();
    if (again.length) home.appendChild(shelf('Watch again', again));

    const all = S.items.slice().sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity:'base', numeric:true }));
    const sec = el('section', 'shelf');
    const h = el('h2', null, 'All films');
    h.appendChild(el('span', 'count', String(all.length)));
    sec.appendChild(h);
    const grid = el('div', 'grid');
    for (const it of all) grid.appendChild(card(it, { progress:true }));
    sec.appendChild(grid);
    home.appendChild(sec);
  }

  function renderHero() {
    // The film you were last in the middle of; otherwise the newest arrival.
    const cont = continueList();
    const it = cont[0] || recentList()[0];
    S.heroFilm = it;
    const p = progressOf(it);
    $('hero').hidden = false;
    $('heroKicker').textContent = p ? 'Continue watching' : (isNew(it) ? 'New on your server' : 'Featured');
    $('heroTitle').textContent = it.name;
    $('heroMeta').textContent = [runtime(it.durationMs), playLabel(it)].filter(Boolean).join('  ·  ');
    $('heroBar').hidden = !p;
    if (p) $('heroBar').firstElementChild.style.width = (p.frac * 100).toFixed(1) + '%';

    const acts = $('heroActions'); acts.innerHTML = '';
    acts.appendChild(playButton(it));
    const more = el('button', 'btn ghost', 'More info');
    more.addEventListener('click', () => openDetail(it));
    acts.appendChild(more);

    const bg = $('heroBg');
    bg.classList.remove('on');
    if (it.artUrl) art(it.artUrl).then(u => {
      if (u && S.heroFilm === it) { bg.style.backgroundImage = `url("${u}")`; bg.classList.add('on'); }
    });
  }

  function playLabel(it) {
    const route = routeFor(it);
    if (!route) return 'Plays in the desktop app';
    return '';
  }

  function playButton(it, opts = {}) {
    const p = progressOf(it);
    const route = routeFor(it);
    const b = el('button', 'btn');
    b.appendChild(el('span', 'tri'));
    b.appendChild(document.createTextNode(p && !opts.fromStart ? 'Resume ' + clock(p.row.resumeMs) : 'Play'));
    if (!route) { b.disabled = true; b.title = 'This browser can’t play this film'; }
    b.addEventListener('click', () => play(it, p && !opts.fromStart ? p.row.resumeMs : 0));
    return b;
  }

  function shelf(title, films, opts = {}) {
    const sec = el('section', 'shelf');
    const h = el('h2', null, title);
    sec.appendChild(h);
    const lane = el('div', 'lane');
    const track = el('div', 'track');
    for (const it of films) track.appendChild(card(it, opts));
    const prev = el('button', 'nav prev', '‹');
    const next = el('button', 'nav next', '›');
    prev.setAttribute('aria-label', 'Scroll ' + title + ' left');
    next.setAttribute('aria-label', 'Scroll ' + title + ' right');
    prev.tabIndex = next.tabIndex = -1;
    const page = dir => track.scrollBy({ left: dir * track.clientWidth * 0.9, behavior:'smooth' });
    prev.addEventListener('click', () => page(-1));
    next.addEventListener('click', () => page(1));
    const sync = () => {
      prev.disabled = track.scrollLeft < 4;
      next.disabled = track.scrollLeft + track.clientWidth >= track.scrollWidth - 4;
    };
    track.addEventListener('scroll', sync, { passive:true });
    requestAnimationFrame(sync);
    lane.append(prev, track, next);
    sec.appendChild(lane);
    return sec;
  }

  function card(it, opts = {}) {
    const b = el('button', 'card');
    b.setAttribute('aria-label', it.name);
    const th = el('div', 'thumb');
    th.appendChild(el('span', 'ph', it.name));
    if (it.artUrl) {
      const img = el('img');
      img.alt = '';
      img.dataset.art = it.artUrl;
      th.appendChild(img);
      lazy.observe(img);
    }
    th.appendChild(el('span', 'play'));
    if (!routeFor(it)) th.appendChild(el('span', 'badge', 'App only'));
    else if (isNew(it)) th.appendChild(el('span', 'badge new', 'New'));
    const p = opts.progress && progressOf(it);
    if (p) {
      const bar = el('div', 'prog'); const i = el('i');
      i.style.width = (p.frac * 100).toFixed(1) + '%';
      bar.appendChild(i); th.appendChild(bar);
    }
    b.appendChild(th);
    const cap = el('div', 'cap');
    cap.appendChild(el('span', 't', it.name));
    cap.appendChild(el('span', 'm', p
      ? clock(it.durationMs - p.row.resumeMs) + ' left'
      : runtime(it.durationMs)));
    b.appendChild(cap);
    b.addEventListener('click', () => openDetail(it));
    return b;
  }

  function renderResults(q) {
    $('hero').hidden = true;
    $('home').hidden = true;
    const box = $('results');
    box.hidden = false; box.innerHTML = '';
    const needle = q.toLocaleLowerCase();
    const hits = S.items.filter(it => it.name.toLocaleLowerCase().includes(needle))
      .sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity:'base', numeric:true }));
    const sec = el('section', 'shelf');
    const h = el('h2', null, `Results for “${q}”`);
    h.appendChild(el('span', 'count', String(hits.length)));
    sec.appendChild(h);
    if (!hits.length) sec.appendChild(el('div', 'empty', 'Nothing in your library matches that.'));
    else {
      const grid = el('div', 'grid');
      for (const it of hits) grid.appendChild(card(it, { progress:true }));
      sec.appendChild(grid);
    }
    box.appendChild(sec);
  }

  let searchTimer = null;
  $('q').addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { renderHome(); if (!$('q').value) window.scrollTo(0, 0); }, 120);
  });
  $('q').addEventListener('keydown', e => {
    if (e.key === 'Escape') { $('q').value = ''; renderHome(); $('q').blur(); }
  });
  $('brand').addEventListener('click', () => { $('q').value = ''; renderHome(); window.scrollTo({ top:0, behavior:'smooth' }); });

  // ---- posters -------------------------------------------------------------
  // They need the bearer header, which an <img src> cannot send, so each is
  // fetched once, held as a blob URL, and only when it scrolls near view.

  const artCache = new Map();
  function art(url) {
    if (!artCache.has(url)) {
      artCache.set(url, api(url)
        .then(r => r.ok ? r.blob() : null)
        .then(b => b ? URL.createObjectURL(b) : null)
        .catch(() => { artCache.delete(url); return null; }));
    }
    return artCache.get(url);
  }
  const lazy = new IntersectionObserver(entries => {
    for (const e of entries) {
      if (!e.isIntersecting) continue;
      const img = e.target;
      lazy.unobserve(img);
      art(img.dataset.art).then(u => {
        if (!u) return;
        img.onload = () => {
          img.classList.add('on');
          const ph = img.parentNode && img.parentNode.querySelector('.ph');
          if (ph) setTimeout(() => { ph.hidden = true; }, 400);   // after the fade, not during it
        };
        img.src = u;
      });
    }
  }, { rootMargin:'300px 600px' });

  // ---- details ---------------------------------------------------------------

  let detailReturn = null;
  function openDetail(it) {
    detailReturn = document.activeElement;
    S.detailFilm = it;
    $('dTitle').textContent = it.name;
    const meta = $('dMeta'); meta.innerHTML = '';
    for (const m of [runtime(it.durationMs), dateOf(it.addedAt) && 'Added ' + dateOf(it.addedAt)])
      if (m) meta.appendChild(el('span', null, m));
    const seen = S.resume[it.filmKey];
    if (seen && seen.watchCount > 0) meta.appendChild(el('span', null,
      seen.watchCount === 1 ? 'Watched once' : `Watched ${seen.watchCount} times`));

    const prog = $('dProg'); prog.innerHTML = '';
    const p = progressOf(it);
    if (p) {
      const bar = el('div', 'bar'); const i = el('i');
      i.style.width = (p.frac * 100).toFixed(1) + '%';
      bar.appendChild(i); prog.appendChild(bar);
      prog.appendChild(el('div', 'left', clock(it.durationMs - p.row.resumeMs) + ' left'));
    }

    const route = routeFor(it);
    $('dWarn').hidden = !!route;
    $('dWarn').textContent = route ? '' :
      'This browser can’t decode this film’s video. It plays in the Vault Movies desktop app, and on most phones and TVs.';

    const acts = $('dActions'); acts.innerHTML = '';
    acts.appendChild(playButton(it));
    if (p && route) {
      const again = el('button', 'btn ghost', 'Start from the beginning');
      again.addEventListener('click', () => play(it, 0));
      acts.appendChild(again);
    }

    const facts = $('dFacts'); facts.innerHTML = '';
    const shelves = S.collections.filter(c => c.items.some(e => e.filmKey === it.filmKey)).map(c => c.name);
    const rows = [
      ['On your shelves', shelves.join(', ')],
      ['Video', [it.videoCodec && it.videoCodec.toUpperCase(), it.audioCodec && it.audioCodec.toUpperCase()].filter(Boolean).join(' · ')],
      ['File', size(it.sizeBytes)],
    ];
    for (const [k, val] of rows) if (val) { facts.appendChild(el('dt', null, k)); facts.appendChild(el('dd', null, val)); }

    const a = $('dArt');
    a.style.backgroundImage = '';
    if (it.artUrl) art(it.artUrl).then(u => { if (u && S.detailFilm === it) a.style.backgroundImage = `url("${u}")`; });

    $('detail').hidden = false;
    $('detail').scrollTop = 0;
    document.body.classList.add('locked');
    const first = acts.querySelector('button:not([disabled])') || $('dClose');
    first.focus({ preventScroll:true });
  }
  function closeDetail() {
    if ($('detail').hidden) return;
    $('detail').hidden = true;
    S.detailFilm = null;
    document.body.classList.remove('locked');
    if (detailReturn && document.contains(detailReturn)) detailReturn.focus({ preventScroll:true });
  }
  $('dClose').addEventListener('click', closeDetail);
  $('detail').addEventListener('click', e => { if (e.target === $('detail')) closeDetail(); });
  // Keep Tab inside the sheet while it is open.
  $('detail').addEventListener('keydown', e => {
    if (e.key !== 'Tab') return;
    const f = [...$('detail').querySelectorAll('button:not([disabled])')];
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  });

  // ---- account menu ------------------------------------------------------------

  function setMenu(open) {
    $('whoMenu').hidden = !open;
    $('whoBtn').setAttribute('aria-expanded', String(open));
  }
  $('whoBtn').addEventListener('click', e => { e.stopPropagation(); setMenu($('whoMenu').hidden); });
  document.addEventListener('click', e => { if (!e.target.closest('.who')) setMenu(false); });
  $('signoutBtn').addEventListener('click', () => { setMenu(false); signOut(false); });
  $('reloadBtn').addEventListener('click', () => { setMenu(false); enter(); });

  // ---- installing as an app ------------------------------------------------------
  // Already installed (opened from the home screen): nothing to offer.
  // Chrome/Edge: they hand over an install prompt we can open from the menu.
  // iPhone/iPad Safari: no prompt exists, so the menu item shows the steps.
  const standalone = matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;
  const isIOS = /iPad|iPhone|iPod/.test(navigator.userAgent) ||
    (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  let installPrompt = null;
  if (!standalone && isIOS) $('installBtn').hidden = false;
  window.addEventListener('beforeinstallprompt', e => {
    e.preventDefault();
    installPrompt = e;
    if (!standalone) $('installBtn').hidden = false;
  });
  window.addEventListener('appinstalled', () => { installPrompt = null; $('installBtn').hidden = true; });
  $('installBtn').addEventListener('click', async () => {
    setMenu(false);
    if (installPrompt) {
      const p = installPrompt; installPrompt = null;
      p.prompt();
      try { const { outcome } = await p.userChoice; if (outcome === 'accepted') $('installBtn').hidden = true; } catch {}
      return;
    }
    $('installTip').hidden = false;
    $('tipClose').focus();
  });
  $('tipClose').addEventListener('click', () => { $('installTip').hidden = true; });
  $('installTip').addEventListener('click', e => { if (e.target === $('installTip')) $('installTip').hidden = true; });

  window.addEventListener('scroll', () => {
    $('top').classList.toggle('solid', window.scrollY > 40);
  }, { passive:true });

  // ---- what this browser can play --------------------------------------------
  //
  // Two different questions, asked of two different things. Native HLS (Safari,
  // iOS, some Chromes) plays through the <video> element, so it is asked with
  // canPlayType. hls.js feeds Media Source Extensions, so for that route — and
  // only that route — MediaSource.isTypeSupported is the right question.

  const CODEC = { h264:'avc1.42E01E', hevc:'hvc1.1.6.L93.B0', av1:'av01.0.05M.08' };
  const probe = document.createElement('video');
  const can = t => { try { const r = probe.canPlayType(t); return r === 'probably' || r === 'maybe'; } catch { return false; } };
  const NATIVE_HLS = can('application/vnd.apple.mpegurl') || can('application/x-mpegURL');
  const MSE = window.ManagedMediaSource || window.MediaSource;
  const mseCan = c => { try { return !!MSE && MSE.isTypeSupported(`video/mp4; codecs="${c}"`); } catch { return false; } };
  const nativeCan = c => can(`video/mp4; codecs="${c}"`) ||
    (c.startsWith('hvc1') && can(`video/mp4; codecs="${c.replace('hvc1', 'hev1')}"`));

  // 'direct' | 'native' (HLS in the element) | 'hlsjs' | null
  function routeFor(it) {
    if (it.webPlayable) return 'direct';
    const c = CODEC[it.hlsVideoCodec];
    if (!it.hlsAvailable || !c) return null;
    if (NATIVE_HLS && nativeCan(c)) return 'native';
    if (mseCan(c)) return 'hlsjs';
    return null;
  }

  let hlsLib = null;
  function loadHls() {
    if (window.Hls) return Promise.resolve(window.Hls);
    if (!hlsLib) hlsLib = new Promise((res, rej) => {
      const s = document.createElement('script');
      s.src = HLS_JS; s.crossOrigin = 'anonymous';
      s.onload = () => window.Hls ? res(window.Hls) : rej(new Error('hls.js'));
      s.onerror = () => { hlsLib = null; rej(new Error('hls.js')); };
      document.head.appendChild(s);
    });
    return hlsLib;
  }

  // ---- playback ----------------------------------------------------------------

  function play(it, startMs) {
    const route = routeFor(it);
    if (!route) return;
    closeDetail();
    S.film = it; S.started = false;
    $('pTitle').textContent = it.name;
    $('player').hidden = false;
    document.body.classList.add('locked');
    bumpTop();
    if (route === 'direct') begin(it.streamUrl, startMs, 'direct');
    else prepare(it, startMs, route);
  }

  function prepState(title, pct, sub, spinning) {
    $('prep').hidden = false;
    $('prepTitle').textContent = title;
    $('prepPct').textContent = pct;
    $('prepSub').textContent = sub;
    $('prepSpin').hidden = !spinning;
  }

  // Ask the server for its web copy of the film and wait until enough of it
  // exists to start. Only the first viewer of a film ever sees this.
  function prepare(it, startMs, route) {
    prepState('Getting this film ready', '', 'This happens once per film. Next time it starts straight away.', true);
    const poll = async () => {
      if (S.film !== it) return;
      let r, j;
      try { r = await api(it.hlsUrl); j = await r.json().catch(() => null); }
      catch (e) {
        if (e.message === 'signed-out') return;
        if (S.film === it) S.prepTimer = setTimeout(poll, 4000);
        return;
      }
      if (S.film !== it) return;
      if (r.status === 409 || !j || j.error || j.state === 'failed' || j.state === 'unsupported') {
        prepState('Couldn’t prepare this film', '',
          (j && j.error) || 'Watch it in the Vault Movies desktop app instead.', false);
        return;
      }
      if (j.ready && j.playlistUrl) { begin(j.playlistUrl, startMs, route); return; }
      $('prepPct').textContent = j.percent > 0 ? j.percent + '%' : '';
      S.prepTimer = setTimeout(poll, 2000);
    };
    poll();
  }

  async function begin(src, startMs, route) {
    const it = S.film;
    if (route === 'hlsjs') {
      let Hls;
      try { Hls = await loadHls(); }
      catch {
        return prepState('Couldn’t load the player', '',
          'The video player component didn’t download. Check your connection and try again.', false);
      }
      if (S.film !== it) return;
      if (!Hls.isSupported()) {
        return prepState('This browser can’t play this film', '', 'Try the Vault Movies desktop app.', false);
      }
      // startPosition is explicit even at zero: a film still being written is
      // an EVENT playlist, which hls.js would otherwise treat as live and open
      // at the far end of whatever exists so far.
      const hls = new Hls({ startPosition: startMs > 0 ? startMs / 1000 : 0, maxBufferLength: 60 });
      S.hls = hls;
      let recovered = 0;
      hls.on(Hls.Events.ERROR, (_, d) => {
        if (!d.fatal || S.hls !== hls) return;
        if (d.type === Hls.ErrorTypes.MEDIA_ERROR && recovered++ < 2) { hls.recoverMediaError(); return; }
        if (d.type === Hls.ErrorTypes.NETWORK_ERROR && recovered++ < 3) { hls.startLoad(); return; }
        prepState('Playback stopped', '', 'Something went wrong reading this film. Go back and try again.', false);
      });
      hls.loadSource(src);
      hls.attachMedia(v);
    } else {
      v.src = src;
      if (startMs > 0) {
        const seek = () => { try { v.currentTime = startMs / 1000; } catch {} };
        v.addEventListener('loadedmetadata', seek, { once:true });
      }
    }
    $('prep').hidden = true;
    const p = v.play();
    if (p && p.catch) p.catch(() => {});   // autoplay refused: the controls are right there
    clearInterval(S.saveTimer);
    S.saveTimer = setInterval(() => pushResume(false), 20000);
  }

  // How long the film runs. An HLS film still being prepared reports Infinity
  // (an EVENT playlist looks open-ended), so the catalogue's length stands in.
  function filmLength() {
    if (isFinite(v.duration) && v.duration > 0) return v.duration;
    return S.film && S.film.durationMs ? S.film.durationMs / 1000 : 0;
  }

  function resumeRow(finished) {
    const it = S.film, prev = S.resume[it.filmKey] || { watchCount:0 };
    const now = Date.now();
    return {
      filmKey: it.filmKey, name: it.name,
      resumeMs: finished ? 0 : Math.floor(v.currentTime * 1000),
      watchCount: (prev.watchCount || 0) + (finished ? 1 : 0),
      lastWatchedAt: now, chaptersJson: prev.chaptersJson || null, updatedAt: now,
    };
  }
  function pushResume(finished, keepalive) {
    // Nothing is written until the film has actually played: backing out of
    // the "getting ready" screen must not overwrite a good bookmark with zero.
    if (!S.film || !S.started || !filmLength()) return;
    if (!finished && v.currentTime < 1) return;
    const row = resumeRow(finished);
    S.resume[row.filmKey] = row;
    api('/api/v1/library', { method:'PUT', body: JSON.stringify({ items:[row] }), keepalive: !!keepalive })
      .then(r => r.ok ? r.json() : null)
      .then(merged => { if (merged) for (const m of merged) S.resume[m.filmKey] = m; })
      .catch(() => {});
  }

  v.addEventListener('playing', () => { S.started = true; });
  v.addEventListener('pause', () => { if (!v.ended) pushResume(false); bumpTop(); });
  v.addEventListener('ended', () => { pushResume(true); stopPlayback(true); });

  function stopPlayback(alreadySaved) {
    clearTimeout(S.prepTimer); S.prepTimer = null;
    clearInterval(S.saveTimer); S.saveTimer = null;
    if (!alreadySaved) pushResume(false);
    if (S.hls) { try { S.hls.destroy(); } catch {} S.hls = null; }
    v.pause(); v.removeAttribute('src'); v.load();
    S.film = null; S.started = false;
    $('prep').hidden = true;
    $('player').hidden = true;
    document.body.classList.remove('locked');
    try { if (document.fullscreenElement) document.exitFullscreen(); } catch {}
    if (!$('app').hidden) renderHome();   // progress bars and "Continue watching" move
  }
  $('pBack').addEventListener('click', () => stopPlayback(false));

  // Leaving the page mid-film still keeps your place.
  window.addEventListener('pagehide', () => { if (S.film) pushResume(false, true); });
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'hidden' && S.film) pushResume(false, true);
  });

  // The back bar fades while a film plays and the mouse is still.
  let topTimer = null;
  function bumpTop() {
    $('ptop').classList.remove('hide');
    clearTimeout(topTimer);
    topTimer = setTimeout(() => { if (!v.paused && $('prep').hidden) $('ptop').classList.add('hide'); }, 3000);
  }
  $('player').addEventListener('mousemove', bumpTop);
  $('player').addEventListener('touchstart', bumpTop, { passive:true });

  document.addEventListener('keydown', e => {
    if (!$('player').hidden) {
      if (e.key === 'Escape' && !document.fullscreenElement) { stopPlayback(false); return; }
      if (e.target === v) return;   // the element's own controls handle their keys
      if (e.key === ' ' || e.key === 'k') { e.preventDefault(); v.paused ? v.play().catch(() => {}) : v.pause(); bumpTop(); }
      else if (e.key === 'ArrowLeft') { v.currentTime = Math.max(0, v.currentTime - 10); bumpTop(); }
      else if (e.key === 'ArrowRight') { v.currentTime = Math.min(filmLength() - 1, v.currentTime + 10); bumpTop(); }
      else if (e.key === 'f') { try { document.fullscreenElement ? document.exitFullscreen() : $('player').requestFullscreen(); } catch {} }
      return;
    }
    if (e.key === 'Escape' && !$('installTip').hidden) { $('installTip').hidden = true; return; }
    if (e.key === 'Escape' && !$('detail').hidden) { closeDetail(); return; }
    if (e.key === '/' && !$('app').hidden && document.activeElement !== $('q')) { e.preventDefault(); $('q').focus(); }
  });

  // ---- start ---------------------------------------------------------------

  (async () => {
    S.refresh = readStored();
    if (!S.refresh) { show('signin'); $('u').focus(); return; }
    const ok = await refresh();
    if (ok === true) return enter();
    if (ok === false) { try { localStorage.removeItem(LS_RT); } catch {} show('signin'); $('u').focus(); return; }
    message('Can’t reach your library', 'The server didn’t answer. Check your connection and try again.',
      [['Try again', () => location.reload()], ['Sign in again', () => signOut(true), true]]);
  })();
})();
</script>
</body>
</html>
""";
}
