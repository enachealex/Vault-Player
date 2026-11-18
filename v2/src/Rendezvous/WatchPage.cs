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
/// Two layouts from one page, split at 900px wide: computers and tablets get
/// the television's layout (glass tab bar along the top, details as a centred
/// glass panel); phones get the phone app's (glass tab bar along the bottom,
/// details as a sheet that slides up).
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
<!-- Declared dark, so a browser's "force dark" mode (Chrome and Edge on
     Android, some desktop settings) leaves the page alone instead of inverting
     its white buttons. It also gives form controls and scrollbars dark styling. -->
<meta name="color-scheme" content="dark">
<meta name="apple-mobile-web-app-capable" content="yes">
<meta name="mobile-web-app-capable" content="yes">
<meta name="apple-mobile-web-app-status-bar-style" content="black-translucent">
<meta name="apple-mobile-web-app-title" content="Vault">
<link rel="manifest" href="/watch/manifest.webmanifest">
<link rel="apple-touch-icon" href="/watch/apple-touch-icon.png">
<link rel="icon" type="image/png" href="/watch/favicon-64.png">
<title>Vault Movies</title>
<style>
  /* Dark smoked glass over film stills, as in the phone and TV apps. Buttons
     are rounded rectangles, never pills. #005ED9 is keyboard focus and nothing
     else; chosen things and progress are white. Rust is the logo and the NEW
     flag only; warm gold is the hero's kicker. */
  :root {
    --bg:#0B0D12; --deep:#06070A;
    --glass:rgba(14,16,22,.45); --glass-strong:rgba(16,18,26,.72);
    --fill:rgba(255,255,255,.08); --fill-hi:rgba(255,255,255,.16);
    --line:rgba(255,255,255,.14); --line-hi:rgba(255,255,255,.28);
    --text:#F4F6FA; --muted:#A6AFBD; --faint:#6F7887; --soft:#D5DAE3;
    --warm:#F5C77E; --rust:#E35A3C; --danger:#FF7A7A;
    --focus:#005ED9;
    --radius:12px; --tile-radius:10px;
    --gutter:16px;
    --tile-w:clamp(140px, 38vw, 220px);
    --tabbar-h:62px;
    --blur:blur(18px) saturate(150%);
    color-scheme:dark;
  }
  * { box-sizing:border-box; }
  html { -webkit-text-size-adjust:100%; }
  html, body { margin:0; background:var(--bg); color:var(--text); }
  body {
    font-family:'Segoe UI Variable Display', 'Segoe UI', Roboto, system-ui, -apple-system, sans-serif;
    -webkit-font-smoothing:antialiased; min-height:100vh; overflow-x:hidden;
    -webkit-tap-highlight-color:transparent;
  }
  body.locked { overflow:hidden; }
  button, input { font:inherit; color:inherit; }
  button { cursor:pointer; }
  :focus { outline:none; }
  :focus-visible { outline:3px solid var(--focus); outline-offset:3px; }
  [hidden] { display:none !important; }

  /* Glass where the browser can blur behind it; a darker solid fill where it can't. */
  .glass { background:var(--glass); border:1px solid var(--line); }
  .glass-strong { background:var(--glass-strong); border:1px solid var(--line); }
  @supports ((-webkit-backdrop-filter:blur(1px)) or (backdrop-filter:blur(1px))) {
    .glass, .glass-strong { -webkit-backdrop-filter:var(--blur); backdrop-filter:var(--blur); }
  }
  @supports not ((-webkit-backdrop-filter:blur(1px)) or (backdrop-filter:blur(1px))) {
    .glass { background:rgba(18,20,28,.88); } .glass-strong { background:rgba(18,20,28,.95); }
  }

  /* ---- shared pieces ---- */
  .btn { display:inline-flex; align-items:center; justify-content:center; gap:9px;
         height:46px; padding:0 22px; border-radius:10px; border:1px solid var(--text);
         background:var(--text); color:var(--bg); font-weight:700; font-size:16px;
         line-height:1; white-space:nowrap; }
  .btn:hover { background:#fff; }
  .btn.ghost { background:var(--fill-hi); border-color:rgba(255,255,255,.18); color:var(--text);
               -webkit-backdrop-filter:var(--blur); backdrop-filter:var(--blur); }
  .btn.ghost:hover { background:rgba(255,255,255,.24); border-color:var(--line-hi); }
  .btn[disabled] { opacity:.45; cursor:default; }
  .btn .tri { width:0; height:0; border-left:12px solid currentColor;
              border-top:7px solid transparent; border-bottom:7px solid transparent; }
  .btn svg { flex:none; }
  .iconbtn { width:40px; height:40px; border-radius:10px; display:inline-flex; align-items:center;
             justify-content:center; padding:0; font-weight:800; font-size:15px; }
  .logo { width:30px; height:30px; border-radius:8px; flex:none; display:inline-block;
          background:url('/watch/icon-192.png') center/cover, linear-gradient(135deg, #E35A3C, #B9381F); }
  .err { color:var(--danger); min-height:1.4em; font-size:14px; margin-top:12px; }
  .muted { color:var(--muted); }
  .tag { display:inline-block; font-size:11px; font-weight:700; letter-spacing:.06em; line-height:1;
         padding:4px 6px; border-radius:5px; border:1px solid rgba(255,255,255,.35); color:#E7EAF0; }
  .metaline { display:flex; flex-wrap:wrap; align-items:center; gap:8px; color:var(--soft); font-size:14px; }
  .metaline .dot { opacity:.5; }

  /* ---- sign-in and other centred screens ---- */
  .screen-center { min-height:100vh; display:flex; align-items:center; justify-content:center;
                   padding:24px var(--gutter);
                   background:
                     radial-gradient(ellipse 62% 78% at 78% 28%, rgba(122,60,36,.55), rgba(11,13,18,0) 78%),
                     radial-gradient(ellipse 70% 80% at 12% 94%, rgba(26,58,110,.55), rgba(11,13,18,0) 82%),
                     var(--bg); }
  .panel { width:100%; max-width:400px; padding:32px 28px; border-radius:20px; }
  .panel .logo { width:40px; height:40px; border-radius:10px; }
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
         display:flex; align-items:center; gap:10px;
         padding:calc(10px + env(safe-area-inset-top)) var(--gutter) 10px;
         background:linear-gradient(rgba(6,7,10,.7), rgba(6,7,10,0));
         transition:background .25s, box-shadow .25s; }
  /* The hairline is a shadow, not a border: a transparent border would show
     a repeat of the gradient's darkest row along the bottom edge. */
  #top.solid { background:rgba(11,13,18,.72); box-shadow:0 1px 0 var(--line);
               -webkit-backdrop-filter:var(--blur); backdrop-filter:var(--blur); }
  #top .brand { display:flex; align-items:center; gap:10px; font-weight:800; font-size:20px;
                letter-spacing:.2px; background:none; border:none; padding:4px; border-radius:8px; }
  #top .spacer { flex:1; }
  #tabs { display:none; gap:6px; padding:5px; border-radius:12px; }
  #tabs button { border:none; background:none; padding:8px 16px; border-radius:8px; font-weight:600;
                 font-size:15px; color:#C9CFD9; }
  #tabs button:hover { color:#fff; background:rgba(255,255,255,.08); }
  #tabs button[aria-current="page"] { background:var(--fill-hi); color:#fff; }
  .who { position:relative; }
  .menu { position:absolute; right:0; top:calc(100% + 8px); min-width:220px; padding:6px;
          border-radius:var(--radius); box-shadow:0 18px 40px rgba(0,0,0,.5); }
  .menu .lbl { padding:8px 10px 6px; font-size:12px; color:var(--faint); }
  .menu button { display:block; width:100%; text-align:left; padding:10px; border:none;
                 background:none; border-radius:8px; font-size:15px; }
  .menu button:hover { background:var(--fill-hi); }

  /* ---- bottom tab bar (phones) ---- */
  #tabbar { position:fixed; z-index:20; left:12px; right:12px; bottom:calc(10px + env(safe-area-inset-bottom));
            height:var(--tabbar-h); border-radius:18px; display:flex; justify-content:space-around; align-items:stretch;
            background:rgba(20,22,30,.62); box-shadow:0 10px 30px rgba(0,0,0,.45); }
  @supports not ((-webkit-backdrop-filter:blur(1px)) or (backdrop-filter:blur(1px))) {
    #tabbar { background:rgba(20,22,30,.95); }
  }
  #tabbar button { flex:1; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:4px;
                   border:none; background:none; font-size:11px; color:var(--muted); border-radius:14px; padding:0; }
  #tabbar button svg { width:22px; height:22px; }
  #tabbar button[aria-current="page"] { color:#fff; }
  #tabbar button:focus-visible { outline-offset:-3px; }

  /* ---- hero ---- */
  #hero { position:relative; height:max(480px, 72vh); height:max(480px, 72svh); overflow:hidden;
          display:flex; align-items:flex-end; justify-content:center; padding:0 var(--gutter) 20px; }
  #hero .bg { position:absolute; inset:0; background-size:cover; background-position:center 30%;
              opacity:0; transition:opacity 1.2s ease; }
  #hero .bg.on { opacity:1; }
  #hero::after { content:''; position:absolute; inset:0; pointer-events:none;
                 background:linear-gradient(180deg, rgba(11,13,18,.55) 0%, rgba(11,13,18,0) 22%,
                   rgba(11,13,18,0) 45%, rgba(11,13,18,.85) 78%, var(--bg) 100%); }
  #hero .inner { position:relative; z-index:1; width:100%; max-width:560px; text-align:center;
                 transition:opacity .35s ease; }
  #hero .inner.fading { opacity:0; }
  #hero .kicker { font-size:11px; font-weight:800; letter-spacing:.18em; text-transform:uppercase;
                  color:var(--warm); margin-bottom:10px; }
  #hero h1 { font-size:clamp(30px, 9vw, 40px); line-height:1.05; margin:0; font-weight:800;
             letter-spacing:-.01em; text-shadow:0 2px 18px rgba(0,0,0,.6); }
  #hero .metaline { justify-content:center; font-size:13px; margin:10px 0 16px; }
  #hero .actions { display:flex; gap:10px; }
  #hero .actions .btn { flex:1; padding:0 12px; }
  #dots { position:relative; z-index:1; display:flex; gap:6px; justify-content:center; margin-top:14px; }
  #dots button { width:8px; height:8px; padding:0; border:none; border-radius:2px;
                 background:rgba(255,255,255,.35); transition:background .3s; }
  #dots button[aria-current="true"] { background:#fff; }
  /* A larger hit area than the dot itself, for thumbs. */
  #dots button { position:relative; }
  #dots button::before { content:''; position:absolute; inset:-8px -3px; }
  @media (prefers-reduced-motion: reduce) {
    #hero .bg, #hero .inner { transition:none; }
  }

  /* ---- rows ---- */
  main { position:relative; padding-bottom:calc(var(--tabbar-h) + 40px + env(safe-area-inset-bottom)); }
  #rows { position:relative; z-index:2; padding-top:8px; }
  .shelf { margin:0 0 18px; }
  .shelf h2 { font-size:17px; margin:0 0 2px; padding:0 var(--gutter); font-weight:700; }
  .shelf h2 .count, .pageh .count { color:var(--faint); font-weight:600; font-size:.8em; margin-left:8px; }
  .lane { position:relative; }
  .track { display:grid; grid-auto-flow:column; grid-auto-columns:var(--tile-w); gap:10px;
           overflow-x:auto; overscroll-behavior-x:contain; scroll-snap-type:x mandatory;
           scroll-padding:0 var(--gutter); padding:10px var(--gutter) 12px; scrollbar-width:none; }
  .track::-webkit-scrollbar { display:none; }
  .track > * { scroll-snap-align:start; }
  .nav { position:absolute; top:10px; bottom:12px; width:calc(var(--gutter) - 4px); min-width:36px;
         border:none; background:rgba(11,13,18,.62); color:#fff; z-index:3;
         opacity:0; transition:opacity .2s; display:flex; align-items:center; justify-content:center; padding:0; }
  .nav svg { width:22px; height:22px; }
  .nav.prev { left:0; border-radius:0 10px 10px 0; }
  .nav.next { right:0; border-radius:10px 0 0 10px; }
  .lane:hover .nav:not([disabled]) { opacity:1; }
  .nav[disabled] { pointer-events:none; }
  @media (hover:none) { .nav { display:none; } }

  /* ---- a film tile: the still, its title written on it ---- */
  .tile { position:relative; display:block; width:100%; aspect-ratio:16/9; padding:0; margin:0;
          border-radius:var(--tile-radius); overflow:hidden; text-align:left;
          background:linear-gradient(135deg, #1B2130, #12151D); border:1px solid rgba(255,255,255,.06);
          transition:transform .2s ease, box-shadow .2s ease, border-color .2s; }
  .tile img { position:absolute; inset:0; width:100%; height:100%; object-fit:cover;
              opacity:0; transition:opacity .4s; }
  .tile img.on { opacity:1; }
  .tile::after { content:''; position:absolute; inset:0; pointer-events:none;
                 background:linear-gradient(180deg, rgba(0,0,0,0) 42%, rgba(0,0,0,.8)); }
  .tile .t { position:absolute; left:9px; right:9px; bottom:8px; z-index:1; font-weight:700; font-size:13px;
             line-height:1.2; text-shadow:0 1px 3px #000; display:-webkit-box; -webkit-line-clamp:2;
             -webkit-box-orient:vertical; overflow:hidden; }
  .tile.has-prog .t { bottom:17px; }
  .tile .flag { position:absolute; top:7px; left:7px; z-index:1; background:var(--rust); color:#fff;
                font-size:10px; font-weight:800; letter-spacing:.08em; line-height:1; padding:4px 6px; border-radius:5px; }
  .tile .app { position:absolute; top:7px; right:7px; z-index:1; font-size:10.5px; font-weight:700; line-height:1;
               padding:4px 6px; border-radius:5px; background:rgba(11,13,18,.82); border:1px solid var(--line);
               color:var(--warm); }
  .tile .prog { position:absolute; left:9px; right:9px; bottom:7px; height:3px; border-radius:2px;
                background:rgba(255,255,255,.25); z-index:1; overflow:hidden; }
  .tile .prog i { display:block; height:100%; background:#fff; border-radius:2px; }
  @media (hover:hover) {
    .tile:hover { transform:translateY(-4px) scale(1.04); border-color:var(--line-hi);
                  box-shadow:0 14px 30px rgba(0,0,0,.55); z-index:1; }
  }
  .tile:focus-visible { outline:3px solid var(--focus); outline-offset:3px; transform:scale(1.04); z-index:1;
                        box-shadow:0 14px 30px rgba(0,0,0,.55); }

  /* Columns no narrower than a row's tile; spare width goes to the tiles. */
  .grid { display:grid; grid-template-columns:repeat(auto-fill, minmax(var(--tile-w), 1fr));
          gap:14px 10px; padding:8px var(--gutter); }

  /* ---- pages other than Home ---- */
  #page { padding-top:calc(70px + env(safe-area-inset-top)); }
  .pageh { font-size:26px; font-weight:800; margin:6px 0 12px; padding:0 var(--gutter); }
  .searchbox { position:relative; display:block; margin:0 var(--gutter) 14px; max-width:640px; }
  .searchbox input { width:100%; height:48px; padding:0 14px 0 42px; border-radius:var(--radius); font-size:16px;
                     background:rgba(255,255,255,.06); border:1px solid var(--line); }
  .searchbox input:focus { border-color:var(--focus); box-shadow:0 0 0 1px var(--focus); }
  .searchbox input::-webkit-search-cancel-button { filter:invert(1); }
  .searchbox svg { position:absolute; left:14px; top:50%; transform:translateY(-50%); opacity:.7; pointer-events:none; }
  .sub { font-size:15px; font-weight:700; color:var(--muted); padding:0 var(--gutter); margin:4px 0 0; }
  .sub .count { color:var(--faint); font-weight:600; margin-left:6px; }
  .empty { padding:18px var(--gutter); color:var(--muted); line-height:1.6; max-width:560px; }
  .notice { margin:0 var(--gutter) 18px; padding:12px 16px; border-radius:var(--radius);
            background:var(--fill); border:1px solid var(--line); color:var(--muted); font-size:14px; }
  .acct { margin:8px var(--gutter); max-width:480px; padding:22px; border-radius:16px; }
  .acct .who-row { display:flex; align-items:center; gap:14px; margin-bottom:18px; }
  .acct .big { width:52px; height:52px; border-radius:12px; background:#2A3242; display:flex; align-items:center;
               justify-content:center; font-weight:800; font-size:22px; }
  .acct .nm { font-weight:700; font-size:18px; }
  .acct .btn { width:100%; margin-top:10px; }

  /* ---- film details ---- */
  #detail { position:fixed; inset:0; z-index:40; }
  #detail .dbg { position:absolute; inset:-60px; background:#0B0D12 center/cover no-repeat;
                 filter:blur(28px) brightness(.42) saturate(1.2); opacity:0; transition:opacity .3s; }
  #detail.open .dbg { opacity:1; }
  #detail .dscroll { position:absolute; inset:0; overflow-y:auto; overscroll-behavior:contain;
                     -webkit-overflow-scrolling:touch; }
  #detail .sheet { position:relative; min-height:100%; transform:translateY(100%);
                   transition:transform .32s cubic-bezier(.2,.8,.2,1); }
  #detail.open .sheet { transform:none; }
  /* The still fades out at the bottom (a mask, not a colour), so it melts into
     whatever glass is beneath it without a seam. */
  #detail .art { position:relative; aspect-ratio:16/9; background:center/cover no-repeat;
                 -webkit-mask-image:linear-gradient(180deg, #000 55%, transparent 100%);
                 mask-image:linear-gradient(180deg, #000 55%, transparent 100%); }
  #detail .art::after { content:''; position:absolute; inset:0;
                        background:linear-gradient(180deg, rgba(0,0,0,.35), rgba(0,0,0,0) 30%); }
  #detail .close { position:absolute; top:calc(12px + env(safe-area-inset-top)); right:12px; z-index:3; }
  #detail .body { position:relative; z-index:2; margin-top:-36px; padding:22px 18px calc(32px + env(safe-area-inset-bottom));
                  border-radius:22px 22px 0 0; border-width:1px 0 0;
                  min-height:calc(100vh - 56.25vw + 36px); }
  #detail h2 { font-size:26px; font-weight:800; line-height:1.1; margin:0; }
  #detail .metaline { font-size:13px; margin:8px 0 16px; }
  #detail .actions { display:flex; flex-direction:column; gap:10px; }
  #detail .actions .btn { width:100%; height:48px; }
  #detail .resume { display:flex; align-items:center; gap:10px; font-size:12.5px; color:var(--muted); margin:2px 0 4px; }
  #detail .resume .bar { flex:1; height:4px; border-radius:2px; background:rgba(255,255,255,.18); overflow:hidden; }
  #detail .resume .bar i { display:block; height:100%; background:#fff; border-radius:2px; }
  #detail .acts { display:flex; justify-content:space-around; gap:8px; margin:16px 0 14px; }
  #detail .act { display:flex; flex-direction:column; align-items:center; gap:6px; min-width:86px; padding:8px 10px;
                 border:none; background:none; border-radius:10px; color:var(--muted); font-size:12px; }
  #detail button.act:hover { background:var(--fill); color:var(--text); }
  #detail .act svg { width:24px; height:24px; color:#fff; }
  #detail .act .note { color:var(--faint); font-size:11px; margin-top:-3px; }
  #detail .warn { color:var(--warm); font-size:14px; line-height:1.5; margin:0 0 14px; }
  #detail .facts { display:grid; grid-template-columns:auto 1fr; gap:6px 18px; font-size:14px; margin:4px 0 22px; }
  #detail .facts dt { color:var(--faint); }
  #detail .facts dd { margin:0; color:var(--muted); }
  #detail h3 { font-size:15px; margin:0 0 10px; }
  #detail .more { display:grid; grid-template-columns:repeat(3, 1fr); gap:8px; }
  #detail .more .tile .t { font-size:12px; }
  @media (prefers-reduced-motion: reduce) { #detail .sheet, #detail .dbg { transition:none; } }

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

  /* ==== wide screens: the television's layout ==== */
  @media (min-width:900px) {
    :root { --gutter:clamp(32px, 3.75vw, 64px); --tile-w:clamp(180px, 16.5vw, 300px); }
    #top { padding-top:calc(18px + env(safe-area-inset-top)); padding-bottom:14px; gap:14px; }
    #top .brand .logo { width:36px; height:36px; border-radius:9px; }
    #tabs { display:flex; }
    #searchIcon { display:none; }
    #tabbar { display:none; }
    main { padding-bottom:64px; }

    #hero { height:clamp(560px, 90vh, 940px); justify-content:flex-start; align-items:flex-end;
            padding:0 var(--gutter) calc(24vh + 36px); }
    #hero::after { background:
      linear-gradient(90deg, rgba(11,13,18,.92) 0%, rgba(11,13,18,.55) 38%, rgba(11,13,18,0) 65%),
      linear-gradient(180deg, rgba(11,13,18,.5) 0%, rgba(11,13,18,0) 18%, rgba(11,13,18,0) 42%,
                      rgba(11,13,18,.88) 74%, var(--bg) 100%); }
    #hero .inner { text-align:left; max-width:620px; }
    #hero .kicker { font-size:12px; letter-spacing:.2em; margin-bottom:14px; }
    #hero h1 { font-size:clamp(44px, 4.4vw, 68px); line-height:1; letter-spacing:-.015em;
               text-shadow:0 3px 24px rgba(0,0,0,.55); }
    #hero .metaline { justify-content:flex-start; font-size:15px; margin:16px 0 22px; }
    #hero .actions .btn { flex:none; height:50px; padding:0 26px; font-size:17px; }
    #dots { justify-content:flex-start; margin-top:22px; }
    #rows { margin-top:-24vh; padding-top:0; }
    .shelf { margin-bottom:22px; }
    .shelf h2 { font-size:clamp(18px, 1.5vw, 22px); }
    .track { gap:14px; padding:12px var(--gutter) 14px; }
    .nav { top:12px; bottom:14px; }
    .tile .t { font-size:14px; left:11px; right:11px; bottom:9px; }
    .tile.has-prog .t { bottom:19px; }
    .tile .prog { left:11px; right:11px; bottom:8px; }
    .grid { gap:22px 14px; }
    #page { padding-top:calc(96px + env(safe-area-inset-top)); }
    .pageh { font-size:32px; }

    /* Details: a centred glass panel instead of a sheet from the bottom. */
    #detail .dscroll { display:flex; justify-content:center; align-items:flex-start; padding:6vh 24px; }
    #detail .sheet { width:100%; max-width:880px; min-height:0; border-radius:18px; overflow:hidden;
                     background:var(--glass-strong); border:1px solid var(--line);
                     -webkit-backdrop-filter:var(--blur); backdrop-filter:var(--blur);
                     box-shadow:0 30px 80px rgba(0,0,0,.6); transform:translateY(18px) scale(.98); opacity:0;
                     transition:transform .25s ease, opacity .25s ease; }
    #detail.open .sheet { transform:none; opacity:1; }
    #detail .close { top:14px; right:14px; }
    #detail .body { margin-top:-110px; min-height:0; padding:0 40px 36px; border:none; border-radius:0;
                    background:none; -webkit-backdrop-filter:none; backdrop-filter:none; }
    #detail h2 { font-size:clamp(30px, 3vw, 40px); }
    #detail .metaline { font-size:15px; margin:12px 0 20px; }
    #detail .actions { flex-direction:row; flex-wrap:wrap; }
    #detail .actions .btn { width:auto; min-width:180px; height:50px; }
    #detail .resume { order:3; flex-basis:100%; max-width:420px; margin-top:2px; }
    #detail .acts { justify-content:flex-start; gap:12px; margin:18px 0 18px -10px; }
  }
</style>
</head>
<body>

<!-- ---- sign in ---- -->
<section id="signin" class="screen-center" hidden>
  <form class="panel glass-strong" id="signinForm" autocomplete="on">
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
  <div class="panel glass-strong">
    <span class="logo" aria-hidden="true"></span>
    <h1 id="msgTitle"></h1>
    <p id="msgText"></p>
    <div class="row" id="msgRow"></div>
  </div>
</section>

<!-- ---- the app ---- -->
<div id="app" hidden>
  <header id="top">
    <button class="brand" id="brand" title="Home"><span class="logo" aria-hidden="true"></span><span>Vault</span></button>
    <nav id="tabs" class="glass" aria-label="Sections">
      <button data-view="home">Home</button>
      <button data-view="films">Films</button>
      <button data-view="mylist">My List</button>
      <button data-view="search">Search</button>
    </nav>
    <div class="spacer"></div>
    <button class="iconbtn glass" id="searchIcon" aria-label="Search">
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"
           aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/></svg></button>
    <div class="who">
      <button class="iconbtn glass" id="whoBtn" aria-haspopup="true" aria-expanded="false" aria-label="Account">
        <span id="avatar"></span></button>
      <div class="menu glass-strong" id="whoMenu" hidden>
        <div class="lbl" id="whoLbl"></div>
        <button id="installBtn" hidden>Install app</button>
        <button id="reloadBtn">Refresh library</button>
        <button id="signoutBtn">Sign out</button>
      </div>
    </div>
  </header>

  <main>
    <section id="hero" hidden aria-roledescription="carousel" aria-label="Featured films">
      <div class="bg" id="heroBgA"></div>
      <div class="bg" id="heroBgB"></div>
      <div class="inner" id="heroInner">
        <div class="kicker" id="heroKicker"></div>
        <h1 id="heroTitle"></h1>
        <div class="metaline" id="heroMeta"></div>
        <div class="actions" id="heroActions"></div>
        <div id="dots" role="group" aria-label="Choose a featured film"></div>
      </div>
    </section>
    <div id="rows"></div>

    <div id="page" hidden>
      <h1 class="pageh" id="pageTitle"></h1>
      <label class="searchbox" id="searchBox" hidden>
        <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4"
             aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/></svg>
        <input id="q" type="search" placeholder="Titles in your vault" aria-label="Search your films"
               autocomplete="off" autocapitalize="off" spellcheck="false" enterkeyhint="search">
      </label>
      <div id="pageBody"></div>
    </div>
  </main>

  <nav id="tabbar" class="glass" aria-label="Sections">
    <button data-view="home"><svg viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><path d="M3 11l9-7 9 7v9a1 1 0 0 1-1 1h-5v-6H9v6H4a1 1 0 0 1-1-1z"/></svg>Home</button>
    <button data-view="search"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/></svg>Search</button>
    <button data-view="mylist"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M12 5v14M5 12h14"/></svg>My List</button>
    <button data-view="account"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="12" cy="8" r="4"/><path d="M4 21c1-4 4-6 8-6s7 2 8 6"/></svg>Account</button>
  </nav>
</div>

<!-- ---- film details ---- -->
<div id="detail" hidden role="dialog" aria-modal="true" aria-labelledby="dTitle">
  <div class="dbg" id="dBg"></div>
  <div class="dscroll" id="dScroll">
    <div class="sheet" id="dSheet">
      <div class="art" id="dArt">
        <button class="iconbtn glass close" id="dClose" aria-label="Close">
          <svg width="16" height="16" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2.6" aria-hidden="true">
            <path d="M6 6l12 12M18 6L6 18"/></svg></button>
      </div>
      <div class="body glass-strong">
        <h2 id="dTitle"></h2>
        <div class="metaline" id="dMeta"></div>
        <p class="warn" id="dWarn" hidden></p>
        <div class="actions" id="dActions"></div>
        <div class="acts" id="dActs"></div>
        <dl class="facts" id="dFacts"></dl>
        <div id="dMore"></div>
      </div>
    </div>
  </div>
</div>

<!-- ---- how to install on an iPhone (Safari has no install prompt a page can open) ---- -->
<div id="installTip" hidden role="dialog" aria-modal="true" aria-labelledby="tipTitle">
  <div class="panel glass-strong">
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
  const HERO_MS = 9000;

  const S = {
    access: null, refresh: null, expiresAt: 0, me: null,
    items: [], byFilmKey: new Map(), resume: {}, collections: [], myList: [],
    view: 'home',
    film: null, hls: null, started: false, saveTimer: null, prepTimer: null,
    feat: [], featIdx: 0, heroLayer: 0, detailFilm: null,
  };
  const $ = id => document.getElementById(id);
  const v = $('v');
  const wide = matchMedia('(min-width:900px)');
  const calm = matchMedia('(prefers-reduced-motion: reduce)');

  // ---- small helpers ---------------------------------------------------

  function el(tag, cls, text) {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function svg(markup, size) {
    const s = size || 24;
    const span = el('span');
    span.innerHTML = `<svg width="${s}" height="${s}" viewBox="0 0 24 24" fill="none" stroke="currentColor"
      stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${markup}</svg>`;
    return span.firstElementChild;
  }
  const ICON = {
    plus: '<path d="M12 5v14M5 12h14"/>',
    check: '<path d="M5 12.5l4.5 4.5L19 7.5"/>',
    cc: '<rect x="3" y="6" width="18" height="12" rx="2"/><path d="M10 10.5a2 2 0 1 0 0 3M16 10.5a2 2 0 1 0 0 3"/>',
    info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v5M12 8h.01"/>',
    left: '<path d="M15 5l-7 7 7 7"/>',
    right: '<path d="M9 5l7 7-7 7"/>',
  };
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
  // "48 min left", "1h 12m left": the rest of a film, said the way people say it.
  function leftText(ms) {
    const min = Math.max(1, Math.round(ms / 60000));
    return (min < 60 ? `${min} min` : runtime(ms)) + ' left';
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
    closeDetail(true);
    stopHero();
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
    S.myList = cat.myList || [];
    S.scanning = !!cat.scanning;
    S.mediaRootPresent = cat.mediaRootPresent !== false;

    try {
      const lr = await api('/api/v1/library');
      const lib = lr.ok ? await lr.json() : [];
      S.resume = {};
      for (const row of lib) S.resume[row.filmKey] = row;
    } catch {}

    show('app');
    S.feat = featuredList();
    S.featIdx = 0;
    setView(S.view, { keepScroll:true });
  }

  function renderWho() {
    const me = S.me || {};
    const name = me.displayName || me.username || '';
    $('avatar').textContent = (name[0] || '?').toUpperCase();
    $('whoLbl').textContent = 'Signed in as ' + (me.username || name) + (me.isOwner ? ' · owner' : '');
  }

  // What to call a film on screen: its tidied title, else the file name.
  const titleOf = it => it.title || it.name;
  const byTitle = (a, b) => titleOf(a).localeCompare(titleOf(b), undefined, { sensitivity:'base', numeric:true });
  const lastWatched = (a, b) => ((S.resume[b.filmKey] || {}).lastWatchedAt || 0) - ((S.resume[a.filmKey] || {}).lastWatchedAt || 0);

  // Where a film's progress stands, or null if it has none worth showing.
  function progressOf(it) {
    const row = S.resume[it.filmKey];
    if (!row || !row.resumeMs || !it.durationMs) return null;
    const frac = row.resumeMs / it.durationMs;
    if (frac < 0.01 || frac > 0.95) return null;   // barely started, or into the credits
    return { frac, row };
  }

  const continueList = () => S.items.filter(progressOf).sort(lastWatched);
  const recentList = () => S.items.slice().sort((a, b) => b.addedAt - a.addedAt);
  const allList = () => S.items.slice().sort(byTitle);
  const watchAgainList = () => S.items.filter(it => (S.resume[it.filmKey] || {}).watchCount > 0 && !progressOf(it)).sort(lastWatched);
  const shelfFilms = c => c.items.map(e => S.byFilmKey.get(e.filmKey)).filter(Boolean);
  const myListFilms = () => S.myList.map(k => S.byFilmKey.get(k)).filter(Boolean);
  const onList = it => S.myList.includes(it.filmKey);
  const DAY = 24 * 3600 * 1000;
  const isNew = it => Date.now() - it.addedAt < 14 * DAY && !S.resume[it.filmKey];
  const is4k = it => /2160p|\b4k\b|\buhd\b/i.test(it.name);
  function searchList(q) {
    const needle = q.trim().toLocaleLowerCase();
    if (!needle) return allList();
    return S.items.filter(it => titleOf(it).toLocaleLowerCase().includes(needle) ||
      it.name.toLocaleLowerCase().includes(needle)).sort(byTitle);
  }

  // The films the featured slot cycles through: new arrivals first, then films
  // this account has never started, shuffled per visit so the top of the
  // screen is not the same film every time. Films with no still are skipped,
  // since the slot is mostly picture. Same choice as the phone app.
  function featuredList(count = 5) {
    const withArt = S.items.filter(it => it.artUrl);
    const fresh = withArt.filter(it => Date.now() - it.addedAt < 30 * DAY && !S.resume[it.filmKey]);
    const unwatched = withArt.filter(it => !S.resume[it.filmKey] && !fresh.includes(it));
    const shuffle = a => { for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j], a[i]]; } return a; };
    const picks = [...shuffle(fresh), ...shuffle(unwatched)];
    if (picks.length < count) picks.push(...shuffle(withArt.filter(it => !picks.includes(it))));
    const out = picks.slice(0, count);
    if (!out.length && S.items.length) out.push(recentList()[0]);
    return out;
  }

  // "2025 • 3h 17m • CC HD"
  function metaLine(box, it) {
    box.innerHTML = '';
    const parts = [it.year ? String(it.year) : '', runtime(it.durationMs)].filter(Boolean);
    parts.forEach((p, i) => {
      if (i) box.appendChild(el('span', 'dot', '•'));
      box.appendChild(el('span', null, p));
    });
    if (parts.length) box.appendChild(el('span', 'dot', '•'));
    if (it.captionsUrl) { const t = el('span', 'tag', 'CC'); t.title = 'English captions'; box.appendChild(t); }
    box.appendChild(el('span', 'tag', is4k(it) ? '4K' : 'HD'));
  }

  // ---- views ---------------------------------------------------------------

  const VIEWS = { home:'Home', films:'Films', mylist:'My List', search:'Search', account:'Account' };

  function setView(view, opts = {}) {
    if (!VIEWS[view]) view = 'home';
    // Account is a page on phones only; on a wide screen it is the menu up top.
    if (view === 'account' && wide.matches) view = 'home';
    S.view = view;
    for (const b of document.querySelectorAll('#tabs button, #tabbar button')) {
      if (b.dataset.view === view) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current');
    }
    render();
    if (!opts.keepScroll) window.scrollTo(0, 0);
    syncTop();
    if (view === 'search' && opts.focus !== false) $('q').focus({ preventScroll:true });
  }

  function render() {
    const home = S.view === 'home';
    $('rows').hidden = !home;
    $('page').hidden = home;
    if (home) { renderHome(); startHero(); }
    else { stopHero(); $('hero').hidden = true; renderPage(); }
  }

  function renderHome() {
    const rows = $('rows');
    rows.innerHTML = '';

    if (!S.items.length) {
      $('hero').hidden = true;
      const box = el('div', 'empty');
      box.style.paddingTop = '110px';
      box.textContent = !S.mediaRootPresent
        ? 'The server can’t see its film folder right now. If the media drive was just unplugged or remounted, check it and refresh.'
        : S.scanning ? 'Your library is still being indexed. Films appear here as they are found — refresh in a minute.'
        : 'No films on the server yet. Add some to its film folder and they’ll show up here.';
      rows.appendChild(box);
      return;
    }

    $('hero').hidden = false;
    if (S.scanning) rows.appendChild(el('div', 'notice', 'Still indexing the server — some films may not be here yet.'));

    const add = (title, films) => { if (films.length) rows.appendChild(shelf(title, films)); };
    add('Continue watching', continueList());
    add('Recently added', recentList().slice(0, 24));
    add('My List', myListFilms());
    for (const c of S.collections) add(c.name, shelfFilms(c));
    add('Watch again', watchAgainList());
    add('All films', allList());
  }

  function renderPage() {
    const view = S.view;
    $('pageTitle').textContent = VIEWS[view];
    $('searchBox').hidden = view !== 'search';
    const body = $('pageBody');
    body.innerHTML = '';
    if (view === 'films') {
      $('pageTitle').appendChild(el('span', 'count', String(S.items.length)));
      body.appendChild(grid(allList()));
    } else if (view === 'mylist') {
      const films = myListFilms();
      if (films.length) body.appendChild(grid(films));
      else body.appendChild(el('div', 'empty', 'Nothing saved yet. Open any film and choose My List to keep it here.'));
    } else if (view === 'search') {
      renderResults();
    } else if (view === 'account') {
      body.appendChild(accountPanel());
    }
  }

  function renderResults() {
    const body = $('pageBody');
    body.innerHTML = '';
    const q = $('q').value.trim();
    const hits = searchList(q);
    const h = el('h2', 'sub', q ? `Results for “${q}”` : 'All films, A–Z');
    h.appendChild(el('span', 'count', String(hits.length)));
    body.appendChild(h);
    if (!hits.length) body.appendChild(el('div', 'empty', 'Nothing in your library matches that.'));
    else body.appendChild(grid(hits));
  }

  function accountPanel() {
    const me = S.me || {};
    const name = me.displayName || me.username || '';
    const box = el('div', 'acct glass');
    const row = el('div', 'who-row');
    row.appendChild(el('div', 'big', (name[0] || '?').toUpperCase()));
    const txt = el('div');
    txt.appendChild(el('div', 'nm', name));
    txt.appendChild(el('div', 'muted', 'Signed in as ' + (me.username || name) + (me.isOwner ? ' · owner' : '')));
    row.appendChild(txt);
    box.appendChild(row);
    const btn = (label, fn) => { const b = el('button', 'btn ghost', label); b.addEventListener('click', fn); box.appendChild(b); };
    if (!$('installBtn').hidden) btn('Install app', install);
    btn('Refresh library', () => enter());
    btn('Sign out', () => signOut(false));
    return box;
  }

  let searchTimer = null;
  $('q').addEventListener('input', () => {
    clearTimeout(searchTimer);
    searchTimer = setTimeout(() => { if (S.view === 'search') renderResults(); }, 120);
  });
  $('q').addEventListener('keydown', e => {
    if (e.key === 'Escape' && $('q').value) { e.stopPropagation(); $('q').value = ''; renderResults(); }
  });
  for (const b of document.querySelectorAll('#tabs button, #tabbar button'))
    b.addEventListener('click', () => setView(b.dataset.view));
  $('brand').addEventListener('click', () => setView('home'));
  $('searchIcon').addEventListener('click', () => setView('search'));

  // ---- rows and tiles --------------------------------------------------------

  function shelf(title, films) {
    const sec = el('section', 'shelf');
    sec.appendChild(el('h2', null, title));
    const lane = el('div', 'lane');
    const track = el('div', 'track');
    for (const it of films) track.appendChild(tile(it));
    const prev = el('button', 'nav prev');
    const next = el('button', 'nav next');
    prev.appendChild(svg(ICON.left)); next.appendChild(svg(ICON.right));
    prev.setAttribute('aria-label', 'Scroll ' + title + ' left');
    next.setAttribute('aria-label', 'Scroll ' + title + ' right');
    // Keyboard users move tile by tile with Tab, so the arrows are for mice only.
    prev.tabIndex = next.tabIndex = -1;
    const page = dir => track.scrollBy({ left: dir * track.clientWidth * 0.85, behavior:'smooth' });
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

  function grid(films) {
    const g = el('div', 'grid');
    for (const it of films) g.appendChild(tile(it));
    return g;
  }

  function tile(it) {
    const b = el('button', 'tile');
    const p = progressOf(it);
    b.setAttribute('aria-label', titleOf(it) + (it.year ? `, ${it.year}` : '') + (p ? `, ${leftText(it.durationMs - p.row.resumeMs)}` : ''));
    if (it.artUrl) {
      const img = el('img');
      img.alt = '';
      img.dataset.art = it.artUrl;
      b.appendChild(img);
      lazy.observe(img);
    }
    if (!routeFor(it)) b.appendChild(el('span', 'app', 'App only'));
    if (isNew(it)) b.appendChild(el('span', 'flag', 'NEW'));
    b.appendChild(el('span', 't', titleOf(it)));
    if (p) {
      b.classList.add('has-prog');
      const bar = el('span', 'prog'); const i = el('i');
      i.style.width = (p.frac * 100).toFixed(1) + '%';
      bar.appendChild(i); b.appendChild(bar);
    }
    b.addEventListener('click', () => openDetail(it));
    return b;
  }

  // ---- the featured film -------------------------------------------------------
  //
  // Two picture layers take turns, so the next still fades in over the last
  // one instead of the hero going dark between films. The next film's still is
  // fetched before the swap starts.

  let heroTimer = null;
  function startHero() {
    if (!S.feat.length) S.feat = featuredList();
    if (!S.feat.length) return;
    if (S.featIdx >= S.feat.length) S.featIdx = 0;
    showFeatured(S.featIdx, true);
    renderDots();
    clearInterval(heroTimer);
    // Reduced motion: no automatic rotation; the dots still change the film.
    if (S.feat.length > 1 && !calm.matches) heroTimer = setInterval(heroTick, HERO_MS);
  }
  function stopHero() { clearInterval(heroTimer); heroTimer = null; }
  function heroTick() {
    // Held still while anything is on top of it, while a key or the pointer
    // is in it (so a button never changes under a click), or while hidden.
    if (S.view !== 'home' || !$('detail').hidden || !$('player').hidden || document.hidden) return;
    if ($('hero').contains(document.activeElement) && document.activeElement !== document.body) return;
    if ($('heroInner').matches(':hover')) return;
    showFeatured((S.featIdx + 1) % S.feat.length, false);
  }

  function showFeatured(i, instant) {
    const it = S.feat[i];
    if (!it) return;
    S.featIdx = i;
    const inner = $('heroInner');
    const fill = () => { fillHero(it); inner.classList.remove('fading'); };
    if (instant || calm.matches) fill();
    else { inner.classList.add('fading'); setTimeout(() => { if (S.feat[S.featIdx] === it) fill(); }, 350); }
    for (const [n, d] of [...$('dots').children].entries()) d.setAttribute('aria-current', String(n === i));

    const layers = [$('heroBgA'), $('heroBgB')];
    if (!it.artUrl) { layers.forEach(l => l.classList.remove('on')); return; }
    art(it.artUrl).then(u => {
      if (!u || S.feat[S.featIdx] !== it) return;
      const cur = layers[S.heroLayer];
      // Already showing this still (the first film again, or a re-render): leave it.
      if (cur.classList.contains('on') && cur.dataset.film === it.filmKey) return;
      const nextLayer = instant && !cur.classList.contains('on') ? cur : layers[1 - S.heroLayer];
      nextLayer.style.backgroundImage = `url("${u}")`;
      nextLayer.dataset.film = it.filmKey;
      nextLayer.classList.add('on');
      for (const l of layers) if (l !== nextLayer) l.classList.remove('on');
      S.heroLayer = layers.indexOf(nextLayer);
    });
  }

  function fillHero(it) {
    const p = progressOf(it);
    $('heroKicker').textContent = p ? 'Continue watching' : isNew(it) ? 'New in your vault' : 'From your vault';
    $('heroTitle').textContent = titleOf(it);
    metaLine($('heroMeta'), it);
    const acts = $('heroActions'); acts.innerHTML = '';
    acts.appendChild(playButton(it));
    const more = el('button', 'btn ghost');
    more.appendChild(svg(ICON.info, 20));
    more.appendChild(document.createTextNode('More info'));
    more.addEventListener('click', () => openDetail(it));
    acts.appendChild(more);
  }

  function renderDots() {
    const box = $('dots');
    box.innerHTML = '';
    box.hidden = S.feat.length < 2;
    S.feat.forEach((it, n) => {
      const d = el('button');
      d.setAttribute('aria-label', `Featured film ${n + 1} of ${S.feat.length}: ${titleOf(it)}`);
      d.setAttribute('aria-current', String(n === S.featIdx));
      d.addEventListener('click', () => { if (n !== S.featIdx) showFeatured(n, false); restartHeroTimer(); });
      box.appendChild(d);
    });
  }
  // A chosen film gets its full nine seconds.
  function restartHeroTimer() {
    if (!heroTimer) return;
    clearInterval(heroTimer);
    heroTimer = setInterval(heroTick, HERO_MS);
  }

  function playButton(it, opts = {}) {
    const p = progressOf(it);
    const route = routeFor(it);
    const b = el('button', 'btn');
    b.appendChild(el('span', 'tri'));
    b.appendChild(document.createTextNode(p && !opts.fromStart ? 'Resume' : 'Play'));
    if (!route) { b.disabled = true; b.title = 'This browser can’t play this film'; }
    b.addEventListener('click', () => play(it, p && !opts.fromStart ? p.row.resumeMs : 0));
    return b;
  }

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
        img.onload = () => img.classList.add('on');
        img.src = u;
      });
    }
  }, { rootMargin:'300px 600px' });

  // ---- details ---------------------------------------------------------------

  let detailReturn = null, detailHide = null;
  function openDetail(it) {
    if ($('detail').hidden) detailReturn = document.activeElement;
    S.detailFilm = it;
    fillDetail(it);
    clearTimeout(detailHide);
    const d = $('detail');
    const wasOpen = !d.hidden && d.classList.contains('open');
    d.hidden = false;
    $('dScroll').scrollTop = 0;
    document.body.classList.add('locked');
    if (!wasOpen) { void d.offsetWidth; d.classList.add('open'); }   // reflow first, so it animates in
    const first = $('dActions').querySelector('button:not([disabled])') || $('dClose');
    first.focus({ preventScroll:true });
  }

  function fillDetail(it) {
    $('dTitle').textContent = titleOf(it);
    metaLine($('dMeta'), it);

    const route = routeFor(it);
    $('dWarn').hidden = !!route;
    $('dWarn').textContent = route ? '' :
      'This browser can’t decode this film’s video. It plays in the Vault Movies desktop app, and on most phones and TVs.';

    const p = progressOf(it);
    const acts = $('dActions'); acts.innerHTML = '';
    acts.appendChild(playButton(it));
    if (p) {
      // Under Resume on a phone; under both buttons on a wide screen (see .resume).
      const line = el('div', 'resume');
      const bar = el('div', 'bar'); const i = el('i');
      i.style.width = (p.frac * 100).toFixed(1) + '%';
      bar.appendChild(i);
      line.append(bar, el('span', null, leftText(it.durationMs - p.row.resumeMs)));
      acts.appendChild(line);
      if (route) {
        const again = el('button', 'btn ghost', 'Start over');
        again.addEventListener('click', () => play(it, 0));
        acts.appendChild(again);
      }
    }

    // The row of small actions: My List, and a note when there are captions.
    const row = $('dActs'); row.innerHTML = '';
    const mine = el('button', 'act');
    const paintMine = () => {
      const on = onList(it);
      mine.innerHTML = '';
      mine.appendChild(svg(on ? ICON.check : ICON.plus));
      mine.appendChild(el('span', null, 'My List'));
      mine.setAttribute('aria-pressed', String(on));
      mine.setAttribute('aria-label', on ? 'Remove from My List' : 'Add to My List');
    };
    paintMine();
    mine.addEventListener('click', () => toggleMyList(it, paintMine));
    row.appendChild(mine);
    if (it.captionsUrl) {
      const cc = el('div', 'act');
      cc.appendChild(svg(ICON.cc));
      cc.appendChild(el('span', null, 'Captions'));
      cc.appendChild(el('span', 'note', 'English'));
      row.appendChild(cc);
    }

    const facts = $('dFacts'); facts.innerHTML = '';
    const seen = S.resume[it.filmKey];
    const shelves = S.collections.filter(c => c.items.some(e => e.filmKey === it.filmKey)).map(c => c.name);
    const rows = [
      ['Watched', seen && seen.watchCount > 0 ? (seen.watchCount === 1 ? 'Once' : `${seen.watchCount} times`) : ''],
      ['Shelves', shelves.join(', ')],
      ['Added', it.addedAt ? dateOf(it.addedAt) : ''],
      ['Format', [it.videoCodec, it.audioCodec].filter(Boolean).map(s => s.toUpperCase()).join(' · ')],
      ['File size', size(it.sizeBytes)],
    ];
    for (const [k, val] of rows) if (val) { facts.appendChild(el('dt', null, k)); facts.appendChild(el('dd', null, val)); }

    // More from your vault: films that share a shelf with this one, then the newest.
    const more = $('dMore'); more.innerHTML = '';
    const picks = [];
    for (const c of S.collections) if (c.items.some(e => e.filmKey === it.filmKey))
      for (const f of shelfFilms(c)) if (f !== it && !picks.includes(f)) picks.push(f);
    for (const f of recentList()) if (f !== it && !picks.includes(f)) picks.push(f);
    if (picks.length) {
      more.appendChild(el('h3', null, 'More from your vault'));
      const g = el('div', 'more');
      for (const f of picks.slice(0, 6)) g.appendChild(tile(f));
      more.appendChild(g);
    }

    const a = $('dArt'), bg = $('dBg');
    a.style.backgroundImage = bg.style.backgroundImage = '';
    if (it.artUrl) art(it.artUrl).then(u => {
      if (u && S.detailFilm === it) a.style.backgroundImage = bg.style.backgroundImage = `url("${u}")`;
    });
  }

  function closeDetail(now) {
    const d = $('detail');
    if (d.hidden) return;
    d.classList.remove('open');
    S.detailFilm = null;
    document.body.classList.remove('locked');
    clearTimeout(detailHide);
    if (now || calm.matches) d.hidden = true;
    else detailHide = setTimeout(() => { d.hidden = true; }, 320);
    if (detailReturn && document.contains(detailReturn)) detailReturn.focus({ preventScroll:true });
  }
  $('dClose').addEventListener('click', () => closeDetail());
  // A click on the blurred backdrop, outside the panel, closes it (wide screens).
  $('dScroll').addEventListener('click', e => { if (e.target === $('dScroll')) closeDetail(); });
  // Keep Tab inside the sheet while it is open.
  $('detail').addEventListener('keydown', e => {
    if (e.key !== 'Tab') return;
    const f = [...$('detail').querySelectorAll('button:not([disabled])')];
    if (!f.length) return;
    const first = f[0], last = f[f.length - 1];
    if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
    else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
  });

  // Saved at once on screen, then on the server; the server's answer (the
  // whole list) is the truth. A failure puts the button back as it was.
  async function toggleMyList(it, repaint) {
    const was = onList(it);
    const before = S.myList.slice();
    S.myList = was ? S.myList.filter(k => k !== it.filmKey) : [it.filmKey, ...S.myList];
    repaint();
    try {
      const r = await api('/api/v1/mylist/' + encodeURIComponent(it.filmKey), { method: was ? 'DELETE' : 'PUT' });
      if (!r.ok) throw new Error('mylist ' + r.status);
      const list = await r.json().catch(() => null);
      if (Array.isArray(list)) S.myList = list;
    } catch (e) {
      if (e.message === 'signed-out') return;
      S.myList = before;
    }
    repaint();
    refreshBehind();
  }

  // The page under the details sheet catches up (My List row, progress bars)
  // without restarting the featured film.
  function refreshBehind() {
    if (S.view === 'home') { renderHome(); const it = S.feat[S.featIdx]; if (it) fillHero(it); }
    else if (S.view !== 'search') renderPage();
    else renderResults();
  }

  // ---- account menu ------------------------------------------------------------

  function setMenu(open) {
    $('whoMenu').hidden = !open;
    $('whoBtn').setAttribute('aria-expanded', String(open));
  }
  $('whoBtn').addEventListener('click', e => {
    e.stopPropagation();
    if (!wide.matches) { setView('account'); return; }
    setMenu($('whoMenu').hidden);
  });
  document.addEventListener('click', e => { if (!e.target.closest('.who')) setMenu(false); });
  $('signoutBtn').addEventListener('click', () => { setMenu(false); signOut(false); });
  $('reloadBtn').addEventListener('click', () => { setMenu(false); enter(); });
  // Turning a phone into a tablet-sized window, or back, moves Account between page and menu.
  wide.addEventListener('change', () => {
    setMenu(false);
    if (!$('app').hidden && S.view === 'account' && wide.matches) setView('home');
  });

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
  async function install() {
    setMenu(false);
    if (installPrompt) {
      const p = installPrompt; installPrompt = null;
      p.prompt();
      try { const { outcome } = await p.userChoice; if (outcome === 'accepted') $('installBtn').hidden = true; } catch {}
      if (S.view === 'account') renderPage();
      return;
    }
    $('installTip').hidden = false;
    $('tipClose').focus();
  }
  $('installBtn').addEventListener('click', install);
  $('tipClose').addEventListener('click', () => { $('installTip').hidden = true; });
  $('installTip').addEventListener('click', e => { if (e.target === $('installTip')) $('installTip').hidden = true; });

  // The top bar turns to glass once anything scrolls under it; pages other
  // than Home have no hero behind it, so it is glass from the start there.
  function syncTop() { $('top').classList.toggle('solid', window.scrollY > 40 || S.view !== 'home'); }
  window.addEventListener('scroll', syncTop, { passive:true });

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
    closeDetail(true);
    stopHero();
    S.film = it; S.started = false;
    $('pTitle').textContent = titleOf(it);
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
      // A plain file carries no caption track the browser can see, so the
      // server's WebVTT copy is added beside it (off until chosen in the
      // player's own menu). HLS streams list their captions themselves.
      if (route === 'direct' && it.captionsUrl) {
        const tr = el('track');
        tr.kind = 'subtitles'; tr.srclang = 'en'; tr.label = 'English'; tr.src = it.captionsUrl;
        v.appendChild(tr);
      }
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

  // ---- captions on or off, as last chosen --------------------------------
  // Whatever the viewer last picked in the player's captions menu holds for
  // the next film and the next visit. A film's tracks turn up as it loads
  // (a <track> for a plain file, the stream's own for HLS), so the choice is
  // applied as each one is added. Changes this code makes itself are not
  // mistaken for the viewer's choice.
  const CC_KEY = 'vault.watch.captions';
  let ccApplying = false;
  function ccWanted() { try { return localStorage.getItem(CC_KEY); } catch { return null; } }
  function subtitleTracks() {
    return Array.from(v.textTracks).filter(t => t.kind === 'subtitles' || t.kind === 'captions');
  }
  function applyCaptionsChoice() {
    const want = ccWanted();
    if (want !== 'on' && want !== 'off') return;   // never chosen: leave the film's default
    const tracks = subtitleTracks();
    if (!tracks.length) return;
    const pick = want === 'on'
      ? (tracks.find(t => /^en/i.test(t.language || '') || /english/i.test(t.label || '')) || tracks[0])
      : null;
    ccApplying = true;
    for (const t of tracks) {
      const mode = t === pick ? 'showing' : 'disabled';
      if (t.mode !== mode) t.mode = mode;
    }
    // The resulting 'change' events fire asynchronously; stand down after them.
    setTimeout(() => { ccApplying = false; }, 0);
  }
  v.textTracks.addEventListener('addtrack', () => setTimeout(applyCaptionsChoice, 0));
  v.textTracks.addEventListener('change', () => {
    if (ccApplying || !S.film) return;
    const on = subtitleTracks().some(t => t.mode === 'showing');
    try { localStorage.setItem(CC_KEY, on ? 'on' : 'off'); } catch {}
  });

  function stopPlayback(alreadySaved) {
    clearTimeout(S.prepTimer); S.prepTimer = null;
    clearInterval(S.saveTimer); S.saveTimer = null;
    if (!alreadySaved) pushResume(false);
    if (S.hls) { try { S.hls.destroy(); } catch {} S.hls = null; }
    v.pause(); v.removeAttribute('src');
    for (const t of v.querySelectorAll('track')) t.remove();
    v.load();
    S.film = null; S.started = false;
    $('prep').hidden = true;
    $('player').hidden = true;
    document.body.classList.remove('locked');
    try { if (document.fullscreenElement) document.exitFullscreen(); } catch {}
    // Progress bars and "Continue watching" move; the featured film stays put.
    if (!$('app').hidden) { refreshBehind(); if (S.view === 'home') startHero(); }
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
    if (e.key === 'Escape' && !$('whoMenu').hidden) { setMenu(false); $('whoBtn').focus(); return; }
    if (e.key === '/' && !$('app').hidden && $('detail').hidden && document.activeElement !== $('q')) {
      e.preventDefault(); setView('search');
    }
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
