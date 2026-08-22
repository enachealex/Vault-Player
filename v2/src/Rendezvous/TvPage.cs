namespace VideoPlayer.Rendezvous;

/// <summary>
/// The television app, served at GET /tv and run in the TV's own browser.
///
/// Written for a remote, not a mouse: everything is reachable with a D-pad,
/// focus is always visible and always somewhere, and Back never strands anyone.
/// Sign-in never asks for a password — the phone does that (see
/// docs/TV-APP-INTEGRATION.md) and the television only takes a six-digit code.
///
/// Served BY the relay on purpose. Nothing under /api/v1, /media or /stream
/// sends CORS headers, so same-origin is the only way this page can call the
/// API at all — hosting it anywhere else is a different project, not a variant.
///
/// COMPATIBILITY FLOOR ~Chromium 53 (2018 webOS/Tizen sets are still in living
/// rooms). That rules out CSS Grid, flex `gap`, `aspect-ratio`, `padStart`,
/// `scrollIntoView` options, `?.` and `??`. `gap` deserves special mention: it
/// collapses to zero spacing in silence rather than failing, so a layout built
/// on it looks merely broken rather than obviously unsupported.
/// </summary>
public static class TvPage
{
    public static string Render(string linkUrl, string linkUrlShort) => Template
        .Replace("__LINKURL__", linkUrl)
        .Replace("__LINKSHORT__", linkUrlShort);

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Vault Movies</title>
<style>
  :root {
    --bg:#1A0D09; --deep:#0E0604; --surface:#24100B; --line:#43281F;
    --text:#F6ECE5; --muted:#D9C0B3; --accent:#C8442A; --warm:#F0A070;
  }
  * { box-sizing:border-box; }
  /* Every size on this page is in rem against this root, which rescale() then
     drives from the screen's own size. Sizes here are 0.375rem larger than they
     were, which is six pixels at the 16px root a 1080p set gets. */
  html { font-size:16px; }
  html,body { height:100%; margin:0; }
  body {
    background:var(--bg); color:var(--text); overflow:hidden; cursor:none;
    font-family:'Segoe UI',Roboto,system-ui,-apple-system,sans-serif;
    -webkit-font-smoothing:antialiased;
  }
  /* Televisions overscan; nothing that matters sits in the outer edge. */
  .screen { display:none; height:100%; padding:3.4rem 5rem; }
  .screen.on { display:block; }
  .col { height:100%; display:flex; flex-direction:column; }

  h1 { font-size:2.975rem; margin:0 0 .6rem; font-weight:700; }
  .sub { color:var(--muted); font-size:1.725rem; margin:0 0 1.6rem; line-height:1.5; }

  /* Focus IS the interface here. It must never be subtle or absent. */
  [data-f] { outline:none; }
  [data-f].focus { box-shadow:0 0 0 .25rem var(--accent); }

  /* ---- sign in ---- */
  .url { font-size:2.975rem; font-weight:800; color:var(--warm); margin:.4rem 0 1.4rem; }
  .entry { font-family:Consolas,monospace; font-size:3.975rem; font-weight:800; color:var(--accent);
           letter-spacing:.5rem; min-height:4.4rem; }
  .entry .ph { color:var(--line); }
  .keys { max-width:70rem; }
  /* Eleven keys across at 5.4rem with .5rem between them; the spacebar matches
     that span so the bottom of the keyboard lines up with the top of it. */
  .key.space { width:64.4rem; }
  /* inline-block + margins, never flex gap */
  /* Fixed size, and centred by the box rather than by padding. Height used to
     come from padding plus the line box, so Del and Enter — set smaller so the
     words fit — came out five pixels shorter than the digits beside them. The
     key is a shape now and the text sits in the middle of it, whatever size
     that text happens to be. */
  .key {
    display:inline-flex; align-items:center; justify-content:center;
    width:5.4rem; height:5.4rem; margin:0 .5rem .5rem 0; padding:0;
    background:var(--surface); border:1px solid var(--line); border-radius:.45rem;
    font-size:1.875rem; font-weight:700; text-align:center; color:var(--text);
    font-family:inherit; vertical-align:top;
  }
  .key.act { color:var(--warm); font-size:1.425rem; }
  .krow { white-space:nowrap; }
  .err { color:var(--accent); font-size:1.675rem; min-height:2rem; margin-top:1rem; }

  /* ---- library ---- */
  .bar { flex:none; margin-bottom:1.2rem; }
  .bar .who { float:right; color:var(--muted); font-size:1.575rem; padding-top:.6rem;
              text-align:right; }
  .whoname { margin-bottom:.5rem; }
  /* Collections. inline-block chips rather than flex, for the same reason the
     grid uses them: `gap` collapses to nothing on the sets this has to run on.
     Hidden entirely until a shelf exists, so a library nobody has organised
     keeps the layout it had — and, more importantly, so Up from the first row
     of films does not land on an invisible row of nothing. */
  .rail { margin-top:1rem; display:none; }
  .rail.on { display:block; }
  .chip {
    display:inline-block; margin:0 .6rem .6rem 0; padding:.6rem 1.3rem;
    background:var(--surface); border:1px solid var(--line); border-radius:1.4rem;
    font-size:1.525rem; font-weight:600; color:var(--muted);
    font-family:inherit; vertical-align:top;
  }
  .chip.sel { background:var(--accent); border-color:var(--accent); color:#fff; }
  /* ---- dropdowns ---- */
  .menu { display:none; position:fixed; z-index:50; background:var(--deep);
          border:1px solid var(--line); border-radius:.8rem; padding:.6rem;
          box-shadow:0 1.4rem 3.4rem rgba(0,0,0,.7); }
  .menu.on { display:block; }
  .mrow {
    display:block; width:100%; text-align:left; background:transparent; border:none;
    color:var(--text); font-family:inherit; font-size:1.525rem; padding:.9rem 1.4rem;
    border-radius:.5rem; white-space:nowrap;
  }
  .mrow[disabled] { color:var(--line); }
  /* The layout choices show the shape they produce, with the words underneath
     rather than beside — a grid of squares reads as a picture of the result,
     and the label says which one it is. */
  .mrow.tile { text-align:center; padding:1.1rem 1.6rem; }
  .mrow.tile .lab { display:block; margin-top:.7rem; font-size:1.35rem; }
  .mrow.tile .sub2 { display:block; font-size:1.15rem; color:var(--muted); margin-top:.15rem; }
  .mrow.sel .lab { color:var(--accent); font-weight:700; }
  .caret { margin-left:.7rem; font-size:1.1rem; color:var(--muted); }

  /* The layout control sits at the far right of the shelf row. */
  .chip.layout { float:right; margin-right:0; padding:.55rem 1rem; }
  .gg { display:inline-block; line-height:0; }
  .gg i { display:inline-block; background:var(--muted); margin:.09rem; vertical-align:top; }
  .gg3 i { width:.62rem; height:.62rem; }
  .gg4 i { width:.48rem; height:.48rem; }
  .gg5 i { width:.38rem; height:.38rem; }
  .gg6 i { width:.31rem; height:.31rem; }
  .chip.layout.focus .gg i { background:var(--text); }

  /* The padding is what stops the focus ring being shaved off. Cards sit flush
     against the edges of this box, and the ring is drawn OUTSIDE the card, so
     without room to breathe the outer column loses its left or right edge and
     the top and bottom rows lose theirs to the scroll clip — which reads as the
     highlight being broken rather than as the card being at the edge. */
  .grid { flex:1; min-height:0; overflow-y:auto; overflow-x:hidden; padding:.4rem; }
  .grid::-webkit-scrollbar { display:none; }
  /* Column count is a setting, so the widths are written per layout rather than
     once. inline-block and percentage margins, never flex gap — gap collapses
     to nothing on the sets this has to run on and the grid silently loses all
     its spacing. */
  .card {
    display:inline-block; vertical-align:top;
    background:transparent; border:none; padding:0; text-align:left; color:inherit;
    font-family:inherit;
  }
  .cols3 .card { width:31.3%; margin:0 3% 2rem 0; }
  .cols3 .card:nth-child(3n) { margin-right:0; }
  .cols4 .card { width:23.5%; margin:0 2% 1.9rem 0; }
  .cols4 .card:nth-child(4n) { margin-right:0; }
  .cols5 .card { width:18.4%; margin:0 1.9% 1.8rem 0; }
  .cols5 .card:nth-child(5n) { margin-right:0; }
  .cols6 .card { width:15.4%; margin:0 1.5% 1.7rem 0; }
  .cols6 .card:nth-child(6n) { margin-right:0; }

  /* The number in the corner of each card, and the digits typed to reach it. */
  .idx { position:absolute; right:.4rem; bottom:.4rem; background:rgba(14,6,4,.88);
         color:var(--text); font-size:1.225rem; font-weight:700;
         padding:.15rem .5rem; border-radius:.3rem; }
  .jump { position:fixed; left:50%; top:50%; transform:translate(-50%,-50%);
          background:rgba(14,6,4,.95); border:2px solid var(--accent); border-radius:1rem;
          padding:1.6rem 3rem; font-size:4rem; font-weight:800; color:var(--accent);
          display:none; letter-spacing:.4rem; }
  .jump.on { display:block; }
  /* 16:9, because these are frames lifted out of the films themselves, not
     printed poster art. A 2:3 portrait box cropped a 2.39:1 frame down to a
     narrow vertical strip — the thin, distorted slivers this used to show.
     padding-top is the portable way to hold a ratio; aspect-ratio is far too
     new for the sets this has to run on. */
  .thumb { position:relative; width:100%; padding-top:56.25%; background:var(--surface);
           border-radius:.5rem; background-size:cover; background-position:center; overflow:hidden; }
  .thumb .glyph { position:absolute; left:0; top:42%; width:100%; text-align:center;
                  font-size:3.375rem; color:var(--line); }
  /* The title reserves two lines so cards keep a common height, but the box was
     taller than the two lines it holds, which left a gap under every one-line
     title. min-height sized to the line height closes it without letting a long
     title push its card out of line with the rest of the row. */
  /* No reserved second line. The box used to hold two lines whatever the title
     was, which left a short one floating above a gap before the runtime. It
     sizes to its text now and is capped at two lines, so a long title still
     cannot push its card out of step with the row. Cards are top-aligned, so
     uneven heights cost nothing. */
  .name { font-size:1.525rem; font-weight:600; margin-top:.55rem; line-height:1.25;
          max-height:3.8rem; overflow:hidden; }
  .meta { font-size:1.375rem; color:var(--muted); margin-top:-.15rem; }
  .badge { position:absolute; left:.4rem; top:.4rem; background:rgba(14,6,4,.9); color:var(--warm);
           font-size:1.225rem; padding:.25rem .5rem; border-radius:.35rem; font-weight:700; }
  .resume { position:absolute; left:0; right:0; bottom:0; height:.35rem; background:rgba(0,0,0,.55); }
  .resume i { display:block; height:100%; background:var(--accent); }

  /* ---- collections ---- */
  .shlist { flex:1; min-height:0; overflow-y:auto; padding:.4rem; }
  .shlist::-webkit-scrollbar { display:none; }
  /* Capped rather than full-bleed. A button the width of a television reads as
     a banner, and the eye has to travel the whole screen to find where the
     highlight is. */
  .shrow {
    display:block; width:100%; max-width:46rem; text-align:left; margin:0 0 .7rem;
    padding:1.1rem 1.5rem; background:var(--surface); border:1px solid var(--line);
    border-radius:.6rem; color:var(--text); font-size:1.775rem; font-family:inherit;
  }
  .shrow .tick { color:var(--accent); font-weight:700; margin-right:1rem; }
  .shrow.new { color:var(--warm); }

  /* ---- detail ---- */
  /* Text on the left, the film on the right. Two columns rather than a title
     stranded at the top of an empty screen. */
  /* Both columns centred against each other rather than the text riding at the
     top of an otherwise empty screen. */
  .dwrap { display:flex; flex:1; min-height:0; align-items:center; }
  .dtext { flex:1; min-width:0; padding-right:3.5rem; }
  .dart { flex:none; width:52%; position:relative;
          padding-top:29.25%; background:var(--surface); border-radius:.8rem;
          background-size:cover; background-position:center; overflow:hidden;
          box-shadow:0 1.2rem 3rem rgba(0,0,0,.55); }
  .dart .glyph { position:absolute; left:0; top:40%; width:100%; text-align:center;
                 font-size:4.375rem; color:var(--line); }
  .dmeta { color:var(--muted); font-size:1.675rem; margin:.6rem 0; }
  .warn { color:var(--warm); font-size:1.625rem; margin:1rem 0; max-width:52rem; line-height:1.55; }
  .btn { display:inline-block; background:var(--accent); color:#fff; border:none; border-radius:.5rem;
         padding:1.1rem 2.6rem; font-size:1.725rem; font-weight:700; font-family:inherit; margin:1.4rem .7rem 0 0; }
  .btn.ghost { background:transparent; border:1px solid var(--line); color:var(--text); }
  .btn[disabled] { opacity:.4; }

  /* ---- player ---- */
  #player { padding:0; background:#000; }
  #player.on { position:relative; }
  video { width:100%; height:100%; background:#000; display:block; }
  #hud { position:absolute; left:0; right:0; bottom:0; padding:2rem 4rem 2.6rem;
         background:linear-gradient(transparent,rgba(14,6,4,.95)); transition:opacity .25s; }
  #hud.hide { opacity:0; }
  #hud .t { font-size:2.175rem; font-weight:700; }
  #hud .times { font-size:1.475rem; color:var(--muted); margin:.8rem 0 .5rem; }
  #hud .times .right { float:right; }
  .track { height:.4rem; background:rgba(255,255,255,.22); border-radius:.2rem; overflow:hidden; clear:both; }
  .track i { display:block; height:100%; background:var(--accent); }
  /* Positioned against #hud, its parent, so it fades with the rest of the HUD
     instead of sitting over the picture for the whole film. */
  #hint { position:absolute; top:.7rem; right:4rem; color:var(--muted); font-size:1.425rem; }
  .center { text-align:center; color:var(--muted); font-size:1.775rem; padding-top:6rem; }

  /* Shown while the server repackages a film the television cannot open as it
     stands. Absolutely positioned rather than flex-centred: this has to lay out
     the same on a 2018 set as on a current one. */
  #prep { display:none; position:absolute; left:0; right:0; top:38%; text-align:center; }
  #prep.on { display:block; }
  #prep .pt { font-size:2.275rem; font-weight:700; }
  #prep .pp { font-size:3.575rem; font-weight:700; color:var(--accent); margin:1.4rem 0 1rem; }
  #prep .ps { font-size:1.575rem; color:var(--muted); }
</style>
</head>
<body>

<section id="signin" class="screen on"><div class="col">
  <h1>Sign in to Vault Movies</h1>
  <p class="sub">On your phone, open this page and sign in with your username <b>or email</b>.<br>It will show you a six-digit code.</p>
  <div class="url">__LINKSHORT__</div>
  <div class="entry" id="entry"></div>
  <div class="keys" id="keys"></div>
  <div class="err" id="signinErr"></div>
</div></section>

<section id="gate" class="screen"><div class="col">
  <h1 id="gateTitle">Waiting for approval</h1>
  <p class="sub" id="gateMsg"></p>
  <div id="gateRow"></div>
</div></section>

<section id="library" class="screen"><div class="col">
  <div class="bar"><span class="who" id="who"></span><h1>Your library</h1>
    <div class="rail" id="rail"></div></div>
  <div class="grid" id="grid"></div>
  <!-- Shows the digits as they are typed, so pressing 1 then 4 is visibly on
       its way to 14 rather than looking like it jumped to 1 and stuck. -->
  <div class="jump" id="jump"></div>
<div class="menu" id="menu"></div>
</div></section>

<section id="detail" class="screen"><div class="col">
  <div class="dwrap">
    <div class="dtext">
      <h1 id="dTitle"></h1>
      <div class="dmeta" id="dMeta"></div>
      <div class="warn" id="dWarn"></div>
      <div id="dRow"></div>
    </div>
    <!-- The film itself, rather than half a screen of nothing. -->
    <div class="dart" id="dArt"><span class="glyph">&#127916;</span></div>
  </div>
</div></section>

<section id="shelves" class="screen"><div class="col">
  <h1>Add to a collection</h1>
  <p class="sub" id="shTitle"></p>
  <div class="err" id="shErr"></div>
  <div class="shlist" id="shList"></div>
</div></section>

<section id="newshelf" class="screen"><div class="col">
  <h1>Name the collection</h1>
  <p class="sub">Something you will recognise on the shelf &mdash; "Pixar", "Christmas".</p>
  <div class="entry" id="nameEntry"></div>
  <div class="keys" id="nameKeys"></div>
  <div class="err" id="nameErr"></div>
</div></section>

<section id="player" class="screen">
  <video id="v" playsinline preload="auto"></video>
  <div id="prep"><div class="pt">Preparing this film for your television</div>
    <div class="pp" id="prepPct">Starting&hellip;</div>
    <div class="ps">This happens once. Next time it starts straight away.</div></div>
  <div id="hud">
    <!-- Inside the HUD, not beside it: bumpHud only fades #hud, so a sibling
         would sit over the picture for the whole film. -->
    <div id="hint">OK pause &middot; &#9664; &#9654; skip 10s &middot; Back exit</div>
    <div class="t" id="pTitle"></div>
    <div class="times"><span id="pAt">0:00</span><span class="right" id="pLeft"></span></div>
    <div class="track"><i id="pBar" style="width:0"></i></div>
  </div>
</section>

<script>
(function () {
  'use strict';
  // A telephone keypad, laid out the way every one of them is: three across,
  // counting down, with the bottom row carrying 0 between the two actions. Del
  // sits under 7 and Enter under 9, so both are a single press from the digits
  // above them and neither ever needs hunting for.
  //
  // Rows are descriptors rather than a string, because that bottom row mixes a
  // digit with two actions and a string cannot say that.
  var ROWS = [
    ['1', '2', '3'],
    ['4', '5', '6'],
    ['7', '8', '9'],
    [{ act: 'del', label: 'Del' }, '0', { act: 'go', label: 'Enter' }]
  ];
  var ALPHABET = '0123456789';
  var CODE_LEN = 6;
  var LS = 'vault.tv.rt';

  var S = { access:null, refresh:null, me:null, items:[], resume:{}, film:null, screen:'signin',
            memoryOnly:false, collections:[], shelf:null, cols:4 };

  // How many films across. Remembered per television, because it is a property
  // of how far away the sofa is, not of the account.
  var COLS = [3, 4, 5, 6];
  var LS_COLS = 'vault.tv.cols';
  try {
    var savedCols = parseInt(localStorage.getItem(LS_COLS), 10);
    if (COLS.indexOf(savedCols) >= 0) S.cols = savedCols;
  } catch (e) {}

  // What this particular television can decode.
  //
  // Asked of a <video> element, deliberately, and not of MediaSource: these are
  // native HLS playbacks, and MediaSource.isTypeSupported answers a question
  // about Media Source Extensions instead — it reports false for plenty a set
  // plays perfectly well, which would strand half the library for no reason.
  //
  // canPlayType is vague by design ('probably', 'maybe', ''), so both of the
  // non-empty answers are taken as yes. A wrong yes costs a viewer an error
  // message; a wrong no costs them the film.
  var CAN = (function () {
    var t = document.createElement('video');
    function ok(type) {
      var r = '';
      try { r = t.canPlayType(type); } catch (e) {}
      return r === 'probably' || r === 'maybe';
    }
    return {
      hls:  ok('application/vnd.apple.mpegurl') || ok('application/x-mpegURL'),
      h264: ok('video/mp4; codecs="avc1.42E01E"'),
      // Both tags of the same codec. Sets differ on which they admit to, and a
      // set that claims either will play the hvc1 the server writes.
      hevc: ok('video/mp4; codecs="hvc1.1.6.L93.B0"') || ok('video/mp4; codecs="hev1.1.6.L93.B0"'),
      av1:  ok('video/mp4; codecs="av01.0.05M.08"')
    };
  })();

  // How this television would play a given film, or null if it cannot.
  //   'direct' — the file itself opens in a browser
  //   'hls'    — the server repackages it first
  function routeFor(it) {
    if (it.webPlayable) return 'direct';
    if (!it.hlsAvailable || !CAN.hls) return null;
    if (it.hlsVideoCodec === 'h264' && CAN.h264) return 'hls';
    if (it.hlsVideoCodec === 'hevc' && CAN.hevc) return 'hls';
    if (it.hlsVideoCodec === 'av1' && CAN.av1) return 'hls';
    return null;
  }
  var entry = '';

  function $(id) { return document.getElementById(id); }
  function pad2(n) { n = String(n); return n.length > 1 ? n : '0' + n; }   // padStart is Chromium 57
  function fmt(ms) {
    var s = Math.max(0, Math.floor(ms / 1000));
    var h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
    return h ? h + ':' + pad2(m) + ':' + pad2(s % 60) : m + ':' + pad2(s % 60);
  }

  // The browser window on a TV is whatever size the browser feels like. Scale
  // the whole design off one root font size rather than assuming 1080p.
  function rescale() {
    var k = Math.min(window.innerWidth / 1920, window.innerHeight / 1080);
    document.documentElement.style.fontSize = Math.max(9, k * 16) + 'px';
  }
  window.addEventListener('resize', rescale);

  function show(name) {
    S.screen = name;
    var all = ['signin','gate','library','detail','shelves','newshelf','player'];
    for (var i = 0; i < all.length; i++) {
      var el = $(all[i]);
      if (all[i] === name) el.className = 'screen on'; else el.className = 'screen';
    }
    focusFirst(true);
  }

  // ---- focus ------------------------------------------------------------
  // While a menu is open it is the only thing focus may reach. Without that the
  // arrows wander out of the menu and into the grid behind it, leaving a menu
  // on screen that nothing is pointing at.
  function focusScope() {
    return document.querySelector('.menu.on') || document.querySelector('.screen.on');
  }
  function focusables() {
    var out = [], all = focusScope().querySelectorAll('[data-f]');
    for (var i = 0; i < all.length; i++) if (all[i].offsetParent !== null && !all[i].disabled) out.push(all[i]);
    return out;
  }
  function current() { return focusScope().querySelector('[data-f].focus'); }
  function setFocus(el) {
    if (!el) return;
    // Cleared across the whole document, not within the current scope. Opening
    // a menu narrows the scope to the menu, so a scoped clear would leave the
    // ring behind on the button that opened it and two things would look
    // focused at once.
    clearFocusRing();
    el.className += ' focus';
    // Move real DOM focus too, not only the ring. Without this the document's
    // active element never leaves <body>, so a television's own spatial
    // navigation and its voice guidance have nothing to work with — and on a
    // set whose browser drives the arrow keys itself, the page never sees them.
    try { el.focus(); } catch (e) {}
    // scrollIntoView options need Chromium 61; keep the element visible by hand.
    var g = el.parentNode;
    while (g && g !== document.body && g.className.indexOf('grid') < 0) g = g.parentNode;
    if (g && g.className && g.className.indexOf('grid') >= 0) {
      // Leave room for the focus ring at both ends, or scrolling parks a card
      // exactly against the edge and clips the very highlight that says where
      // you are.
      var pad = 24;
      var top = el.offsetTop, bot = top + el.offsetHeight;
      if (top - pad < g.scrollTop) g.scrollTop = top - pad;
      else if (bot + pad > g.scrollTop + g.clientHeight) g.scrollTop = bot - g.clientHeight + pad;
    }
  }
  function clearFocusRing() {
    var lit = document.querySelectorAll('[data-f].focus');
    for (var i = 0; i < lit.length; i++)
      lit[i].className = lit[i].className.replace(/\s*focus/, '');
  }

  function focusFirst(force) {
    if (!force && current()) return;
    var f = focusables(), pref = null;
    for (var i = 0; i < f.length; i++) if (f[i].getAttribute('data-first') !== null) { pref = f[i]; break; }
    clearFocusRing();
    setFocus(pref || f[0]);
  }
  function move(dx, dy) {
    var from = current();
    if (!from) { focusFirst(true); return; }
    var a = from.getBoundingClientRect();
    var ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    var f = focusables(), best = null, bestScore = Infinity;
    for (var i = 0; i < f.length; i++) {
      if (f[i] === from) continue;
      var b = f[i].getBoundingClientRect();
      var ox = b.left + b.width / 2 - ax, oy = b.top + b.height / 2 - ay;
      if (dx && (dx > 0 ? ox <= 1 : ox >= -1)) continue;
      if (dy && (dy > 0 ? oy <= 1 : oy >= -1)) continue;
      var along = dx ? Math.abs(ox) : Math.abs(oy);
      var across = dx ? Math.abs(oy) : Math.abs(ox);
      var score = along + across * 3;   // punish sideways drift so a press goes where it pointed
      if (score < bestScore) { bestScore = score; best = f[i]; }
    }
    if (best) setFocus(best);
  }

  // ---- remote -----------------------------------------------------------
  // Back differs per platform and an unhandled Back closes the whole app on
  // some televisions, which is the worst outcome available.
  var BACK = [8, 27, 10009 /* Tizen */, 461 /* webOS */];
  document.addEventListener('keydown', function (e) {
    var k = e.key, c = e.keyCode, handled = true;
    // Cheap insurance: if a re-arm was ever lost, ordinary use puts it back.
    if (!trapArmed()) armTrap();
    // While a film is up there is nothing to focus, so sideways means seek.
    // Plenty of remotes send only the arrows during playback and never the
    // dedicated transport keys, so without this the hint on screen would be
    // promising something a lot of sets could not do.
    var playing = S.screen === 'player';
    if (k === 'ArrowLeft' || c === 37) { if (playing) seekBy(-10); else move(-1, 0); }
    else if (k === 'ArrowRight' || c === 39) { if (playing) seekBy(10); else move(1, 0); }
    else if (k === 'ArrowUp' || c === 38) { if (!playing) move(0, -1); }
    else if (k === 'ArrowDown' || c === 40) { if (!playing) move(0, 1); }
    else if (k === 'Enter' || c === 13) activate();
    else if (BACK.indexOf(c) >= 0 || k === 'Backspace' || k === 'Escape' || k === 'GoBack') handleBack(VIA_KEY);
    else if (c === 415 || c === 19 || c === 179 || k === 'MediaPlayPause') { if (S.screen === 'player') togglePlay(); }
    else if (c === 417 || k === 'MediaFastForward') seekBy(30);
    else if (c === 412 || k === 'MediaRewind') seekBy(-30);
    else if (S.screen === 'signin' && typeText(k, c)) { /* physical keyboard */ }
    // On the library, the remote's number buttons move the highlight to the
    // film carrying that number.
    else if (S.screen === 'library' && jumpDigit(k, c)) { /* handled */ }
    else handled = false;
    if (handled) { e.preventDefault(); e.stopPropagation(); }
  }, true);

  function typeText(k, c) {
    var ch = (k && k.length === 1) ? k.toUpperCase() : String.fromCharCode(c);
    if (ALPHABET.indexOf(ch) < 0) return false;
    pushChar(ch); return true;
  }
  function activate() {
    if (S.screen === 'player') { togglePlay(); return; }
    var el = current(); if (el) el.click();
  }
  // Back arrives by two different roads and sometimes both at once.
  //
  // A television browser treats Back as history navigation and acts on it
  // before the page's key handler ever runs — preventDefault comes too late,
  // which is why pressing Back inside a film walked out of the app instead of
  // leaving the film. The cure is to always have somewhere to go back TO: a
  // history entry of our own is pushed, so the press is spent popping that
  // rather than leaving, and a fresh one is pushed to replace it.
  //
  // Some sets then deliver the keydown as well, so one press can arrive twice.
  // Anything within a few hundred milliseconds of the last is the same press.
  // One press can arrive on both roads at once, so they are told apart: two
  // hits on DIFFERENT roads in quick succession are one press echoing, while
  // two on the SAME road are a viewer pressing Back twice and must both count —
  // otherwise backing quickly out of nested screens silently loses presses.
  var VIA_KEY = 1, VIA_POP = 2, ECHO_MS = 350;
  var lastBackAt = 0, lastBackVia = 0;

  function handleBack(via) {
    var now = new Date().getTime();
    if (lastBackVia && via !== lastBackVia && (now - lastBackAt) < ECHO_MS) return;
    lastBackAt = now;
    lastBackVia = via;
    back();
  }

  // Our own history entry, marked so it can be told from the page's first one
  // and never stacked twice.
  function trapArmed() { return !!(history.state && history.state.vault); }

  function armTrap() {
    if (!window.history || !history.pushState) return false;
    if (trapArmed()) return true;
    try { history.pushState({ vault: 1 }, ''); return true; }
    catch (e) { return false; }
  }

  function armBackTrap() {
    if (!armTrap()) return;   // no history API: the key handler alone covers it

    window.addEventListener('popstate', function () {
      // Re-armed first and unconditionally — before deciding what the press
      // meant, and even on the library where Back does nothing. The moment
      // there is no entry of ours left, the next press leaves the page. A tick
      // later, because pushing synchronously from inside popstate is the
      // fragile case on older engines.
      setTimeout(armTrap, 0);
      handleBack(VIA_POP);
    });

    // Chromium 79 and later — webOS 6.0, so 2021 sets onward — treats a history
    // entry as skippable when the document that pushed it has never had a user
    // gesture, and Back jumps straight over such an entry WITHOUT firing
    // popstate. This trap is armed during boot, before anyone has touched
    // anything, so on those sets it would be stepped over and Back would leave
    // the app after all. A gesture anywhere clears the flag for entries already
    // pushed, so the first real press seals it. Back and Escape do not count as
    // activation, and are skipped so the sealing press cannot be the very press
    // that needed the trap.
    var sealed = false;
    function seal(e) {
      if (sealed) return;
      var c = e.keyCode || 0;
      if (e.type === 'keydown' && (c === 27 || BACK.indexOf(c) >= 0)) return;
      sealed = true;
      document.removeEventListener('keydown', seal, true);
      document.removeEventListener('click', seal, true);
      armTrap();
    }
    document.addEventListener('keydown', seal, true);
    document.addEventListener('click', seal, true);
  }

  function back() {
    // A menu is the innermost thing on screen, so it is the first thing Back
    // should take away.
    if (closeMenu()) return;
    if (S.screen === 'player') { stopPlayback(); return; }
    if (S.screen === 'detail') { show('library'); return; }
    if (S.screen === 'shelves') { show('detail'); return; }
    if (S.screen === 'newshelf') { show('shelves'); return; }
    // Library and sign-in are the floor. Doing nothing is deliberate — letting
    // Back bubble exits the app entirely on several platforms.
  }

  // ---- server -----------------------------------------------------------
  function api(path, opts) {
    opts = opts || {};
    opts.headers = opts.headers || {};
    if (S.access) opts.headers['Authorization'] = 'Bearer ' + S.access;
    if (opts.body) opts.headers['Content-Type'] = 'application/json';
    return fetch(path, opts).then(function (r) {
      if (r.status !== 401 || !S.refresh) return r;
      return refresh().then(function (ok) {
        if (!ok) throw new Error('signed-out');
        opts.headers['Authorization'] = 'Bearer ' + S.access;
        return fetch(path, opts);
      });
    });
  }
  function storeSession(j) {
    S.access = j.accessToken;
    S.refresh = j.refreshToken;   // single use: persist the NEW one immediately
    S.me = j;
    try { localStorage.setItem(LS, j.refreshToken); } catch (e) { S.memoryOnly = true; }
  }
  function refresh() {
    return fetch('/api/v1/refresh', {
      method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ refreshToken: S.refresh })
    }).then(function (r) {
      if (!r.ok) { signOut(); return false; }
      return r.json().then(function (j) { storeSession(j); return true; });
    })['catch'](function () { return false; });
  }
  function signOut() {
    // A session can end while a film is on screen — a password reset revokes
    // the refresh token, and the next 401 lands here. Hiding the player without
    // stopping it leaves a film playing that no key can reach, because every
    // transport key gates on S.screen, which is now 'signin'.
    if (S.screen === 'player') stopPlayback();

    // Tell the server too. Clearing local storage alone leaves the refresh
    // token valid for its full sixty days, so a television handed on or sold
    // could be signed back in from its own storage; and the person pressing
    // this reasonably believes the set has been disconnected from their account.
    if (S.refresh) {
      try {
        fetch('/api/v1/logout', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken: S.refresh })
        })['catch'](function () {});
      } catch (e) {}
    }

    S.access = S.refresh = S.me = null;
    S.items = []; S.collections = []; S.shelf = null; S.resume = {};
    try { localStorage.removeItem(LS); } catch (e) {}
    entry = ''; renderEntry();
    show('signin');
  }

  // ---- sign in ----------------------------------------------------------
  function renderEntry() {
    var out = '';
    for (var i = 0; i < CODE_LEN; i++) out += entry.charAt(i) ? entry.charAt(i) : '<span class="ph">&middot;</span>';
    $('entry').innerHTML = out;
  }
  function pushChar(ch) {
    if (entry.length >= CODE_LEN) return;
    entry += ch; renderEntry();
    if (entry.length === CODE_LEN) claim(0);
  }
  function buildKeys() {
    var host = $('keys'), html = '', first = true;
    for (var r = 0; r < ROWS.length; r++) {
      // Each row is its own block, so Up and Down move between rows rather than
      // wherever the reflow happened to put the next key.
      html += '<div class="krow">';
      for (var i = 0; i < ROWS[r].length; i++) {
        var k = ROWS[r][i];
        if (typeof k === 'string') {
          html += '<button class="key" data-f' + (first ? ' data-first' : '') +
                  ' data-ch="' + k + '">' + k + '</button>';
          first = false;
        } else {
          // Same width as a digit so the columns line up; smaller text so the
          // word fits the key rather than the key stretching to fit the word.
          html += '<button class="key act" data-f data-act="' + k.act + '">' + k.label + '</button>';
        }
      }
      html += '</div>';
    }
    host.innerHTML = html;
    // The buttons are now nested a level deeper, so wire them by query rather
    // than by walking the host's immediate children.
    var keys = host.getElementsByTagName('button');
    for (var j = 0; j < keys.length; j++) {
      (function (b) {
        b.addEventListener('click', function () {
          var act = b.getAttribute('data-act');
          if (act === 'del') { entry = entry.slice(0, -1); renderEntry(); }
          else if (act === 'go') claim(0);
          else pushChar(b.getAttribute('data-ch'));
        });
      })(keys[j]);
    }
  }
  // The claim limiter is global and counts failures server-wide, so a device
  // holding a perfectly good code can be refused. Hold the code and retry
  // rather than sending the person back to their phone for nothing.
  var RETRY = [2000, 4000, 8000, 15000, 15000, 15000];
  function claim(attempt) {
    if (entry.length !== CODE_LEN) {
      $('signinErr').textContent = 'Enter all ' + CODE_LEN + ' digits.'; return;
    }
    $('signinErr').textContent = attempt ? 'The server is busy — still trying…' : 'Signing in…';
    fetch('/api/v1/link/claim', {
      method:'POST', headers:{'Content-Type':'application/json'},
      body: JSON.stringify({ code: entry })
    }).then(function (r) {
      return r.json()['catch'](function () { return {}; }).then(function (j) {
        if (r.status === 429) {
          if (attempt < RETRY.length) { setTimeout(function () { claim(attempt + 1); }, RETRY[attempt]); }
          else $('signinErr').textContent = 'The server is busy. Press Sign in to try again.';
          return;
        }
        if (r.status === 403) { $('signinErr').textContent = j.error || 'This account can\'t sign in. Ask the owner.'; entry=''; renderEntry(); return; }
        if (!r.ok) {
          $('signinErr').textContent = j.error || 'That code has already been used, or it expired. Get a fresh one on your phone.';
          entry = ''; renderEntry(); return;
        }
        storeSession(j); $('signinErr').textContent = ''; enter();
      });
    })['catch'](function () { $('signinErr').textContent = 'Could not reach the server.'; });
  }

  // ---- gate (pending / blocked) ------------------------------------------
  function showGate(msg) {
    $('gateMsg').textContent = msg + (S.memoryOnly
      ? '  This TV can\'t remember the sign-in, so you\'ll need to do it again next time.' : '');
    var row = $('gateRow'); row.innerHTML = '';
    row.appendChild(mkBtn('Check again', function () { enter(); }, true));
    row.appendChild(mkBtn('Sign out of this TV', signOut, false, false, true));
    show('gate');
    if (gateTimer) clearInterval(gateTimer);
    gateTimer = setInterval(function () { if (S.screen === 'gate') enter(true); }, 30000);
  }
  var gateTimer = null;

  // ---- library ----------------------------------------------------------
  // Approval is decided by the CATALOG, never by refresh: refresh performs no
  // status check, so a blocked account refreshes 200 forever.
  function enter(quiet) {
    if (!quiet) { show('library'); $('grid').innerHTML = '<div class="center">Loading your library…</div>'; }
    renderWho();
    api('/api/v1/catalog').then(function (r) {
      return r.json()['catch'](function () { return {}; }).then(function (j) {
        if (r.status === 403) { showGate(j.error || 'Your account is waiting for the owner\'s approval.'); return null; }
        if (!r.ok) {
          centerMessage(j.error || 'The library is not available.', 'Try again', function () { enter(true); });
          return null;
        }
        if (gateTimer) { clearInterval(gateTimer); gateTimer = null; }
        if (S.screen !== 'library') show('library');
        return j;
      });
    }).then(function (cat) {
      if (!cat) return;
      S.items = cat.items || [];
      // Shelves ride along with the catalogue, so this is one round trip.
      // An older relay omits the field entirely; treat that as "none".
      S.collections = cat.collections || [];
      // A shelf held from a previous refresh may have been renamed or deleted
      // on another device. Re-point it by id, and fall back to the whole
      // library rather than filtering against something that no longer exists.
      if (S.shelf) {
        var still = null;
        for (var c = 0; c < S.collections.length; c++)
          if (S.collections[c].id === S.shelf.id) { still = S.collections[c]; break; }
        S.shelf = still;
      }
      renderRail();
      return api('/api/v1/library').then(function (r) { return r.ok ? r.json() : []; })
        ['catch'](function () { return []; })
        .then(function (lib) {
          for (var i = 0; i < (lib || []).length; i++) S.resume[lib[i].filmKey] = lib[i];
          renderGrid(cat);
        });
    })['catch'](function () {
      centerMessage('Could not reach the server.', 'Try again', function () { enter(true); });
    });
  }

  // ---- collections ------------------------------------------------------

  // The films the chosen shelf holds, or everything. Membership arrives as
  // FilmKeys and is matched against the catalogue here rather than being
  // resolved server-side, so one cached /catalog response serves every shelf —
  // that endpoint mints a signed media token per film on every call, and
  // re-fetching it per shelf would re-sign the whole library on each keypress.
  function visibleItems() {
    if (!S.shelf) return S.items;
    var keys = {};
    for (var i = 0; i < S.shelf.items.length; i++) keys[S.shelf.items[i].filmKey] = true;
    var out = [];
    for (var j = 0; j < S.items.length; j++) if (keys[S.items[j].filmKey]) out.push(S.items[j]);
    return out;
  }

  // Who is signed in, and the only way to stop being signed in. Without this
  // the sole signOut control lives on the approval gate, which an approved
  // account never sees again — so a set stays bound to whoever linked it first,
  // with no way to switch accounts or hand the television on.
  function renderWho() {
    var who = $('who');
    who.innerHTML = '';
    if (!S.me) return;
    // The name IS the button. A caret says it opens onto something, which is
    // the only way a viewer would know to press it.
    var b = document.createElement('button');
    b.className = 'chip';
    b.setAttribute('data-f', '');
    b.appendChild(document.createTextNode(S.me.displayName));
    var caret = document.createElement('span');
    caret.className = 'caret';
    caret.innerHTML = '&#9662;';
    b.appendChild(caret);
    b.addEventListener('click', function () {
      openMenu(b, [
        // Nothing behind it yet, so it is shown and disabled rather than
        // hidden — the menu keeps its shape when settings arrive.
        { label: 'Settings', disabled: true },
        { label: 'Sign out', onSelect: signOut }
      ]);
    });
    who.appendChild(b);
  }

  function renderRail() {
    var rail = $('rail');
    rail.innerHTML = '';
    if (!S.collections.length) { rail.className = 'rail'; return; }
    rail.className = 'rail on';

    function chip(label, shelf) {
      var b = document.createElement('button');
      b.className = 'chip' + (S.shelf === shelf ? ' sel' : '');
      b.setAttribute('data-f', '');
      b.textContent = label;
      b.addEventListener('click', function () {
        S.shelf = shelf;
        // Redraw both, then put focus back on this chip. renderGrid forces
        // focus to the first card, which would snatch it away mid-browse and
        // read as the remote fighting you.
        renderRail();
        renderGrid(null, true);
        var chips = rail.getElementsByClassName('chip');
        for (var k = 0; k < chips.length; k++)
          if (chips[k].textContent === label) { setFocus(chips[k]); break; }
      });
      return b;
    }

    rail.appendChild(chip('All films', null));
    for (var i = 0; i < S.collections.length; i++) {
      var c = S.collections[i];
      rail.appendChild(chip(c.name + '  ' + c.items.length, c));
    }
    rail.appendChild(layoutChip());
  }

  // A grid of squares drawn to match the layout it will switch to, so the
  // control shows the current choice rather than naming it. Sits at the right
  // end of the shelf row, away from the shelves it has nothing to do with.
  function gridGlyph(cols) {
    var out = '';
    for (var r = 0; r < 2; r++) {
      for (var c = 0; c < cols; c++) out += '<i></i>';
      out += '<br>';
    }
    return '<span class="gg gg' + cols + '">' + out + '</span>';
  }

  function layoutChip() {
    var b = document.createElement('button');
    b.className = 'chip layout';
    b.setAttribute('data-f', '');
    b.innerHTML = gridGlyph(S.cols);
    var caret = document.createElement('span');
    caret.className = 'caret';
    caret.innerHTML = '&#9662;';
    b.appendChild(caret);
    b.addEventListener('click', function () { openLayoutMenu(b); });
    return b;
  }

  var COL_NOTES = { 3: 'Largest', 4: 'Recommended', 5: 'More at once', 6: 'Most at once' };

  function openLayoutMenu(anchor) {
    var rows = [];
    for (var i = 0; i < COLS.length; i++) {
      (function (n) {
        rows.push({
          tile: true,
          selected: n === S.cols,
          html: gridGlyph(n),
          label: n + ' across',
          sub: COL_NOTES[n],
          onSelect: function () { setCols(n); }
        });
      })(COLS[i]);
    }
    openMenu(anchor, rows);
  }

  function setCols(n) {
    S.cols = n;
    try { localStorage.setItem(LS_COLS, String(n)); } catch (e) {}
    applyCols();
    renderRail();
    renderGrid(null, true);
    // Put focus back on the control that was just used, not into the grid.
    var chips = $('rail').getElementsByClassName('layout');
    if (chips.length) setFocus(chips[0]);
  }

  function applyCols() {
    var g = $('grid');
    g.className = 'grid cols' + S.cols;
  }

  // Every terminal screen in the library must leave at least one thing to
  // press. Back is deliberately a no-op here — letting it bubble closes the
  // whole app on webOS and Tizen — so a screen with no [data-f] is a set that
  // has to be power-cycled to escape. This is the only way to draw one.
  function centerMessage(text, label, fn) {
    var g = $('grid');
    g.innerHTML = '';
    var msg = document.createElement('div');
    msg.className = 'center';
    msg.textContent = text;
    g.appendChild(msg);
    var b = document.createElement('button');
    b.className = 'btn';
    b.setAttribute('data-f', '');
    b.setAttribute('data-first', '');
    b.style.display = 'block';
    b.style.margin = '1.6rem auto 0';
    b.textContent = label;
    b.addEventListener('click', fn);
    g.appendChild(b);
    var out = document.createElement('button');
    out.className = 'btn ghost';
    out.setAttribute('data-f', '');
    out.style.display = 'block';
    out.style.margin = '.8rem auto 0';
    out.textContent = 'Sign out of this TV';
    out.addEventListener('click', signOut);
    g.appendChild(out);
    focusFirst(true);
  }

  function renderGrid(cat, keepFocus) {
    var g = $('grid');
    if (!S.items.length) {
      // A library still being indexed is not a permanent state, so this offers
      // the re-check that nothing else would ever perform — once the grid is
      // painted, nothing re-enters enter() on its own.
      centerMessage(
        cat && cat.scanning ? 'Still indexing the library…' : 'No films on the server yet.',
        cat && cat.scanning ? 'Check again' : 'Refresh',
        function () { enter(true); });
      return;
    }

    var items = visibleItems();
    if (!items.length) {
      // An empty shelf must still leave something to press. The library screen
      // deliberately swallows Back — letting it bubble closes the whole app on
      // webOS and Tizen — so a screen with nothing focusable is a set that has
      // to be power-cycled.
      g.innerHTML = '';
      var msg = document.createElement('div');
      msg.className = 'center';
      msg.textContent = 'Nothing on this shelf yet.';
      g.appendChild(msg);
      var out = document.createElement('button');
      out.className = 'btn';
      out.setAttribute('data-f', '');
      out.setAttribute('data-first', '');
      out.style.display = 'block';
      out.style.margin = '1.6rem auto 0';
      out.textContent = 'Show all films';
      out.addEventListener('click', function () {
        S.shelf = null; renderRail(); renderGrid(null);
      });
      g.appendChild(out);
      if (!keepFocus) focusFirst(true);
      return;
    }

    applyCols();
    g.innerHTML = '';
    for (var i = 0; i < items.length; i++) {
      (function (it, idx) {
        var seen = S.resume[it.filmKey];
        var pct = (seen && it.durationMs) ? Math.min(100, seen.resumeMs / it.durationMs * 100) : 0;
        var b = document.createElement('button');
        b.className = 'card';
        b.setAttribute('data-f', '');
        if (idx === 0) b.setAttribute('data-first', '');
        b.innerHTML =
          '<div class="thumb"><span class="glyph">&#127916;</span>' +
            (routeFor(it) ? '' : '<span class="badge">APP ONLY</span>') +
            // The number the remote's digits jump to. Position in the list as
            // shown, so it follows the shelf and the sort rather than being an
            // id the viewer would have to learn.
            '<span class="idx">' + (idx + 1) + '</span>' +
            (pct > 1 ? '<span class="resume"><i style="width:' + pct + '%"></i></span>' : '') +
          '</div><div class="name"></div><div class="meta">' +
          (it.durationMs ? fmt(it.durationMs) : '') + '</div>';
        b.getElementsByClassName('name')[0].textContent = it.name;
        b.addEventListener('click', function () { openDetail(it); });
        g.appendChild(b);
        if (it.artUrl) loadArt(b.getElementsByClassName('thumb')[0], it.artUrl);
      })(items[i], i);
    }
    if (!keepFocus) focusFirst(true);
  }

  // Posters need an Authorization header and an <img> cannot send one, so fetch
  // the bytes and hand the element an object URL instead.
  //
  // Cached by URL, and deliberately never revoked. Before the collections rail
  // the grid was drawn about twice in a session, so fetching every poster afresh
  // each time cost nothing; now a shelf can be picked with the remote and the
  // grid redraws on a keypress. Without this, every press would re-fetch the
  // whole library over an authenticated connection and mint a fresh blob for
  // every card, none ever released — on a television with a few hundred
  // megabytes to play with, that is a browser that dies after a few minutes of
  // browsing. One blob per film for the life of the page is the whole cost.
  var artCache = {};
  function loadArt(el, url) {
    var gl = el.getElementsByClassName('glyph')[0];
    if (artCache[url]) {
      el.style.backgroundImage = 'url("' + artCache[url] + '")';
      if (gl) gl.style.display = 'none';
      return;
    }
    api(url).then(function (r) { return r.ok ? r.blob() : null; }).then(function (b) {
      if (!b) return;
      artCache[url] = URL.createObjectURL(b);
      el.style.backgroundImage = 'url("' + artCache[url] + '")';
      if (gl) gl.style.display = 'none';
    })['catch'](function () {});
  }

  // ---- detail -----------------------------------------------------------
  function openDetail(it) {
    S.film = it;
    $('dTitle').textContent = it.name;

    // Reuse the cached poster — the grid has already fetched it, so this is
    // instant and costs no second request.
    var art = $('dArt');
    art.style.backgroundImage = '';
    var gl = art.getElementsByClassName('glyph')[0];
    if (gl) gl.style.display = '';
    if (it.artUrl) loadArt(art, it.artUrl);
    var bits = [];
    if (it.durationMs) bits.push(fmt(it.durationMs));
    if (it.videoCodec) bits.push(String(it.videoCodec).toUpperCase());
    if (it.audioCodec) bits.push(String(it.audioCodec).toUpperCase());
    if (it.sizeBytes) bits.push((it.sizeBytes / 1073741824).toFixed(1) + ' GB');
    $('dMeta').textContent = bits.join('   ·   ');

    var route = routeFor(it);
    var mkv = it.contentType && it.contentType.indexOf('matroska') >= 0;
    $('dWarn').textContent =
      route === 'direct' ? '' :
      // The server can fix this one, so say what will happen rather than warn.
      route === 'hls' ?
        (mkv ? 'This is an MKV, which no television browser opens. '
             : 'This film needs repackaging before it will play here. ') +
        'The server prepares it the first time you press Play; after that it starts straight away.' :
      // Nothing to be done from here — explain which of the three walls was hit.
      (!it.hlsAvailable
         ? 'This film\'s video can\'t be repackaged for a browser.'
         : !CAN.hls
           ? 'This television\'s browser can\'t play a streamed film.'
           : 'This television won\'t decode ' +
             String(it.hlsVideoCodec || 'this format').toUpperCase() + ' video.') +
      ' Watch it in the Vault Movies desktop app, which plays anything.';

    var seen = S.resume[it.filmKey];
    var canResume = !!route && seen && seen.resumeMs > 15000 &&
                    (!it.durationMs || seen.resumeMs < it.durationMs - 30000);
    var row = $('dRow'); row.innerHTML = '';
    if (canResume) row.appendChild(mkBtn('Resume ' + fmt(seen.resumeMs), function () { play(it, seen.resumeMs); }, true));
    row.appendChild(mkBtn(canResume ? 'Start again' : 'Play', function () { play(it, 0); }, !canResume, !route));
    // Only worth offering once the catalogue has answered — a shelf needs a
    // film key to hold, and the server is what supplies it.
    row.appendChild(mkBtn('Add to Collection', openShelves, false, false, true));
    row.appendChild(mkBtn('Back', function () { show('library'); }, false, false, true));
    show('detail');
  }
  function mkBtn(label, fn, first, disabled, ghost) {
    var b = document.createElement('button');
    b.className = 'btn' + (ghost ? ' ghost' : '');
    b.textContent = label;
    if (!disabled) { b.setAttribute('data-f',''); if (first) b.setAttribute('data-first',''); }
    b.disabled = !!disabled;
    b.addEventListener('click', fn);
    return b;
  }

  // ---- dropdowns --------------------------------------------------------
  //
  // A menu on a television has to be escapable, has to hold focus while it is
  // open, and has to give focus back to whatever opened it. Nothing else here
  // needs a menu, so this is deliberately one small thing rather than a
  // framework: a list of rows, an anchor to sit under, and a way out.

  var menuOwner = null;

  function openMenu(anchor, rows) {
    var m = $('menu');
    m.innerHTML = '';
    for (var i = 0; i < rows.length; i++) {
      (function (row, first) {
        var b = document.createElement('button');
        b.className = 'mrow' + (row.tile ? ' tile' : '') + (row.selected ? ' sel' : '');
        if (row.disabled) {
          b.disabled = true;
        } else {
          b.setAttribute('data-f', '');
          if (first) b.setAttribute('data-first', '');
        }
        b.innerHTML = row.html || '';
        if (row.label) {
          var lab = document.createElement('span');
          lab.className = 'lab';
          lab.textContent = row.label;
          b.appendChild(lab);
        }
        if (row.sub) {
          var sub = document.createElement('span');
          sub.className = 'sub2';
          sub.textContent = row.sub;
          b.appendChild(sub);
        }
        if (!row.disabled) {
          b.addEventListener('click', function () {
            closeMenu();
            if (row.onSelect) row.onSelect();
          });
        }
        m.appendChild(b);
      })(rows[i], i === 0 || (i === 1 && rows[0].disabled));
    }

    m.className = 'menu on';
    // Positioned after it is visible, because until then it has no size to
    // measure and would be placed against zero.
    var a = anchor.getBoundingClientRect(), mr = m.getBoundingClientRect();
    var left = Math.min(a.left, window.innerWidth - mr.width - 16);
    var top = a.bottom + 8;
    if (top + mr.height > window.innerHeight - 8) top = Math.max(8, a.top - mr.height - 8);
    m.style.left = Math.max(8, left) + 'px';
    m.style.top = top + 'px';

    menuOwner = anchor;
    focusFirst(true);
  }

  /// Returns true if there was a menu to close, so Back can stop there.
  function closeMenu() {
    var m = $('menu');
    if (m.className.indexOf('on') < 0) return false;
    m.className = 'menu';
    m.innerHTML = '';
    // Focus goes back to whatever opened it, or the viewer is dropped somewhere
    // they did not choose.
    var owner = menuOwner;
    menuOwner = null;
    if (owner && owner.offsetParent !== null) setFocus(owner);
    else focusFirst(true);
    return true;
  }

  // ---- jumping to a film by number --------------------------------------
  //
  // Type the number printed in a card's corner and the highlight goes there.
  // Digits accumulate while they keep coming, so 1 then 4 reaches 14 and a
  // third digit reaches 142 — the highlight follows every press rather than
  // waiting, so the search is visibly narrowing as you type. A pause of a
  // second and a half ends that number; the next digit starts a fresh one.
  //
  // It only ever moves the highlight. Opening a film stays a deliberate press
  // of OK, so a mistyped digit costs nothing but another press.

  var JUMP_GAP = 1500;
  var jumpBuf = '', jumpTimer = null;

  function jumpDigit(k, c) {
    var ch = (k && k.length === 1) ? k : String.fromCharCode(c);
    if (ch < '0' || ch > '9') return false;

    var cards = $('grid').getElementsByClassName('card');
    if (!cards.length) return false;

    var next = jumpBuf + ch;
    // A number no film could carry starts again from this digit, rather than
    // leaving somebody typing into something that can never match.
    if (parseInt(next, 10) > cards.length) next = ch;
    jumpBuf = next;

    var n = parseInt(jumpBuf, 10);
    if (n >= 1 && n <= cards.length) setFocus(cards[n - 1]);

    $('jump').textContent = jumpBuf;
    $('jump').className = 'jump on';

    clearTimeout(jumpTimer);
    jumpTimer = setTimeout(function () {
      jumpBuf = '';
      $('jump').className = 'jump';
    }, JUMP_GAP);
    return true;
  }

  // ---- collections ------------------------------------------------------
  //
  // Shelves can be made and filled from the television, not only the desktop.
  // The API is the same one the desktop uses; the only thing the set needs of
  // its own is a way to type a name, since a collection is called something
  // rather than numbered.

  function openShelves() {
    if (!S.film) return;
    $('shTitle').textContent = S.film.name;
    renderShelves();
    show('shelves');
  }

  function onShelf(shelf, film) {
    for (var i = 0; i < shelf.items.length; i++)
      if (shelf.items[i].filmKey === film.filmKey) return true;
    return false;
  }

  function renderShelves(keepIndex) {
    var host = $('shList'), film = S.film;
    host.innerHTML = '';

    for (var i = 0; i < S.collections.length; i++) {
      (function (shelf, first, index) {
        var on = onShelf(shelf, film);
        var b = document.createElement('button');
        b.className = 'shrow';
        b.setAttribute('data-f', '');
        if (first) b.setAttribute('data-first', '');
        // A tick that holds its column whether or not it is showing, so the
        // names stay in line and the list does not jump as things are toggled.
        b.innerHTML = '<span class="tick">' + (on ? '&#10003;' : '&nbsp;&nbsp;') + '</span>';
        b.appendChild(document.createTextNode(shelf.name + '  (' + shelf.items.length + ')'));
        b.addEventListener('click', function () { toggleShelf(shelf, on, index); });
        host.appendChild(b);
      })(S.collections[i], i === 0, i);
    }

    var mk = document.createElement('button');
    mk.className = 'shrow new';
    mk.setAttribute('data-f', '');
    if (!S.collections.length) mk.setAttribute('data-first', '');
    mk.textContent = '+  New collection';
    mk.addEventListener('click', function () { openNewShelf(); });
    host.appendChild(mk);

    // The Back key does this too, but a remote's Back is not something every
    // viewer trusts, and nothing else on this screen said how to leave it.
    var bk = document.createElement('button');
    bk.className = 'shrow back';
    bk.setAttribute('data-f', '');
    bk.textContent = 'Back';
    bk.addEventListener('click', function () { show('detail'); });
    host.appendChild(bk);

    // Toggling a shelf redraws this list, and redrawing used to send focus back
    // to the top — so ticking the third shelf bounced you to the first. Put it
    // back where it was.
    var rows = host.getElementsByClassName('shrow');
    if (typeof keepIndex === 'number' && rows[keepIndex]) setFocus(rows[keepIndex]);
    else focusFirst(true);
  }

  function toggleShelf(shelf, wasOn, idx) {
    var path = '/api/v1/collections/' + shelf.id + '/items/' + (wasOn ? 'remove' : 'add');
    shelfNote('');
    api(path, { method: 'POST', body: JSON.stringify({ filmKey: S.film.filmKey }) })
      .then(function (r) {
        return r.json()['catch'](function () { return null; }).then(function (j) {
          if (!r.ok) {
            // Every failure here used to be silent: a bad response returned
            // null and the catch swallowed the rest, so a film that would not
            // go on a shelf looked exactly like a button that did nothing.
            shelfNote((j && j.error) || ('The server refused that (' + r.status + ').'));
            return null;
          }
          return j;
        });
      })
      .then(function (updated) {
        if (!updated) return;
        // Swap the server's answer in rather than guessing what it did, so the
        // count and the tick can never drift from what is actually stored.
        for (var i = 0; i < S.collections.length; i++)
          if (S.collections[i].id === updated.id) S.collections[i] = updated;
        renderShelves(idx);
        renderRail();
        // Putting a film on a shelf is a finished errand, so it goes back to
        // the film. Taking one off is usually part of tidying up, so that stays
        // put and the list can be worked through.
        if (!wasOn) show('detail');
      })['catch'](function (e) {
        shelfNote('Could not reach the server. ' + (e && e.message ? e.message : ''));
      });
  }

  function shelfNote(text) {
    var el = $('shErr');
    if (el) el.textContent = text || '';
  }

  // ---- naming a new one --------------------------------------------------

  var NAME_ROWS = ['QWERTYUIOP', 'ASDFGHJKL', 'ZXCVBNM'];
  var newName = '';

  function openNewShelf() {
    newName = '';
    $('nameErr').textContent = '';
    renderName();
    buildNameKeys();
    show('newshelf');
  }

  function renderName() {
    $('nameEntry').textContent = newName || ' ';
  }

  function buildNameKeys() {
    var host = $('nameKeys');
    if (host.getElementsByTagName('button').length) return;   // built once
    var html = '';
    for (var r = 0; r < NAME_ROWS.length; r++) {
      html += '<div class="krow">';
      for (var i = 0; i < NAME_ROWS[r].length; i++) {
        var ch = NAME_ROWS[r].charAt(i);
        html += '<button class="key" data-f' + (r === 0 && i === 0 ? ' data-first' : '') +
                ' data-ch="' + ch + '">' + ch + '</button>';
      }
      if (r === 0) html += '<button class="key act" data-f data-act="del">Del</button>';
      if (r === 1) html += '<button class="key act" data-f data-act="go">Save</button>';
      html += '</div>';
    }
    // A spacebar along the bottom, the width of the rows above it, because that
    // is where every keyboard puts one and it is the easiest key to hit with a
    // remote when it is the only thing on its row.
    html += '<div class="krow"><button class="key act space" data-f data-act="sp">Space</button></div>';

    host.innerHTML = html;
    var keys = host.getElementsByTagName('button');
    for (var j = 0; j < keys.length; j++) {
      (function (b) {
        b.addEventListener('click', function () {
          var act = b.getAttribute('data-act');
          if (act === 'del') { newName = newName.slice(0, -1); renderName(); }
          else if (act === 'sp') { if (newName.length && newName.length < 40) { newName += ' '; renderName(); } }
          else if (act === 'go') { saveNewShelf(); }
          else if (newName.length < 40) {
            // Typed in capitals because that is what the keys show, but stored
            // the way a person writes a name.
            newName += newName.length ? b.getAttribute('data-ch').toLowerCase()
                                      : b.getAttribute('data-ch');
            renderName();
          }
        });
      })(keys[j]);
    }
  }

  function saveNewShelf() {
    var name = newName.replace(/^\s+|\s+$/g, '');
    if (!name) { $('nameErr').textContent = 'Give it a name first.'; return; }
    $('nameErr').textContent = 'Saving…';
    api('/api/v1/collections', { method: 'POST', body: JSON.stringify({ name: name }) })
      .then(function (r) {
        return r.json()['catch'](function () { return null; }).then(function (j) {
          if (!r.ok) { $('nameErr').textContent = (j && j.error) || 'Could not create that.'; return; }
          $('nameErr').textContent = '';
          S.collections.push(j);
          // Put the film on it straight away — making a shelf from a film's own
          // page can only mean one thing.
          return api('/api/v1/collections/' + j.id + '/items/add',
                     { method: 'POST', body: JSON.stringify({ filmKey: S.film.filmKey }) })
            .then(function (r2) { return r2.ok ? r2.json() : null; })
            .then(function (updated) {
              if (updated) {
                for (var i = 0; i < S.collections.length; i++)
                  if (S.collections[i].id === updated.id) S.collections[i] = updated;
              }
              renderRail();
              renderShelves();
              // The film is on the new shelf; there is nothing further to do
              // here, so return to it rather than to a list.
              show('detail');
            });
        });
      })['catch'](function () { $('nameErr').textContent = 'Could not reach the server.'; });
  }

  // ---- player -----------------------------------------------------------
  var v = $('v'), hudTimer = null, saveTimer = null;
  // Whether this film actually started, and the pending seek-on-load.
  var began = false, seekOnLoad = null;
  var prepTimer = null;
  function play(it, startMs) {
    S.film = it;
    show('player');
    $('pTitle').textContent = it.name;
    // Relative URLs throughout; the signed token rides in the query string,
    // because a <video> attaches no headers of its own.
    if (routeFor(it) === 'hls') prepare(it, startMs);
    else begin(it.streamUrl, startMs);
  }

  // Ask the server for the film's web copy and wait for it to be worth opening.
  // It reports ready well before it is finished — a viewer only needs the front
  // of the film to start, and the rest is written faster than it is watched.
  function prepare(it, startMs) {
    $('prep').className = 'on';
    $('prepPct').textContent = 'Starting…';
    // Reset the sub-line: a previous attempt may have left a failure on it.
    $('prep').getElementsByClassName('ps')[0].textContent =
      'This happens once. Next time it starts straight away.';
    poll();

    function poll() {
      api(it.hlsUrl).then(function (r) {
        return r.ok ? r.json() : r.json()['catch'](function () { return null; });
      }).then(function (j) {
        if (S.screen !== 'player' || S.film !== it) return;   // viewer left
        if (!j || j.error || j.state === 'failed' || j.state === 'unsupported') {
          $('prepPct').textContent = 'Couldn\'t prepare this film';
          $('prep').getElementsByClassName('ps')[0].textContent =
            'Press Back, and watch it in the Vault Movies desktop app instead.';
          return;
        }
        if (j.ready && j.playlistUrl) {
          $('prep').className = '';
          begin(j.playlistUrl, startMs);
          return;
        }
        $('prepPct').textContent = j.percent > 0 ? j.percent + '%' : 'Starting…';
        prepTimer = setTimeout(poll, 2000);
      })['catch'](function () {
        if (S.screen === 'player' && S.film === it) prepTimer = setTimeout(poll, 4000);
      });
    }
  }

  function begin(src, startMs) {
    v.src = src;
    began = true;
    // Held so it can be taken off again. A listener that only removes itself
    // when it fires survives a viewer who backs out before the film loads, and
    // then seeks the NEXT film to this one's position — which the periodic save
    // promptly writes back as that film's bookmark.
    seekOnLoad = function () {
      v.removeEventListener('loadedmetadata', seekOnLoad);
      seekOnLoad = null;
      if (startMs > 0) { try { v.currentTime = startMs / 1000; } catch (e) {} }
    };
    v.addEventListener('loadedmetadata', seekOnLoad);
    var p = v.play(); if (p && p['catch']) p['catch'](function () {});
    try { if (document.documentElement.requestFullscreen) document.documentElement.requestFullscreen(); } catch (e) {}
    bumpHud();
    saveTimer = setInterval(function () { pushResume(false); }, 20000);
  }
  function togglePlay() {
    if (v.paused) { var p = v.play(); if (p && p['catch']) p['catch'](function () {}); }
    else v.pause();
    bumpHud();
  }
  // How long the film runs, in seconds.
  //
  // Not simply v.duration: an HLS film still being prepared is served as an
  // EVENT playlist, and a player reads that as open-ended and answers Infinity.
  // Everything that needs a length — seeking, the scrub bar, saving a resume
  // point — would quietly switch itself off for exactly the films this route
  // exists to carry. The catalogue knows the real length, so fall back to it.
  function filmDuration() {
    if (isFinite(v.duration) && v.duration > 0) return v.duration;
    if (S.film && S.film.durationMs) return S.film.durationMs / 1000;
    return 0;
  }

  function seekBy(sec) {
    if (S.screen !== 'player') return;
    var d = filmDuration();
    if (!d) return;
    v.currentTime = Math.max(0, Math.min(d - 1, v.currentTime + sec));
    bumpHud();
  }
  function bumpHud() {
    $('hud').className = '';
    clearTimeout(hudTimer);
    hudTimer = setTimeout(function () { if (!v.paused) $('hud').className = 'hide'; }, 4000);
  }
  v.addEventListener('timeupdate', function () {
    var d = filmDuration();
    if (!d) return;
    $('pAt').textContent = fmt(v.currentTime * 1000);
    $('pLeft').textContent = '-' + fmt(Math.max(0, d - v.currentTime) * 1000);
    $('pBar').style.width = Math.min(100, v.currentTime / d * 100) + '%';
  });
  v.addEventListener('ended', function () { pushResume(true); stopPlayback(); });

  function pushResume(finished) {
    if (!S.film || !filmDuration()) return;
    var it = S.film, prev = S.resume[it.filmKey] || { watchCount:0 };
    var row = {
      filmKey: it.filmKey, name: it.name,
      resumeMs: finished ? 0 : Math.floor(v.currentTime * 1000),
      watchCount: prev.watchCount + (finished ? 1 : 0),
      lastWatchedAt: Date.now(),
      chaptersJson: prev.chaptersJson || null,
      updatedAt: Date.now()
    };
    S.resume[it.filmKey] = row;
    api('/api/v1/library', { method:'PUT', body: JSON.stringify({ items: [row] }) })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (merged) {
        if (!merged) return;
        for (var i = 0; i < merged.length; i++) S.resume[merged[i].filmKey] = merged[i];
      })['catch'](function () {});
  }
  function stopPlayback() {
    if (saveTimer) { clearInterval(saveTimer); saveTimer = null; }
    // Leaving while a film was still being prepared: stop asking. The remux
    // itself carries on server-side, so coming back finds it further along.
    if (prepTimer) { clearTimeout(prepTimer); prepTimer = null; }
    if (seekOnLoad) { v.removeEventListener('loadedmetadata', seekOnLoad); seekOnLoad = null; }
    $('prep').className = '';

    // Only write a bookmark if the film actually started. Backing out of the
    // "Preparing this film" screen leaves currentTime at 0 while the length is
    // known from the catalogue, so the guard inside pushResume is satisfied and
    // a perfectly good position an hour into a film gets overwritten with zero.
    // The failure screen tells the viewer to press Back, so this was the
    // expected way out of it.
    if (began) pushResume(false);
    began = false;

    v.pause(); v.removeAttribute('src'); v.load();
    try { if (document.exitFullscreen && document.fullscreenElement) document.exitFullscreen(); } catch (e) {}
    show('library');
    renderGrid(null);
  }

  // ---- boot -------------------------------------------------------------
  rescale();
  armBackTrap();
  buildKeys(); renderEntry();
  try { S.refresh = localStorage.getItem(LS); } catch (e) { S.memoryOnly = true; }
  if (S.refresh) refresh().then(function (ok) { if (ok) enter(); else show('signin'); });
  else show('signin');
})();
</script>
</body>
</html>
""";
}
