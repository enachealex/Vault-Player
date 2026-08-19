namespace VideoPlayer.Rendezvous;

/// <summary>
/// The television app, served at GET /tv and run in the TV's own browser.
///
/// Written for a remote, not a mouse: everything is reachable with a D-pad,
/// focus is always visible and always somewhere, and Back never strands anyone.
/// Sign-in never asks for a password — the phone does that (see
/// docs/TV-APP-INTEGRATION.md) and the television only takes a six-character code.
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

  h1 { font-size:2.6rem; margin:0 0 .6rem; font-weight:700; }
  .sub { color:var(--muted); font-size:1.35rem; margin:0 0 1.6rem; line-height:1.5; }

  /* Focus IS the interface here. It must never be subtle or absent. */
  [data-f] { outline:none; }
  [data-f].focus { box-shadow:0 0 0 .25rem var(--accent); }

  /* ---- sign in ---- */
  .url { font-size:2.6rem; font-weight:800; color:var(--warm); margin:.4rem 0 1.4rem; }
  .entry { font-family:Consolas,monospace; font-size:3.6rem; font-weight:800; color:var(--accent);
           letter-spacing:.5rem; min-height:4.4rem; }
  .entry .ph { color:var(--line); }
  .keys { max-width:62rem; }
  /* inline-block + margins, never flex gap */
  .key {
    display:inline-block; width:4.4rem; margin:0 .45rem .45rem 0; padding:.75rem 0;
    background:var(--surface); border:1px solid var(--line); border-radius:.45rem;
    font-size:1.5rem; font-weight:700; text-align:center; color:var(--text);
    font-family:inherit; vertical-align:top;
  }
  .key.wide { width:9.7rem; color:var(--warm); }
  .err { color:var(--accent); font-size:1.3rem; min-height:2rem; margin-top:1rem; }

  /* ---- library ---- */
  .bar { flex:none; margin-bottom:1.2rem; }
  .bar .who { float:right; color:var(--muted); font-size:1.2rem; padding-top:1rem; }
  .grid { flex:1; min-height:0; overflow-y:auto; overflow-x:hidden; }
  .grid::-webkit-scrollbar { display:none; }
  .card {
    display:inline-block; width:18.4%; margin:0 1.9% 1.8rem 0; vertical-align:top;
    background:transparent; border:none; padding:0; text-align:left; color:inherit;
    font-family:inherit;
  }
  .card:nth-child(5n) { margin-right:0; }
  /* padding-top is the portable 2:3 box; aspect-ratio is far too new */
  .thumb { position:relative; width:100%; padding-top:150%; background:var(--surface);
           border-radius:.5rem; background-size:cover; background-position:center; overflow:hidden; }
  .thumb .glyph { position:absolute; left:0; top:42%; width:100%; text-align:center;
                  font-size:3rem; color:var(--line); }
  .name { font-size:1.15rem; font-weight:600; margin-top:.55rem; line-height:1.3; height:3rem; overflow:hidden; }
  .meta { font-size:1rem; color:var(--muted); }
  .badge { position:absolute; left:.4rem; top:.4rem; background:rgba(14,6,4,.9); color:var(--warm);
           font-size:.85rem; padding:.25rem .5rem; border-radius:.35rem; font-weight:700; }
  .resume { position:absolute; left:0; right:0; bottom:0; height:.35rem; background:rgba(0,0,0,.55); }
  .resume i { display:block; height:100%; background:var(--accent); }

  /* ---- detail ---- */
  .dmeta { color:var(--muted); font-size:1.3rem; margin:.6rem 0; }
  .warn { color:var(--warm); font-size:1.25rem; margin:1rem 0; max-width:52rem; line-height:1.55; }
  .btn { display:inline-block; background:var(--accent); color:#fff; border:none; border-radius:.5rem;
         padding:1.1rem 2.6rem; font-size:1.35rem; font-weight:700; font-family:inherit; margin:1.4rem .7rem 0 0; }
  .btn.ghost { background:transparent; border:1px solid var(--line); color:var(--text); }
  .btn[disabled] { opacity:.4; }

  /* ---- player ---- */
  #player { padding:0; background:#000; }
  #player.on { position:relative; }
  video { width:100%; height:100%; background:#000; display:block; }
  #hud { position:absolute; left:0; right:0; bottom:0; padding:2rem 4rem 2.6rem;
         background:linear-gradient(transparent,rgba(14,6,4,.95)); transition:opacity .25s; }
  #hud.hide { opacity:0; }
  #hud .t { font-size:1.8rem; font-weight:700; }
  #hud .times { font-size:1.1rem; color:var(--muted); margin:.8rem 0 .5rem; }
  #hud .times .right { float:right; }
  .track { height:.4rem; background:rgba(255,255,255,.22); border-radius:.2rem; overflow:hidden; clear:both; }
  .track i { display:block; height:100%; background:var(--accent); }
  #hint { position:absolute; top:2.6rem; right:4rem; color:var(--muted); font-size:1.05rem; }
  .center { text-align:center; color:var(--muted); font-size:1.4rem; padding-top:6rem; }
</style>
</head>
<body>

<section id="signin" class="screen on"><div class="col">
  <h1>Sign in to Vault Movies</h1>
  <p class="sub">On your phone, open this page and sign in with your username <b>or email</b>.<br>It will show you a six-character code.</p>
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
  <div class="bar"><span class="who" id="who"></span><h1>Your library</h1></div>
  <div class="grid" id="grid"></div>
</div></section>

<section id="detail" class="screen"><div class="col">
  <h1 id="dTitle"></h1>
  <div class="dmeta" id="dMeta"></div>
  <div class="warn" id="dWarn"></div>
  <div id="dRow"></div>
</div></section>

<section id="player" class="screen">
  <video id="v" playsinline preload="auto"></video>
  <div id="hint">OK pause &middot; &#9664; &#9654; skip &middot; Back exit</div>
  <div id="hud">
    <div class="t" id="pTitle"></div>
    <div class="times"><span id="pAt">0:00</span><span class="right" id="pLeft"></span></div>
    <div class="track"><i id="pBar" style="width:0"></i></div>
  </div>
</section>

<script>
(function () {
  'use strict';
  var ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789'; // no I/L/O/0/1 — unreadable across a room
  var LS = 'vault.tv.rt';

  var S = { access:null, refresh:null, me:null, items:[], resume:{}, film:null, screen:'signin', memoryOnly:false };
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
    var all = ['signin','gate','library','detail','player'];
    for (var i = 0; i < all.length; i++) {
      var el = $(all[i]);
      if (all[i] === name) el.className = 'screen on'; else el.className = 'screen';
    }
    focusFirst(true);
  }

  // ---- focus ------------------------------------------------------------
  function focusables() {
    var out = [], all = document.querySelectorAll('.screen.on [data-f]');
    for (var i = 0; i < all.length; i++) if (all[i].offsetParent !== null && !all[i].disabled) out.push(all[i]);
    return out;
  }
  function current() { return document.querySelector('.screen.on [data-f].focus'); }
  function setFocus(el) {
    if (!el) return;
    var old = current();
    if (old) old.className = old.className.replace(/\s*focus/, '');
    el.className += ' focus';
    // scrollIntoView options need Chromium 61; keep the element visible by hand.
    var g = el.parentNode;
    while (g && g !== document.body && g.className.indexOf('grid') < 0) g = g.parentNode;
    if (g && g.className && g.className.indexOf('grid') >= 0) {
      var top = el.offsetTop, bot = top + el.offsetHeight;
      if (top < g.scrollTop) g.scrollTop = top - 16;
      else if (bot > g.scrollTop + g.clientHeight) g.scrollTop = bot - g.clientHeight + 16;
    }
  }
  function focusFirst(force) {
    if (!force && current()) return;
    var f = focusables(), pref = null;
    for (var i = 0; i < f.length; i++) if (f[i].getAttribute('data-first') !== null) { pref = f[i]; break; }
    var old = current(); if (old) old.className = old.className.replace(/\s*focus/, '');
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
    if (k === 'ArrowLeft' || c === 37) move(-1, 0);
    else if (k === 'ArrowRight' || c === 39) move(1, 0);
    else if (k === 'ArrowUp' || c === 38) move(0, -1);
    else if (k === 'ArrowDown' || c === 40) move(0, 1);
    else if (k === 'Enter' || c === 13) activate();
    else if (BACK.indexOf(c) >= 0 || k === 'Backspace' || k === 'Escape' || k === 'GoBack') back();
    else if (c === 415 || c === 19 || c === 179 || k === 'MediaPlayPause') { if (S.screen === 'player') togglePlay(); }
    else if (c === 417 || k === 'MediaFastForward') seekBy(30);
    else if (c === 412 || k === 'MediaRewind') seekBy(-30);
    else if (S.screen === 'signin' && typeText(k, c)) { /* physical keyboard */ }
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
  function back() {
    if (S.screen === 'player') { stopPlayback(); return; }
    if (S.screen === 'detail') { show('library'); return; }
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
    S.access = S.refresh = S.me = null;
    try { localStorage.removeItem(LS); } catch (e) {}
    entry = ''; renderEntry();
    show('signin');
  }

  // ---- sign in ----------------------------------------------------------
  function renderEntry() {
    var out = '';
    for (var i = 0; i < 6; i++) out += entry.charAt(i) ? entry.charAt(i) : '<span class="ph">&middot;</span>';
    $('entry').innerHTML = out;
  }
  function pushChar(ch) {
    if (entry.length >= 6) return;
    entry += ch; renderEntry();
    if (entry.length === 6) claim(0);
  }
  function buildKeys() {
    var host = $('keys'), html = '';
    for (var i = 0; i < ALPHABET.length; i++)
      html += '<button class="key" data-f' + (i === 0 ? ' data-first' : '') +
              ' data-ch="' + ALPHABET.charAt(i) + '">' + ALPHABET.charAt(i) + '</button>';
    html += '<button class="key wide" data-f data-act="del">Delete</button>';
    html += '<button class="key wide" data-f data-act="go">Sign in</button>';
    host.innerHTML = html;
    for (var j = 0; j < host.children.length; j++) {
      (function (b) {
        b.addEventListener('click', function () {
          var act = b.getAttribute('data-act');
          if (act === 'del') { entry = entry.slice(0, -1); renderEntry(); }
          else if (act === 'go') claim(0);
          else pushChar(b.getAttribute('data-ch'));
        });
      })(host.children[j]);
    }
  }
  // The claim limiter is global and counts failures server-wide, so a device
  // holding a perfectly good code can be refused. Hold the code and retry
  // rather than sending the person back to their phone for nothing.
  var RETRY = [2000, 4000, 8000, 15000, 15000, 15000];
  function claim(attempt) {
    if (entry.length !== 6) { $('signinErr').textContent = 'Enter all six characters.'; return; }
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
    $('who').textContent = S.me ? S.me.displayName : '';
    api('/api/v1/catalog').then(function (r) {
      return r.json()['catch'](function () { return {}; }).then(function (j) {
        if (r.status === 403) { showGate(j.error || 'Your account is waiting for the owner\'s approval.'); return null; }
        if (!r.ok) { $('grid').innerHTML = '<div class="center">' + (j.error || 'The library is not available.') + '</div>'; return null; }
        if (gateTimer) { clearInterval(gateTimer); gateTimer = null; }
        if (S.screen !== 'library') show('library');
        return j;
      });
    }).then(function (cat) {
      if (!cat) return;
      S.items = cat.items || [];
      return api('/api/v1/library').then(function (r) { return r.ok ? r.json() : []; })
        ['catch'](function () { return []; })
        .then(function (lib) {
          for (var i = 0; i < (lib || []).length; i++) S.resume[lib[i].filmKey] = lib[i];
          renderGrid(cat);
        });
    })['catch'](function () {
      $('grid').innerHTML = '<div class="center">Could not reach the server.</div>';
    });
  }

  function renderGrid(cat) {
    var g = $('grid');
    if (!S.items.length) {
      g.innerHTML = '<div class="center">' +
        (cat && cat.scanning ? 'Still indexing the library…' : 'No films on the server yet.') + '</div>';
      return;
    }
    g.innerHTML = '';
    for (var i = 0; i < S.items.length; i++) {
      (function (it, idx) {
        var seen = S.resume[it.filmKey];
        var pct = (seen && it.durationMs) ? Math.min(100, seen.resumeMs / it.durationMs * 100) : 0;
        var b = document.createElement('button');
        b.className = 'card';
        b.setAttribute('data-f', '');
        if (idx === 0) b.setAttribute('data-first', '');
        b.innerHTML =
          '<div class="thumb"><span class="glyph">&#127916;</span>' +
            (it.webPlayable ? '' : '<span class="badge">APP ONLY</span>') +
            (pct > 1 ? '<span class="resume"><i style="width:' + pct + '%"></i></span>' : '') +
          '</div><div class="name"></div><div class="meta">' +
          (it.durationMs ? fmt(it.durationMs) : '') + '</div>';
        b.getElementsByClassName('name')[0].textContent = it.name;
        b.addEventListener('click', function () { openDetail(it); });
        g.appendChild(b);
        if (it.artUrl) loadArt(b.getElementsByClassName('thumb')[0], it.artUrl);
      })(S.items[i], i);
    }
    focusFirst(true);
  }

  // Posters need an Authorization header and an <img> cannot send one, so fetch
  // the bytes and hand the element an object URL instead.
  function loadArt(el, url) {
    api(url).then(function (r) { return r.ok ? r.blob() : null; }).then(function (b) {
      if (!b) return;
      el.style.backgroundImage = 'url("' + URL.createObjectURL(b) + '")';
      var gl = el.getElementsByClassName('glyph')[0];
      if (gl) gl.style.display = 'none';
    })['catch'](function () {});
  }

  // ---- detail -----------------------------------------------------------
  function openDetail(it) {
    S.film = it;
    $('dTitle').textContent = it.name;
    var bits = [];
    if (it.durationMs) bits.push(fmt(it.durationMs));
    if (it.videoCodec) bits.push(String(it.videoCodec).toUpperCase());
    if (it.audioCodec) bits.push(String(it.audioCodec).toUpperCase());
    if (it.sizeBytes) bits.push((it.sizeBytes / 1073741824).toFixed(1) + ' GB');
    $('dMeta').textContent = bits.join('   ·   ');

    var mkv = it.contentType && it.contentType.indexOf('matroska') >= 0;
    $('dWarn').textContent = it.webPlayable ? '' :
      (mkv ? 'This is an MKV, which television browsers can\'t open.'
           : 'This television\'s browser won\'t decode ' + (it.videoCodec || 'this format') + '.') +
      ' Watch it in the Vault Movies desktop app, which plays anything.';

    var seen = S.resume[it.filmKey];
    var canResume = it.webPlayable && seen && seen.resumeMs > 15000 &&
                    (!it.durationMs || seen.resumeMs < it.durationMs - 30000);
    var row = $('dRow'); row.innerHTML = '';
    if (canResume) row.appendChild(mkBtn('Resume ' + fmt(seen.resumeMs), function () { play(it, seen.resumeMs); }, true));
    row.appendChild(mkBtn(canResume ? 'Start again' : 'Play', function () { play(it, 0); }, !canResume, !it.webPlayable));
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

  // ---- player -----------------------------------------------------------
  var v = $('v'), hudTimer = null, saveTimer = null;
  function play(it, startMs) {
    S.film = it;
    show('player');
    $('pTitle').textContent = it.name;
    v.src = it.streamUrl;              // relative; the signed token rides in the query
    v.addEventListener('loadedmetadata', function once() {
      v.removeEventListener('loadedmetadata', once);
      if (startMs > 0) { try { v.currentTime = startMs / 1000; } catch (e) {} }
    });
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
  function seekBy(sec) {
    if (S.screen !== 'player' || !isFinite(v.duration)) return;
    v.currentTime = Math.max(0, Math.min(v.duration - 1, v.currentTime + sec));
    bumpHud();
  }
  function bumpHud() {
    $('hud').className = '';
    clearTimeout(hudTimer);
    hudTimer = setTimeout(function () { if (!v.paused) $('hud').className = 'hide'; }, 4000);
  }
  v.addEventListener('timeupdate', function () {
    if (!isFinite(v.duration)) return;
    $('pAt').textContent = fmt(v.currentTime * 1000);
    $('pLeft').textContent = '-' + fmt((v.duration - v.currentTime) * 1000);
    $('pBar').style.width = (v.currentTime / v.duration * 100) + '%';
  });
  v.addEventListener('ended', function () { pushResume(true); stopPlayback(); });

  function pushResume(finished) {
    if (!S.film || !isFinite(v.duration)) return;
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
    pushResume(false);
    v.pause(); v.removeAttribute('src'); v.load();
    try { if (document.exitFullscreen && document.fullscreenElement) document.exitFullscreen(); } catch (e) {}
    show('library');
    renderGrid(null);
  }

  // ---- boot -------------------------------------------------------------
  rescale();
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
