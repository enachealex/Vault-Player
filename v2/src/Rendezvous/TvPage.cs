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
  /* Smoked glass over a dimmed film still, the same look as the desktop app
     (see Theme.xaml there). Nothing on this page can blur what is behind it —
     backdrop-filter is Chromium 76, a long way past the sets this runs on — so
     the still is softened ONCE into a small canvas (softenArt) and every
     panel is plain translucency on top of that. It reads as frosted glass and
     costs the television nothing per frame. */
  :root {
    --bg:#0B0D12; --deep:#06070A;
    --glass:rgba(255,255,255,.08); --glass-hi:rgba(255,255,255,.17);
    --line:rgba(255,255,255,.12); --line-hi:rgba(255,255,255,.3);
    --smoke:rgba(20,22,28,.72); --smoke-hi:rgba(44,48,58,.86);
    --text:#F4F6FA; --muted:#A6AFBD; --faint:#6F7887;
    --warm:#F5C77E; --danger:#FF7A7A; --ok:#5FD88E;
    /* Focus has its own colour, and nothing else on this page uses it. A ring
       that shares its colour with "this one is chosen" or "this is how far you
       got" stops meaning "you are here" from across a room. Chosen things are
       white instead, and progress is white. */
    --focus:#005ED9;
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

  /* ---- the wallpaper ---- */
  /* A dusk sky until there is a film to show, then the film under the
     highlight, faded in over it. Two canvases so one can fade into the next. */
  #wall { position:fixed; left:0; top:0; right:0; bottom:0; z-index:0; overflow:hidden;
    background:
      radial-gradient(ellipse 62% 78% at 78% 28%, rgba(122,60,36,.6), rgba(42,23,18,.28) 46%, rgba(11,13,18,0) 78%),
      radial-gradient(ellipse 70% 80% at 12% 94%, rgba(26,58,110,.62), rgba(15,30,58,.3) 50%, rgba(11,13,18,0) 82%),
      radial-gradient(ellipse 55% 45% at 42% 4%, rgba(38,49,74,.7), rgba(11,13,18,0) 100%),
      #0B0D12; }
  #wall canvas { position:absolute; left:0; top:0; width:100%; height:100%; opacity:0;
                 -webkit-transition:opacity .7s; transition:opacity .7s; }
  #wall canvas.on { opacity:1; }
  /* The scrim: the still is never shown at full strength, so white text reads
     on whatever frame a film happens to have. Darker low down where the grid
     is, and on the left where titles sit. */
  #wall .scrim { position:absolute; left:0; top:0; right:0; bottom:0;
    background:
      linear-gradient(to right, rgba(7,8,11,.55), rgba(7,8,11,0) 62%),
      linear-gradient(rgba(7,8,11,.5), rgba(7,8,11,.66) 45%, rgba(7,8,11,.9)); }

  /* Televisions overscan; nothing that matters sits in the outer edge. */
  .screen { display:none; height:100%; padding:3.4rem 5rem; position:relative; z-index:1; }
  .screen.on { display:block; }
  .col { height:100%; display:flex; flex-direction:column; }

  h1 { font-size:3.4rem; margin:0 0 .6rem; font-weight:700; letter-spacing:-.02em; }
  .sub { color:var(--muted); font-size:1.725rem; margin:0 0 1.6rem; line-height:1.5; }
  .sub b { color:var(--text); }

  /* The time, top right, as on the set's own home screen. */
  .clock { font-size:2.6rem; font-weight:300; color:var(--text); white-space:nowrap; }
  #clockFixed { position:fixed; top:3.2rem; right:5rem; z-index:2; }

  /* Focus IS the interface here. It must never be subtle or absent.
     Two rings, not one. The blue is the highlight; the pale hairline inside it
     is what keeps the blue readable when it lands on top of a film still,
     which is most of this screen. Blue on a dark wall is about 3:1, which is
     fine on its own and not fine against a bright frame — the hairline gives
     it an edge to sit against either way. Drawn as a shadow rather than a
     border so it costs the card no width: these are percentage-width columns
     and a real border would reflow the grid. */
  [data-f] { outline:none; }
  [data-f].focus { box-shadow:0 0 0 .14rem var(--text), 0 0 0 .5rem var(--focus); }

  /* ---- sign in ---- */
  .url { font-size:2.975rem; font-weight:800; color:var(--warm); margin:.4rem 0 1.4rem; }
  .entry { font-family:Consolas,monospace; font-size:3.975rem; font-weight:800; color:var(--text);
           letter-spacing:.5rem; min-height:4.4rem; }
  .entry .ph { color:var(--faint); }
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
    background:var(--smoke); border:1px solid var(--line); border-radius:.9rem;
    font-size:1.875rem; font-weight:700; text-align:center; color:var(--text);
    font-family:inherit; vertical-align:top;
  }
  .key.focus { background:var(--smoke-hi); }
  .key.act { color:var(--warm); font-size:1.425rem; }
  .krow { white-space:nowrap; }
  .err { color:var(--danger); font-size:1.675rem; min-height:2rem; margin-top:1rem; }

  /* ---- library: the frame every tab sits in ---- */
  /* No padding of its own: the featured film runs edge to edge, and every
     piece inside keeps the 5rem overscan margin for itself. */
  #library { padding:0; overflow:hidden; }
  /* Everything under the bar lives in #pageIn, which is slid with a transform
     rather than scrolled. A transform is composited — the television moves
     one finished layer instead of repainting the screen for every frame of
     the slide — and it is the only smooth option on a 2018 set. */
  #page { position:absolute; left:0; top:0; right:0; bottom:0; overflow:hidden; }
  #pageIn { position:relative; min-height:100%;
            -webkit-transition:-webkit-transform .32s ease-out; transition:transform .32s ease-out; }
  /* Home is all picture and rows, on the plain background; the other tabs
     let the softened wall show through. */
  #pageIn.home { background:var(--bg); }

  /* ---- the top bar ---- */
  /* Steps out of the way once the page has slid down into the rows, the way
     the set's own launcher does, and comes back when you return to the top. */
  .nav { position:absolute; left:5rem; right:5rem; top:2.4rem; height:4.6rem; z-index:5;
         -webkit-transition:opacity .25s; transition:opacity .25s; }
  #library.down .nav { opacity:0; }
  .logo { float:left; width:4.6rem; height:4.6rem; margin-right:1.4rem; border-radius:1rem; overflow:hidden; }
  .logo svg { display:block; width:100%; height:100%; }
  /* Glass. The fill alone has to read as glass, because backdrop-filter is
     Chromium 76 and most sets this runs on will simply skip that line. */
  .tabs { float:left; padding:.45rem; border-radius:1.1rem; white-space:nowrap;
          background:rgba(14,16,22,.55); border:1px solid rgba(255,255,255,.14);
          -webkit-backdrop-filter:blur(18px); backdrop-filter:blur(18px); }
  .tab { display:inline-block; vertical-align:top; height:3.6rem; margin:0 .4rem 0 0; padding:0 1.6rem;
         background:transparent; border:none; border-radius:.75rem; color:#C9CFD9;
         font-family:inherit; font-size:1.45rem; font-weight:600; }
  .tab:last-child { margin-right:0; }
  /* The tab you are on is white, as chosen things are everywhere here; the
     blue ring only ever says where the remote is. */
  .tab.on { background:rgba(255,255,255,.16); color:#fff; }
  .acct { float:right; width:4.6rem; height:4.6rem; margin-left:1.6rem; padding:0;
          background:rgba(14,16,22,.55); border:1px solid rgba(255,255,255,.14); border-radius:1rem;
          color:var(--text); font-family:inherit; font-size:1.7rem; font-weight:800; }
  .nav .clock { float:right; font-size:2rem; font-weight:600; color:#E7EAF0; line-height:4.6rem; }

  /* ---- the featured film ---- */
  /* The box is only as tall as the words; the still itself runs the full
     height of the screen, under the first row too, and the shade fades it
     into the page. Two layers so one film can cross-fade into the next. */
  .hero { position:relative; height:34rem; }
  .hart { position:absolute; left:0; top:0; width:100%; height:100vh; opacity:0;
          background-size:cover; background-position:center top;
          -webkit-transition:opacity 1.2s; transition:opacity 1.2s; }
  .hart.on { opacity:1; }
  .hshade { position:absolute; left:0; top:0; width:100%; height:100vh; z-index:1;
    background:
      linear-gradient(to right, rgba(11,13,18,.92) 0%, rgba(11,13,18,.55) 38%, rgba(11,13,18,0) 65%),
      linear-gradient(rgba(11,13,18,.5) 0%, rgba(11,13,18,0) 18%, rgba(11,13,18,0) 40%, rgba(11,13,18,.9) 66%, #0B0D12 82%); }
  .hcopy { position:absolute; left:5rem; top:9.4rem; width:64rem; z-index:2; }
  .kicker { font-size:1.15rem; font-weight:800; letter-spacing:.2em; color:var(--warm);
            text-transform:uppercase; margin-bottom:1.3rem; }
  .hcopy h1 { font-size:4.7rem; line-height:1.04; font-weight:800; letter-spacing:-.015em; margin:0;
              text-shadow:0 .2rem 1.6rem rgba(0,0,0,.55); max-height:9.8rem; overflow:hidden; }
  /* Year, runtime and the small outlined tags. */
  .fmeta { color:#D5DAE3; font-size:1.45rem; margin:1.3rem 0 0; }
  .fmeta .dot { opacity:.5; margin:0 .6rem; }
  .tag { display:inline-block; font-size:1.05rem; font-weight:700; letter-spacing:.06em; line-height:1;
         padding:.35rem .5rem; border-radius:.4rem; border:1px solid rgba(255,255,255,.35); color:#E7EAF0;
         margin-left:.7rem; vertical-align:.1rem; }
  .hcopy .btn { margin-top:2rem; }
  /* Which of the featured films this is. Small squares, not pills. */
  .hdots { position:absolute; left:5rem; top:31.2rem; z-index:2; line-height:0; }
  .hdots i { display:inline-block; width:.7rem; height:.7rem; margin-right:.6rem; border-radius:.18rem;
             background:rgba(255,255,255,.3); }
  .hdots i.on { background:#fff; }

  /* ---- rows of films ---- */
  .rows { position:relative; z-index:2; padding-bottom:6rem; }
  .row { margin-bottom:.6rem; }
  .row h3 { margin:0 0 0 5rem; font-size:1.8rem; font-weight:700; }
  /* The padding is room for a focused tile to grow into; the strip clips
     everything else. Slid with a transform, for the same reason as #pageIn. */
  .strip { overflow:hidden; padding:1.1rem 0 1.4rem 5rem; }
  .strip-in { position:relative; white-space:nowrap;
              -webkit-transition:-webkit-transform .22s ease-out; transition:transform .22s ease-out; }
  .tile { display:inline-block; vertical-align:top; position:relative; width:19.9rem; margin:0 1.3rem 0 0;
          padding:0; border:none; background:transparent; color:var(--text); font-family:inherit;
          text-align:left; white-space:normal;
          -webkit-transition:-webkit-transform .15s; transition:transform .15s; }
  /* 16:9 by padding, because aspect-ratio is far too new for these sets.
     The percentage is of the tile's own width, which is why it sits on an
     inner box rather than on the tile. */
  .tbox { display:block; position:relative; padding-top:56.25%; border-radius:.95rem; overflow:hidden;
          background:#151821; border:1px solid rgba(255,255,255,.06); }
  .tglyph { position:absolute; left:0; top:28%; width:100%; text-align:center; font-size:2.6rem; color:var(--faint); }
  .tart { position:absolute; left:0; top:0; width:100%; height:100%; background-size:cover; background-position:center; }
  .tshade { position:absolute; left:0; top:0; width:100%; height:100%;
            background:linear-gradient(rgba(0,0,0,0) 42%, rgba(0,0,0,.82)); }
  .tt { position:absolute; left:1rem; right:1rem; bottom:.9rem; font-size:1.35rem; font-weight:700;
        line-height:1.22; max-height:3.3rem; overflow:hidden; text-shadow:0 1px 3px #000; }
  .tile.prog .tt { bottom:1.9rem; }
  .tnew { position:absolute; left:.8rem; top:.8rem; background:#E35A3C; color:#fff; font-size:1rem;
          font-weight:800; letter-spacing:.08em; line-height:1; padding:.35rem .55rem; border-radius:.45rem; }
  .tapp { position:absolute; right:.8rem; top:.8rem; background:rgba(6,7,10,.78); color:var(--warm);
          font-size:1rem; font-weight:700; line-height:1; padding:.35rem .55rem; border-radius:.45rem; }
  .tbar { position:absolute; left:1rem; right:1rem; bottom:.9rem; height:.4rem; border-radius:.2rem;
          background:rgba(255,255,255,.25); overflow:hidden; }
  .tbar i { display:block; height:100%; background:#fff; }
  /* A focused tile is lifted and ringed in white — it sits on a picture, and
     white is what reads on every picture. Ring on the inner box, so it
     follows the rounded corners. */
  [data-f].tile.focus { box-shadow:none; z-index:3; -webkit-transform:scale(1.08); transform:scale(1.08); }
  .tile.focus .tbox { box-shadow:0 0 0 .25rem #fff, 0 1.4rem 3.4rem rgba(0,0,0,.6); }

  /* ---- Films, My List, search results: a grid of the same tiles ---- */
  .gwrap { position:absolute; left:0; right:0; top:9rem; bottom:0; }
  .ghead { margin:0 5rem; height:3.6rem; font-size:2.4rem; font-weight:800; white-space:nowrap; overflow:hidden; }
  .ghead .n { color:var(--muted); font-size:1.6rem; font-weight:600; margin-left:1rem; }
  /* The padding is what stops a lifted tile or its ring being shaved off by
     the scroll clip. inline-block and percentage margins, never flex gap —
     gap collapses to nothing on the sets this has to run on. */
  .grid { position:absolute; left:0; right:0; top:3.6rem; bottom:0; overflow-y:auto; overflow-x:hidden;
          padding:1.4rem 5rem 5rem; }
  .grid::-webkit-scrollbar { display:none; }
  .grid .tile { width:18.6%; margin:0 1.75% 2.2rem 0; }
  .grid .tile:nth-child(5n) { margin-right:0; }
  .grid.c4 .tile { width:23.5%; margin:0 2% 2.2rem 0; }
  .grid.c4 .tile:nth-child(5n) { margin-right:2%; }
  .grid.c4 .tile:nth-child(4n) { margin-right:0; }
  .gwrap .center, .sright .center { padding-top:8rem; }

  /* ---- search ---- */
  .sleft { position:absolute; left:5rem; top:9rem; width:30rem; }
  .sbox { height:4.8rem; line-height:4.8rem; padding:0 1.4rem; margin-bottom:1.2rem; border-radius:.95rem;
          background:rgba(14,16,22,.55); border:1px solid rgba(255,255,255,.14);
          font-size:1.8rem; font-weight:600; white-space:nowrap; overflow:hidden; }
  .sbox .ph { color:var(--faint); font-weight:400; }
  .skeys .key { width:4.5rem; height:4.5rem; font-size:1.6rem; margin:0 .44rem .44rem 0; border-radius:.75rem; }
  .skeys .key.w2 { width:9.44rem; font-size:1.3rem; }
  .sright { position:absolute; left:36rem; right:0; top:9rem; bottom:0; }
  .sright .ghead { margin-left:1.4rem; }
  .sright .grid { padding-left:1.4rem; }

  /* ---- dropdowns ---- */
  .menu { display:none; position:fixed; z-index:50; background:rgba(22,24,30,.97);
          border:1px solid var(--line); border-radius:1.2rem; padding:.6rem;
          box-shadow:0 1.4rem 3.4rem rgba(0,0,0,.7); }
  .menu.on { display:block; }
  .mrow {
    display:block; width:100%; text-align:left; background:transparent; border:none;
    color:var(--text); font-family:inherit; font-size:1.525rem; padding:.9rem 1.4rem;
    border-radius:.8rem; white-space:nowrap;
  }
  .mrow.focus { background:var(--glass-hi); }
  .mrow[disabled] { color:var(--faint); }

  /* ---- collections ---- */
  .shlist { flex:1; min-height:0; overflow-y:auto; padding:.65rem; }
  .shlist::-webkit-scrollbar { display:none; }
  /* Capped rather than full-bleed. A button the width of a television reads as
     a banner, and the eye has to travel the whole screen to find where the
     highlight is. */
  .shrow {
    display:block; width:100%; max-width:46rem; text-align:left; margin:0 0 .7rem;
    padding:1.1rem 1.6rem; background:var(--smoke); border:1px solid var(--line);
    border-radius:1rem; color:var(--text); font-size:1.775rem; font-family:inherit;
  }
  .shrow.focus { background:var(--smoke-hi); }
  .shrow .tick { color:var(--ok); font-weight:700; margin-right:1rem; }
  .shrow.new { color:var(--warm); }

  /* ---- detail ---- */
  /* A pane of glass over the film itself: the wall behind is this film,
     softened and dimmed, and the still sits sharp inside the pane. "More from
     your vault" runs along the bottom as an ordinary row. */
  #detail { padding:0; }
  .dpanel { position:absolute; left:5rem; right:5rem; top:4rem; padding:2.8rem 3rem; border-radius:1.4rem;
            background:rgba(16,18,26,.72); border:1px solid rgba(255,255,255,.14);
            -webkit-backdrop-filter:blur(30px) saturate(150%); backdrop-filter:blur(30px) saturate(150%);
            box-shadow:0 2rem 5rem rgba(0,0,0,.45); }
  .dpanel:after { content:''; display:block; clear:both; }
  .dleft { float:left; width:61%; padding-right:2.6rem; }
  .dright { float:right; width:39%; }
  .dpanel h1 { font-size:3.6rem; line-height:1.08; font-weight:800; margin:0; max-height:7.8rem; overflow:hidden; }
  .dart { position:relative; width:100%; padding-top:56.25%; background:var(--deep); border-radius:1rem;
          background-size:cover; background-position:center; overflow:hidden;
          border:1px solid var(--line); box-shadow:0 1.6rem 4rem rgba(0,0,0,.5); }
  .dart .glyph { position:absolute; left:0; top:40%; width:100%; text-align:center;
                 font-size:4.375rem; color:var(--faint); }
  .dpanel .btn { margin:1.8rem .7rem 0 0; padding:0 1.8rem; font-size:1.5rem; }
  .dprog { margin-top:1.3rem; color:var(--muted); font-size:1.3rem; white-space:nowrap; }
  .dprog .bar { display:inline-block; width:22rem; height:.4rem; margin-right:1.2rem; vertical-align:middle;
                background:rgba(255,255,255,.18); border-radius:.2rem; overflow:hidden; }
  .dprog .bar i { display:block; height:100%; background:#fff; }
  .dnote { color:var(--muted); font-size:1.4rem; margin-top:1.3rem; }
  .facts { margin-top:1.4rem; font-size:1.4rem; line-height:1.55; }
  .facts .k { display:inline-block; width:13rem; color:var(--faint); }
  .warn { color:var(--warm); font-size:1.45rem; margin:1.2rem 0 0; line-height:1.5; }
  .warn:empty, .dnote:empty, .dprog:empty { display:none; }
  .dmore { position:absolute; left:0; right:0; bottom:1.6rem; }
  /* The action a screen wants pressed is white, as on every television's own
     Play button; the rest are glass. Rounded rectangles, never pills. */
  .btn { display:inline-block; background:var(--text); color:var(--bg); border:1px solid var(--text);
         border-radius:.95rem; padding:0 2.4rem; height:4.7rem; line-height:4.5rem; font-size:1.6rem;
         font-weight:700; font-family:inherit; margin:1.4rem .9rem 0 0; white-space:nowrap; vertical-align:top; }
  .btn.ghost { background:rgba(255,255,255,.16); border-color:rgba(255,255,255,.18); color:var(--text); }
  .btn.ghost.focus { background:rgba(255,255,255,.26); }
  .btn[disabled] { opacity:.4; }
  /* A play triangle drawn with borders, and an "i" in a ring: no icon font
     to load, and both are crisp at any size rescale() picks. */
  .tri { display:inline-block; width:0; height:0; margin:0 1rem 0 0; vertical-align:-.05rem;
         border-left:1.15rem solid currentColor; border-top:.68rem solid transparent; border-bottom:.68rem solid transparent; }
  .ii { display:inline-block; width:1.7rem; height:1.7rem; line-height:1.45rem; margin:0 .9rem 0 0; text-align:center;
        border:.16rem solid currentColor; border-radius:50%; font-size:1.1rem; font-weight:800; vertical-align:.05rem; }
  .ico { display:inline-block; width:1.4rem; margin-right:.7rem; text-align:center; font-weight:800; }

  /* ---- player ---- */
  #player { padding:0; background:#000; }
  #player.on { position:relative; }
  video { width:100%; height:100%; background:#000; display:block; }
  #hud { position:absolute; left:0; right:0; bottom:0; padding:3rem 4rem 2.6rem;
         background:linear-gradient(rgba(0,0,0,0),rgba(0,0,0,.88)); transition:opacity .25s; }
  #hud.hide { opacity:0; }
  video::cue { font-size:2.4rem; line-height:1.3; color:#fff; background:rgba(0,0,0,.62); }
  #hud .t { font-size:2.375rem; font-weight:700; }
  #hud .times { font-size:1.475rem; color:var(--muted); margin:.8rem 0 .6rem; }
  #hud .times .right { float:right; }
  .track { height:.45rem; background:rgba(255,255,255,.24); border-radius:.25rem; overflow:hidden; clear:both; }
  .track i { display:block; height:100%; background:var(--text); border-radius:.25rem; }
  /* Positioned against #hud, its parent, so it fades with the rest of the HUD
     instead of sitting over the picture for the whole film. */
  #hint { position:absolute; top:.9rem; right:4rem; color:var(--muted); font-size:1.425rem;
          background:rgba(20,22,28,.7); border:1px solid var(--line); border-radius:.6rem;
          padding:.35rem 1.2rem; }
  .center { text-align:center; color:var(--muted); font-size:1.775rem; padding-top:6rem; }

  /* Shown while the server repackages a film the television cannot open as it
     stands. Absolutely positioned rather than flex-centred: this has to lay out
     the same on a 2018 set as on a current one. */
  #prep { display:none; position:absolute; left:0; right:0; top:38%; text-align:center; }
  #prep.on { display:block; }
  #prep .pt { font-size:2.275rem; font-weight:700; }
  #prep .pp { font-size:3.575rem; font-weight:700; color:var(--text); margin:1.4rem 0 1rem; }
  #prep .ps { font-size:1.575rem; color:var(--muted); }
</style>
</head>
<!-- tabindex so the document itself can be focused. Without it there is a
     moment on load with no active element at all, and a television browser
     reads that as "this page does not want the keys". -->
<body tabindex="-1">

<!-- The wallpaper every screen sits on. See softenArt. -->
<div id="wall"><canvas id="wallA" width="256" height="144"></canvas><canvas id="wallB" width="256" height="144"></canvas><div class="scrim"></div></div>
<!-- The library carries its own clock in its header; every other screen uses this one. -->
<div class="clock" id="clockFixed"></div>

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

<section id="library" class="screen">
  <!-- Home, Films, My List and Search all draw into #pageIn; the bar stays put. -->
  <div id="page"><div id="pageIn"></div></div>
  <div class="nav" id="nav">
    <div class="logo"><svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024"> <defs> <linearGradient id="tile" x1="0" y1="0" x2="1" y2="1"> <stop offset="0" stop-color="#E8603F"/> <stop offset="1" stop-color="#A9331B"/> </linearGradient> <linearGradient id="steel" x1="0" y1="0" x2="1" y2="1"> <stop offset="0" stop-color="#D9DEE6"/> <stop offset="0.5" stop-color="#8C95A4"/> <stop offset="1" stop-color="#4A515E"/> </linearGradient> <linearGradient id="doorFace" x1="0" y1="0" x2="1" y2="0"> <stop offset="0" stop-color="#5A6270"/> <stop offset="0.45" stop-color="#C3CAD5"/> <stop offset="1" stop-color="#6E7685"/> </linearGradient> <radialGradient id="glow" cx="0.5" cy="0.25" r="0.85"> <stop offset="0" stop-color="#3A2A1E"/> <stop offset="1" stop-color="#0C0D12"/> </radialGradient> <clipPath id="inside"><circle cx="440" cy="520" r="268"/></clipPath> </defs> <rect width="1024" height="1024" rx="200" fill="url(#tile)"/> <circle cx="440" cy="520" r="330" fill="#2B2F38"/> <circle cx="440" cy="520" r="318" fill="url(#steel)"/> <circle cx="440" cy="520" r="282" fill="#2B2F38"/> <g fill="#3A3F4A"> <circle cx="440" cy="219" r="13"/><circle cx="440" cy="821" r="13"/> <circle cx="139" cy="520" r="13"/> <circle cx="227" cy="307" r="13"/><circle cx="227" cy="733" r="13"/> <circle cx="653" cy="307" r="13"/><circle cx="653" cy="733" r="13"/> </g> <g clip-path="url(#inside)"> <rect x="160" y="240" width="560" height="560" fill="url(#glow)"/> <g> <rect x="236" y="300" width="44" height="118" rx="5" fill="#F5C77E"/> <rect x="286" y="290" width="40" height="128" rx="5" fill="#F4F6FA"/> <rect x="332" y="306" width="46" height="112" rx="5" fill="#3FA7A0"/> <rect x="384" y="296" width="40" height="122" rx="5" fill="#E35A3C"/> <rect x="430" y="310" width="44" height="108" rx="5" fill="#F5C77E"/> <rect x="480" y="294" width="40" height="124" rx="5" fill="#7FB3FF"/> <rect x="526" y="304" width="44" height="114" rx="5" fill="#F4F6FA"/> <rect x="576" y="300" width="40" height="118" rx="5" fill="#E35A3C"/> </g> <rect x="160" y="418" width="560" height="18" fill="#C9A46A"/> <g> <rect x="200" y="470" width="44" height="122" rx="5" fill="#E35A3C"/> <rect x="250" y="480" width="40" height="112" rx="5" fill="#F4F6FA"/> <rect x="296" y="466" width="44" height="126" rx="5" fill="#F5C77E"/> <circle cx="420" cy="530" r="62" fill="#D9DEE6"/> <circle cx="420" cy="530" r="16" fill="#2B2F38"/> <g fill="#2B2F38"> <circle cx="420" cy="494" r="15"/><circle cx="420" cy="566" r="15"/> <circle cx="389" cy="512" r="15"/><circle cx="451" cy="512" r="15"/> <circle cx="389" cy="548" r="15"/><circle cx="451" cy="548" r="15"/> </g> <rect x="500" y="476" width="40" height="116" rx="5" fill="#3FA7A0"/> <rect x="546" y="468" width="44" height="124" rx="5" fill="#F4F6FA"/> <rect x="596" y="482" width="40" height="110" rx="5" fill="#F5C77E"/> <rect x="642" y="472" width="44" height="120" rx="5" fill="#E35A3C"/> </g> <rect x="160" y="592" width="560" height="18" fill="#C9A46A"/> <g> <rect x="236" y="646" width="44" height="120" rx="5" fill="#7FB3FF"/> <rect x="286" y="656" width="40" height="110" rx="5" fill="#F5C77E"/> <rect x="332" y="642" width="44" height="124" rx="5" fill="#F4F6FA"/> <rect x="382" y="652" width="40" height="114" rx="5" fill="#E35A3C"/> <rect x="428" y="644" width="44" height="122" rx="5" fill="#3FA7A0"/> <rect x="478" y="656" width="40" height="110" rx="5" fill="#F4F6FA"/> <rect x="524" y="648" width="44" height="118" rx="5" fill="#F5C77E"/> <rect x="574" y="660" width="40" height="106" rx="5" fill="#7FB3FF"/> </g> <rect x="160" y="766" width="560" height="18" fill="#C9A46A"/> </g> <rect x="752" y="380" width="34" height="70" rx="10" fill="#3A3F4A"/> <rect x="752" y="590" width="34" height="70" rx="10" fill="#3A3F4A"/> <ellipse cx="862" cy="520" rx="118" ry="336" fill="#2B2F38"/> <ellipse cx="856" cy="520" rx="104" ry="318" fill="url(#doorFace)"/> <ellipse cx="856" cy="520" rx="78" ry="250" fill="none" stroke="#4A515E" stroke-width="10"/> <g stroke="#2B2F38" stroke-width="16" stroke-linecap="round"> <line x1="856" y1="420" x2="856" y2="620"/> <line x1="818" y1="452" x2="894" y2="588"/> <line x1="818" y1="588" x2="894" y2="452"/> </g> <ellipse cx="856" cy="520" rx="22" ry="62" fill="#2B2F38"/> <ellipse cx="856" cy="520" rx="10" ry="28" fill="#D9DEE6"/> </svg></div>
    <div class="tabs" id="tabs"><button class="tab on" data-f data-tab="home">Home</button><button class="tab" data-f data-tab="films">Films</button><button class="tab" data-f data-tab="mylist">My List</button><button class="tab" data-f data-tab="search">Search</button></div>
    <!-- Floated right, so the account square comes first and the clock sits to its left. -->
    <button class="acct" id="acct" data-f></button>
    <span class="clock" id="clockBar"></span>
  </div>
  <!-- Outside #pageIn on purpose: a fixed element inside a transformed one is
       positioned against that element, not the screen. -->
  <div class="menu" id="menu"></div>
</section>

<section id="detail" class="screen">
  <div class="dpanel">
    <div class="dleft">
      <h1 id="dTitle"></h1>
      <div class="fmeta" id="dMeta"></div>
      <div id="dRow"></div>
      <div class="dprog" id="dProg"></div>
      <div class="warn" id="dWarn"></div>
      <div class="dnote" id="dNote"></div>
      <div class="facts" id="dFacts"></div>
    </div>
    <div class="dright"><div class="dart" id="dArt"><span class="glyph">&#127916;</span></div></div>
  </div>
  <div class="dmore" id="dMore"></div>
</section>

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
    <div id="hint">OK pause &middot; &#9664; &#9654; skip 10s<span id="ccHint"></span> &middot; Back exit</div>
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

  // tab: which of Home, Films, My List and Search is showing. stack: films
  // opened one from another's page. libPos: where focus was in the library
  // when a film was opened. dirty: the library needs drawing again on return.
  var S = { access:null, refresh:null, me:null, items:[], byKey:{}, resume:{}, myList:[], film:null,
            screen:'signin', memoryOnly:false, collections:[], scanning:false,
            featured:[], heroIdx:0, tab:'home', query:'', stack:[], libPos:null, dirty:false, playFrom:null };

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
  /* One rem, in pixels. Anything measuring against the focus ring has to ask,
     because rescale() moves it. */
  function rootPx() {
    return parseFloat(document.documentElement.style.fontSize) || 16;
  }
  window.addEventListener('resize', rescale);

  // keep: the caller puts focus somewhere itself (going back to the library
  // lands on the tile you left, not on the first thing on the screen).
  function show(name, keep) {
    S.screen = name;
    var all = ['signin','gate','library','detail','shelves','newshelf','player'];
    for (var i = 0; i < all.length; i++) {
      var el = $(all[i]);
      if (all[i] === name) el.className = 'screen on'; else el.className = 'screen';
    }
    // The library carries its own clock in the bar; the detail pane would sit under this one.
    $('clockFixed').style.display = (name === 'library' || name === 'player' || name === 'detail') ? 'none' : '';
    // Nothing has been chosen on these, so they get the plain dusk.
    if (name === 'signin' || name === 'gate') showWall(null);
    if (!keep) focusFirst(true);
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
    var art = el.getAttribute('data-art');
    if (art) wallSoon(art);
    // Move real DOM focus too, not only the ring. Without this the document's
    // active element never leaves <body>, so a television's own spatial
    // navigation and its voice guidance have nothing to work with — and on a
    // set whose browser drives the arrow keys itself, the page never sees them.
    // preventScroll is Chromium 64; older sets ignore it and scroll anyway,
    // which afterFocus then undoes for the boxes moved by transform.
    try { el.focus({ preventScroll: true }); } catch (e) {}
    afterFocus(el);
    // scrollIntoView options need Chromium 61; keep the element visible by hand.
    var g = el.parentNode;
    while (g && g !== document.body && g.className.indexOf('grid') < 0) g = g.parentNode;
    if (g && g.className && g.className.indexOf('grid') >= 0) {
      // Leave room for the focus ring at both ends, or scrolling parks a card
      // exactly against the edge and clips the very highlight that says where
      // you are. Measured off the root font size rather than fixed at 24px,
      // because rescale() drives that from the screen: on a set reporting a 4K
      // viewport the root doubles, the ring doubles with it, and a fixed 24
      // would no longer clear it.
      var pad = Math.round(rootPx() * 0.9);
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
    if (stripMove(from, dx, dy)) return;
    // Down from the bar on Home goes into the featured film, never past it
    // into a row: geometry alone would often pick a tile, since the tabs sit
    // to the right of the film's buttons.
    if (dy > 0 && S.screen === 'library' && inside(from, $('nav')) && $('hero') && !document.querySelector('.menu.on')) {
      var hb = lastAbove.library;
      if (!hb || hb.offsetParent === null || !inside(hb, $('hero'))) hb = $('hero').querySelector('[data-first]') || $('hero').querySelector('[data-f]');
      if (hb) { setFocus(hb); return; }
    }
    // Measured where things will be, not where they are mid-slide. A quick
    // second press arrives while the page is still gliding to its new place,
    // and measured as it stands the bar would seem to sit BELOW a featured
    // film that is still on its way back down.
    var drift = pageDrift(), navEl = S.screen === 'library' ? $('nav') : null;
    function rect(el) {
      var r = el.getBoundingClientRect();
      if (!drift || !inside(el, $('pageIn'))) return r;
      return { left: r.left, top: r.top - drift, width: r.width, height: r.height };
    }
    var a = rect(from);
    var ax = a.left + a.width / 2, ay = a.top + a.height / 2;
    var f = focusables(), best = null, bestScore = Infinity, level = null, levelScore = Infinity;
    for (var i = 0; i < f.length; i++) {
      if (f[i] === from) continue;
      // Sideways never crosses between the bar and the page. Right off the
      // end of a row would otherwise find the account button, and Left off
      // Home would find a tile slid out past the edge of the screen.
      if (dx && navEl && inside(f[i], navEl) !== inside(from, navEl)) continue;
      var b = rect(f[i]);
      var ox = b.left + b.width / 2 - ax, oy = b.top + b.height / 2 - ay;
      if (dx && (dx > 0 ? ox <= 1 : ox >= -1)) continue;
      // and wholly past the edge you are leaving by, so Right off the wide
      // Clear key is not the 0 key sitting above its right half.
      if (dx && (dx > 0 ? b.left < a.left + a.width - 2 : b.left + b.width > a.left + 2)) continue;
      if (dy && (dy > 0 ? oy <= 1 : oy >= -1)) continue;
      var along = dx ? Math.abs(ox) : Math.abs(oy);
      var across = dx ? Math.abs(oy) : Math.abs(ox);
      var score = along + across * 3;   // punish sideways drift so a press goes where it pointed
      if (score < bestScore) { bestScore = score; best = f[i]; }
      // Sideways, something level with where you are beats something nearer
      // but a row up: Right off the search keypad's Clear goes to the
      // results, not up to the 0 above it.
      if (dx && b.top < a.top + a.height && b.top + b.height > a.top && score < levelScore) { levelScore = score; level = f[i]; }
    }
    if (level) best = level;
    // Arriving at a row from above it lands at that row's remembered column,
    // not at whichever tile happens to sit under the button you left.
    var into = best && stripOf(best);
    if (into && !stripOf(from)) { focusRow(into.row); return; }
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
    else if (k === 'ArrowUp' || c === 38) { if (playing) toggleCaptions(); else move(0, -1); }
    else if (c === 460 || k === 'Subtitle' || ((k === 'c' || k === 'C') && playing)) { if (playing) toggleCaptions(); }
    else if (k === 'ArrowDown' || c === 40) { if (!playing) move(0, 1); }
    else if (k === 'Enter' || c === 13) activate();
    else if (BACK.indexOf(c) >= 0 || k === 'Backspace' || k === 'Escape' || k === 'GoBack') handleBack(VIA_KEY);
    else if (c === 415 || c === 19 || c === 179 || k === 'MediaPlayPause') { if (S.screen === 'player') togglePlay(); }
    else if (c === 417 || k === 'MediaFastForward') seekBy(30);
    else if (c === 412 || k === 'MediaRewind') seekBy(-30);
    else if (S.screen === 'signin' && typeText(k, c)) { /* physical keyboard */ }
    // On the Search tab, a physical keyboard types into the search.
    else if (S.screen === 'library' && S.tab === 'search' && !document.querySelector('.menu.on') &&
             searchKey(k)) { /* handled */ }
    else handled = false;
    if (handled) { e.preventDefault(); e.stopPropagation(); }
  }, true);

  // ---- taking the keys --------------------------------------------------
  // A television browser opens a page with the remote still pointed at itself.
  // The arrows drove its own cursor and the page saw nothing until somebody
  // clicked once — which is a strange thing to have to explain to anybody, and
  // impossible to guess.
  //
  // Nothing a page can say makes a browser hand the keys over, so it is asked
  // repeatedly rather than once. The browser never announces the moment it
  // becomes willing, so the only way to catch that moment is to keep asking
  // through it. The asking stops the instant a key actually arrives, and gives
  // up after twelve seconds either way rather than nagging a set that is never
  // going to agree.
  var claimTicks = 0, claimTimer = 0;

  function claimWindow() { try { window.focus(); } catch (e) {} }

  function claimKeys(force) {
    // After the opening couple of seconds, only ask when the document plainly
    // does not have focus. Otherwise this would keep calling focus() on the
    // highlighted card while somebody is scrolling, and focus() scrolls.
    if (!force && document.hasFocus && document.hasFocus()) return;
    claimWindow();
    var el = current();
    try { if (el) el.focus(); else document.body.focus(); } catch (e) {}
  }

  function stopClaiming() {
    if (claimTimer) { clearInterval(claimTimer); claimTimer = 0; }
  }

  claimTimer = setInterval(function () {
    claimTicks++;
    claimKeys(claimTicks <= 8);   // forced for the first two seconds
    if (claimTicks > 48) stopClaiming();
  }, 250);

  // Every moment a browser might plausibly be willing.
  window.addEventListener('load', function () { claimKeys(true); });
  window.addEventListener('pageshow', function () { claimKeys(true); });
  window.addEventListener('focus', function () { claimKeys(true); });
  document.addEventListener('visibilitychange', function () {
    if (!document.hidden) claimKeys(true);
  });
  // webOS fires this when the pointer appears or disappears, and it disappears
  // the moment somebody reaches for the D-pad — which is precisely when the
  // page wants the keys and the browser still has them.
  document.addEventListener('cursorStateChange', function () { claimKeys(true); });
  // A click claims the window and nothing more. Moving DOM focus here would
  // drag it back to the highlighted element in the instant before the clicked
  // one gets its turn, which is a way to make the pointer feel broken while
  // fixing the remote.
  document.addEventListener('click', claimWindow, true);

  // A key arriving settles it: the page has the keys and can stop asking.
  document.addEventListener('keydown', stopClaiming, true);

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
    if (S.screen === 'detail') { closeDetail(); return; }
    if (S.screen === 'shelves') { show('detail'); return; }
    if (S.screen === 'newshelf') { show('shelves'); return; }
    if (S.screen === 'library') { libraryBack(); return; }
    // Sign-in is the floor. Doing nothing is deliberate — letting Back bubble
    // exits the app entirely on several platforms.
  }

  // In the library Back walks toward Home's featured film: from another tab
  // to Home, and from anywhere on Home to the top. Once there it does nothing,
  // for the same reason as above.
  function libraryBack() {
    if (!S.items.length) return;
    if (S.tab !== 'home') { setTab('home'); return; }
    var hero = $('hero'), c = current();
    if (hero && c && !inside(c, hero)) {
      var f = focusables();
      for (var i = 0; i < f.length; i++) if (inside(f[i], hero) && f[i].getAttribute('data-first') !== null) { setFocus(f[i]); return; }
      for (i = 0; i < f.length; i++) if (inside(f[i], hero)) { setFocus(f[i]); return; }
    }
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
    stopHero();
    S.items = []; S.byKey = {}; S.collections = []; S.resume = {}; S.myList = [];
    S.featured = []; S.tab = 'home'; S.query = ''; S.stack = []; S.libPos = null;
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
    if (!quiet) { show('library', true); pageMessage('Loading your library…'); }
    renderWho();
    api('/api/v1/catalog').then(function (r) {
      return r.json()['catch'](function () { return {}; }).then(function (j) {
        if (r.status === 403) { showGate(j.error || 'Your account is waiting for the owner\'s approval.'); return null; }
        if (!r.ok) {
          centerMessage(j.error || 'The library is not available.', 'Try again', function () { enter(true); });
          return null;
        }
        if (gateTimer) { clearInterval(gateTimer); gateTimer = null; }
        if (S.screen !== 'library') show('library', true);
        return j;
      });
    }).then(function (cat) {
      if (!cat) return;
      S.items = cat.items || [];
      S.byKey = {};
      for (var i = 0; i < S.items.length; i++) S.byKey[S.items[i].filmKey] = S.items[i];
      // Shelves and My List ride along with the catalogue, so this is one
      // round trip. An older relay omits them entirely; treat that as "none".
      S.collections = (cat.collections || []).slice().sort(function (a, b) { return a.sortOrder - b.sortOrder; });
      S.myList = cat.myList || [];
      S.scanning = !!cat.scanning;
      return api('/api/v1/library').then(function (r) { return r.ok ? r.json() : []; })
        ['catch'](function () { return []; })
        .then(function (lib) {
          S.resume = {};
          for (var i = 0; i < (lib || []).length; i++) S.resume[lib[i].filmKey] = lib[i];
          // Chosen once per visit, not per redraw, so coming back from a film
          // does not reshuffle the top of the screen.
          S.featured = featuredList();
          S.heroIdx = 0;
          renderTab(null);
        });
    })['catch'](function () {
      centerMessage('Could not reach the server.', 'Try again', function () { enter(true); });
    });
  }

  // ---- what to show ------------------------------------------------------
  // The same decisions the phone app makes (mobile/src/library.ts), so the
  // two never disagree about what is new or how far through a film you are.

  // What to call a film on screen: its tidied title, else the file name.
  function titleOf(it) { return it.title || it.name; }
  function byTitle(a, b) {
    try { return titleOf(a).localeCompare(titleOf(b), undefined, { sensitivity: 'base', numeric: true }); }
    catch (e) { return titleOf(a).toLowerCase() < titleOf(b).toLowerCase() ? -1 : 1; }
  }
  function lastWatched(a, b) {
    return ((S.resume[b.filmKey] || {}).lastWatchedAt || 0) - ((S.resume[a.filmKey] || {}).lastWatchedAt || 0);
  }
  // In progress, or null: barely started and into the credits both count as not.
  function progressOf(it) {
    var row = S.resume[it.filmKey];
    if (!row || !row.resumeMs || !it.durationMs) return null;
    var frac = row.resumeMs / it.durationMs;
    if (frac < 0.01 || frac > 0.95) return null;
    return { frac: frac, row: row };
  }
  var DAY = 86400000;
  function isNew(it) { return new Date().getTime() - it.addedAt < 14 * DAY && !S.resume[it.filmKey]; }
  function onList(it) { return S.myList.indexOf(it.filmKey) >= 0; }
  function filmsFor(keys) {
    var out = [];
    for (var i = 0; i < keys.length; i++) if (S.byKey[keys[i]]) out.push(S.byKey[keys[i]]);
    return out;
  }
  function shelfKeys(shelf) {
    var out = [];
    for (var i = 0; i < shelf.items.length; i++) out.push(shelf.items[i].filmKey);
    return out;
  }
  function shuffle(a) {
    for (var i = a.length - 1; i > 0; i--) {
      var j = Math.floor(Math.random() * (i + 1)), t = a[i]; a[i] = a[j]; a[j] = t;
    }
    return a;
  }
  // The films the featured slot cycles through: new arrivals nobody has
  // started, then anything never started, shuffled per visit. Only films with
  // a still, because the slot is mostly picture.
  function featuredList() {
    var now = new Date().getTime(), withArt = [], fresh = [], unwatched = [], rest = [], i;
    for (i = 0; i < S.items.length; i++) if (S.items[i].artUrl) withArt.push(S.items[i]);
    for (i = 0; i < withArt.length; i++) {
      var f = withArt[i];
      if (S.resume[f.filmKey]) rest.push(f);
      else if (now - f.addedAt < 30 * DAY) fresh.push(f);
      else unwatched.push(f);
    }
    return shuffle(fresh).concat(shuffle(unwatched)).concat(shuffle(rest)).slice(0, 5);
  }
  function runtime(ms) {
    if (!ms) return '';
    var min = Math.round(ms / 60000), h = Math.floor(min / 60), m = min % 60;
    return h ? (m ? h + 'h ' + m + 'm' : h + 'h') : m + 'm';
  }
  function is4k(it) { return /2160p|\b4k\b|\buhd\b/i.test(it.name || ''); }
  // "2014 • 2h 16m  CC  HD". The tags are outlined, so they read as labels
  // rather than as something to press.
  function metaHtml(it) {
    var bits = [];
    if (it.year) bits.push(String(it.year));
    if (it.durationMs) bits.push(runtime(it.durationMs));
    return bits.join('<span class="dot">&bull;</span>') +
      (it.captionsUrl ? '<span class="tag">CC</span>' : '') +
      '<span class="tag">' + (is4k(it) ? '4K' : 'HD') + '</span>';
  }

  // ---- tabs ---------------------------------------------------------------
  // Home, Films, My List and Search draw into the one #pageIn under the bar.
  // `pos` is somewhere to put focus back to after the redraw (capturePos);
  // `focusTab` leaves it on the tab itself, which is what pressing OK on a tab
  // should do — the remote stays where the viewer put it.
  function renderTab(pos, focusTab) {
    var tabs = $('tabs').getElementsByTagName('button');
    for (var i = 0; i < tabs.length; i++)
      tabs[i].className = 'tab' + (tabs[i].getAttribute('data-tab') === S.tab ? ' on' : '');
    stopHero();
    setPageY(0);
    if (!S.items.length) {
      // A library still being indexed is not a permanent state, so this offers
      // the re-check that nothing else would ever perform.
      centerMessage(
        S.scanning ? 'Still indexing the library…' : 'No films on the server yet.',
        S.scanning ? 'Check again' : 'Refresh',
        function () { enter(true); });
      return;
    }
    if (S.tab === 'films') renderGridTab('Films', S.items.slice().sort(byTitle), 'A–Z', '');
    else if (S.tab === 'mylist') renderGridTab('My List', filmsFor(S.myList), '',
      'Nothing saved yet. Open any film and choose My List to keep it here.');
    else if (S.tab === 'search') renderSearch();
    else renderHome();

    if (focusTab) {
      for (var t = 0; t < tabs.length; t++) if (tabs[t].getAttribute('data-tab') === S.tab) setFocus(tabs[t]);
    } else if (!restorePos(pos)) focusFirst(true);
  }

  function setTab(name) {
    S.tab = name;
    renderTab(null, true);
  }

  // Slides the page so the focused row is in view. Up at the featured film
  // the page is at rest and the bar shows; any lower and the bar fades out.
  var pageY = 0;
  function setPageY(y) {
    pageY = y;
    var pin = $('pageIn'), tr = 'translate3d(0,' + (-y) + 'px,0)';
    pin.style.webkitTransform = tr; pin.style.transform = tr;
    var lib = $('library');
    if (y > 0) { if (lib.className.indexOf(' down') < 0) lib.className += ' down'; }
    else lib.className = lib.className.replace(' down', '');
  }
  // How far the page still has to travel to where setPageY sent it, while the
  // slide is in progress; zero at rest and on every other screen.
  function pageDrift() {
    if (S.screen !== 'library') return 0;
    return $('pageIn').getBoundingClientRect().top - $('page').getBoundingClientRect().top + pageY;
  }
  function pageScrollFor(el, t) {
    var y = 0;
    if (t) {
      var rows = rowsIn($('pageIn')), ri = rows.indexOf(t.row);
      // The first row sits under the featured film with the page at rest.
      // Every row after it is brought up to near the top of the screen, so
      // the rows still to come are what fills the rest of it.
      if (ri > 0) y = t.row.getBoundingClientRect().top - $('pageIn').getBoundingClientRect().top - rootPx() * 3;
    }
    var max = $('pageIn').offsetHeight - $('page').clientHeight;
    setPageY(Math.max(0, Math.min(y, max)));
  }

  // Every terminal screen in the library must leave at least one thing to
  // press. Back is deliberately a no-op at the floor — letting it bubble
  // closes the whole app on webOS and Tizen — so a screen with no [data-f] is
  // a set that has to be power-cycled to escape. The bar is always there, but
  // the way out should be in front of the viewer, not up in a corner.
  function centerMessage(text, label, fn) {
    var w = pageMessage(text);
    var b = document.createElement('button');
    b.className = 'btn';
    b.setAttribute('data-f', '');
    b.setAttribute('data-first', '');
    b.style.display = 'block';
    b.style.margin = '1.6rem auto 0';
    b.textContent = label;
    b.addEventListener('click', fn);
    w.appendChild(b);
    var out = document.createElement('button');
    out.className = 'btn ghost';
    out.setAttribute('data-f', '');
    out.style.display = 'block';
    out.style.margin = '.8rem auto 0';
    out.textContent = 'Sign out of this TV';
    out.addEventListener('click', signOut);
    w.appendChild(out);
    if (S.screen === 'library') focusFirst(true);
  }
  function pageMessage(text) {
    stopHero();
    setPageY(0);
    var pin = $('pageIn');
    pin.className = '';
    pin.innerHTML = '';
    var w = document.createElement('div');
    w.className = 'gwrap';
    var msg = document.createElement('div');
    msg.className = 'center';
    msg.textContent = text;
    w.appendChild(msg);
    pin.appendChild(w);
    return w;
  }

  // ---- home ---------------------------------------------------------------
  function renderHome() {
    var pin = $('pageIn');
    pin.className = 'home';
    pin.innerHTML = '';
    if (S.featured.length) {
      var h = document.createElement('div');
      h.className = 'hero';
      h.id = 'hero';
      h.innerHTML =
        '<div class="hart" id="heroA"></div><div class="hart" id="heroB"></div><div class="hshade"></div>' +
        '<div class="hcopy"><div class="kicker" id="hKick"></div><h1 id="hTitle"></h1>' +
        '<div class="fmeta" id="hMeta"></div><div id="hBtns"></div></div>' +
        '<div class="hdots" id="hDots"></div>';
      pin.appendChild(h);
      heroTop = null;
      showHero(S.heroIdx);
    } else {
      // Nothing with a still to feature: the rows start under the bar.
      var gap = document.createElement('div');
      gap.style.height = '9rem';
      pin.appendChild(gap);
    }

    var rows = document.createElement('div');
    rows.className = 'rows';
    var inProgress = [], again = [], i;
    for (i = 0; i < S.items.length; i++) {
      var it = S.items[i], seen = S.resume[it.filmKey];
      if (progressOf(it)) inProgress.push(it);
      else if (seen && seen.watchCount > 0) again.push(it);
    }
    addRow(rows, 'cont', 'Continue watching', inProgress.sort(lastWatched));
    addRow(rows, 'recent', 'Recently added',
      S.items.slice().sort(function (a, b) { return b.addedAt - a.addedAt; }).slice(0, 20));
    addRow(rows, 'mylist', 'My List', filmsFor(S.myList));
    for (i = 0; i < S.collections.length; i++)
      addRow(rows, 'shelf:' + S.collections[i].id, S.collections[i].name, filmsFor(shelfKeys(S.collections[i])));
    addRow(rows, 'again', 'Watch again', again.sort(lastWatched));
    addRow(rows, 'all', 'All films', S.items.slice().sort(byTitle));
    pin.appendChild(rows);
    startHero();
  }

  // A labelled, sideways-scrolling strip of tiles. Rows are told apart by
  // key rather than position, so the column each one was left at survives a
  // redraw that adds or removes a row above it.
  var rowCol = {};
  function addRow(host, key, label, films) {
    if (!films.length) return null;
    var row = document.createElement('div');
    row.className = 'row';
    row.setAttribute('data-rowkey', key);
    var h = document.createElement('h3');
    h.textContent = label;
    row.appendChild(h);
    var strip = document.createElement('div');
    strip.className = 'strip';
    var inn = document.createElement('div');
    inn.className = 'strip-in';
    for (var i = 0; i < films.length; i++) inn.appendChild(makeTile(films[i], false));
    strip.appendChild(inn);
    row.appendChild(strip);
    host.appendChild(row);
    return row;
  }

  // One film: its still, the title written on it, NEW for a recent arrival
  // nobody has watched, and a thin white bar for how far you got.
  function makeTile(it, withWall) {
    var p = progressOf(it);
    var b = document.createElement('button');
    b.className = 'tile' + (p ? ' prog' : '');
    b.setAttribute('data-f', '');
    b.setAttribute('data-key', it.filmKey);
    if (withWall && it.artUrl) b.setAttribute('data-art', it.artUrl);
    b.innerHTML =
      '<span class="tbox"><span class="tglyph">&#127916;</span><span class="tart"></span><span class="tshade"></span>' +
      (isNew(it) ? '<span class="tnew">NEW</span>' : '') +
      (routeFor(it) ? '' : '<span class="tapp">APP ONLY</span>') +
      '<span class="tt"></span>' +
      (p ? '<span class="tbar"><i style="width:' + Math.round(p.frac * 100) + '%"></i></span>' : '') +
      '</span>';
    b.getElementsByClassName('tt')[0].textContent = titleOf(it);
    b.addEventListener('click', function () { openDetail(it); });
    if (it.artUrl) loadArt(b.getElementsByClassName('tart')[0], it.artUrl);
    return b;
  }

  // ---- the featured film ----------------------------------------------------
  var heroTimer = null, heroTop = null;
  function showHero(i) {
    var it = S.featured[i];
    if (!it || !$('hero')) return;
    S.heroIdx = i;
    var p = progressOf(it), route = routeFor(it);
    $('hKick').textContent = p ? 'Continue watching' : isNew(it) ? 'New in your vault' : 'From your vault';
    $('hTitle').textContent = titleOf(it);
    $('hMeta').innerHTML = metaHtml(it);
    var btns = $('hBtns');
    btns.innerHTML = '';
    var playB = mkBtn(p ? 'Resume' : 'Play', function () { play(it, p ? p.row.resumeMs : 0); }, true, !route, false, 'play');
    playB.setAttribute('data-pos', 'heroPlay');
    btns.appendChild(playB);
    var info = mkBtn('More info', function () { openDetail(it); }, !route, false, true, 'info');
    info.setAttribute('data-pos', 'heroInfo');
    btns.appendChild(info);
    var dots = '';
    if (S.featured.length > 1)
      for (var d = 0; d < S.featured.length; d++) dots += '<i' + (d === i ? ' class="on"' : '') + '></i>';
    $('hDots').innerHTML = dots;

    // Cross-fade: the new still goes on whichever layer is hidden, and only
    // once its bytes are here, so the fade is never into an empty frame.
    withArt(it.artUrl, function (src) {
      if (S.featured[S.heroIdx] !== it || !$('heroA')) return;
      var next = heroTop === 'heroA' ? 'heroB' : 'heroA', prev = heroTop;
      $(next).style.zIndex = 1;
      if (prev) $(prev).style.zIndex = 0;
      $(next).style.backgroundImage = 'url("' + src + '")';
      $(next).className = 'hart on';
      heroTop = next;
      // The old still stays fully lit underneath until the new one is opaque
      // over it. Fading one out while the other fades in dips through black.
      if (prev) setTimeout(function () { if (heroTop !== prev && $(prev)) $(prev).className = 'hart'; }, 1300);
    });
    // Fetch the next one now, so it is ready when its turn comes.
    var after = S.featured[(i + 1) % S.featured.length];
    if (after && after.artUrl) withArt(after.artUrl, function () {});
  }
  // Every nine seconds — but never while the remote is on the featured film's
  // own buttons, or Play would change films under the viewer's thumb.
  function startHero() {
    stopHero();
    if (S.featured.length < 2) return;
    heroTimer = setInterval(function () {
      if (S.screen !== 'library' || S.tab !== 'home' || !$('hero')) return;
      if (document.querySelector('.menu.on')) return;
      var c = current();
      if (c && inside(c, $('hero'))) return;
      showHero((S.heroIdx + 1) % S.featured.length);
    }, 9000);
  }
  function stopHero() {
    if (heroTimer) { clearInterval(heroTimer); heroTimer = null; }
  }

  // ---- Films and My List --------------------------------------------------
  function renderGridTab(title, films, note, emptyText) {
    var pin = $('pageIn');
    pin.className = '';
    pin.innerHTML = '';
    var w = document.createElement('div');
    w.className = 'gwrap';
    var head = document.createElement('div');
    head.className = 'ghead';
    head.textContent = title;
    var n = document.createElement('span');
    n.className = 'n';
    n.textContent = films.length + (films.length === 1 ? ' film' : ' films') + (note ? '  ·  ' + note : '');
    head.appendChild(n);
    w.appendChild(head);
    var g = document.createElement('div');
    g.className = 'grid';
    w.appendChild(g);
    pin.appendChild(w);
    if (!films.length) {
      var msg = document.createElement('div');
      msg.className = 'center';
      msg.textContent = emptyText;
      g.appendChild(msg);
      var b = document.createElement('button');
      b.className = 'btn';
      b.setAttribute('data-f', '');
      b.setAttribute('data-first', '');
      b.style.display = 'block';
      b.style.margin = '1.6rem auto 0';
      b.textContent = 'Browse films';
      b.addEventListener('click', function () { S.tab = 'films'; renderTab(null); });
      g.appendChild(b);
      return;
    }
    for (var i = 0; i < films.length; i++) {
      var t = makeTile(films[i], true);
      if (i === 0) t.setAttribute('data-first', '');
      g.appendChild(t);
    }
  }

  // ---- search ---------------------------------------------------------------
  // A keypad rather than the set's own keyboard, which covers half the screen
  // and behaves differently on every make. Results follow every key, so there
  // is no Enter to find; an empty search shows the whole library A–Z.
  var SEARCH_ROWS = ['ABCDEF', 'GHIJKL', 'MNOPQR', 'STUVWX', 'YZ1234', '567890'];
  function renderSearch() {
    var pin = $('pageIn');
    pin.className = '';
    pin.innerHTML =
      '<div class="sleft"><div class="sbox" id="sBox"></div><div class="skeys" id="sKeys"></div></div>' +
      '<div class="sright"><div class="ghead" id="sHead"></div><div class="grid c4" id="sGrid"></div></div>';
    var html = '';
    for (var r = 0; r < SEARCH_ROWS.length; r++) {
      // One block per row, so Up and Down move between rows of keys.
      html += '<div class="krow">';
      for (var i = 0; i < SEARCH_ROWS[r].length; i++) {
        var ch = SEARCH_ROWS[r].charAt(i);
        html += '<button class="key" data-f' + (r === 0 && i === 0 ? ' data-first' : '') +
                ' data-pos="k' + ch + '" data-ch="' + ch + '">' + ch + '</button>';
      }
      html += '</div>';
    }
    html += '<div class="krow">' +
      '<button class="key act w2" data-f data-pos="ksp" data-act="sp">Space</button>' +
      '<button class="key act w2" data-f data-pos="kdel" data-act="del">Del</button>' +
      '<button class="key act w2" data-f data-pos="kclr" data-act="clr">Clear</button></div>';
    var host = $('sKeys');
    host.innerHTML = html;
    var keys = host.getElementsByTagName('button');
    for (var j = 0; j < keys.length; j++) {
      (function (b) {
        b.addEventListener('click', function () {
          var act = b.getAttribute('data-act');
          if (act === 'del') S.query = S.query.slice(0, -1);
          else if (act === 'clr') S.query = '';
          else if (act === 'sp') { if (S.query.length && S.query.slice(-1) !== ' ') S.query += ' '; }
          else if (S.query.length < 40) S.query += b.getAttribute('data-ch').toLowerCase();
          searchChanged();
        });
      })(keys[j]);
    }
    searchChanged();
  }
  // A physical keyboard types into the box too.
  function searchKey(k) {
    if (!k || k.length !== 1 || !/[a-z0-9 ]/i.test(k)) return false;
    if (k === ' ') { if (!S.query.length || S.query.slice(-1) === ' ') return true; }
    if (S.query.length < 40) S.query += k.toLowerCase();
    searchChanged();
    return true;
  }
  function searchChanged() {
    var box = $('sBox');
    if (!box) return;
    if (S.query) box.textContent = S.query;
    else box.innerHTML = '<span class="ph">Search your vault</span>';
    var q = S.query.replace(/^\s+|\s+$/g, ''), found = [];
    for (var i = 0; i < S.items.length; i++) {
      var it = S.items[i];
      if (!q || titleOf(it).toLowerCase().indexOf(q) >= 0 || it.name.toLowerCase().indexOf(q) >= 0) found.push(it);
    }
    found.sort(byTitle);
    var head = $('sHead');
    head.textContent = q ? 'Results' : 'All films';
    var n = document.createElement('span');
    n.className = 'n';
    n.textContent = q ? found.length + (found.length === 1 ? ' film' : ' films') : 'A–Z';
    head.appendChild(n);
    var g = $('sGrid');
    g.innerHTML = '';
    g.scrollTop = 0;
    if (!found.length) {
      var msg = document.createElement('div');
      msg.className = 'center';
      msg.textContent = 'Nothing in your vault matches “' + q + '”.';
      g.appendChild(msg);
    }
    for (var j = 0; j < found.length; j++) g.appendChild(makeTile(found[j], true));
  }

  // ---- account ------------------------------------------------------------
  // Who is signed in, and the only way to stop being signed in. Without it a
  // set stays bound to whoever linked it first, with no way to switch
  // accounts or hand the television on.
  function displayName() { return (S.me && (S.me.displayName || S.me.username)) || ''; }
  function renderWho() {
    var n = displayName();
    $('acct').textContent = n ? n.charAt(0).toUpperCase() : '?';
  }
  function openAccount() {
    openMenu($('acct'), [
      // Shown and disabled: it says whose library this is.
      { label: displayName() || 'Signed in', disabled: true },
      { label: 'Sign out of this TV', onSelect: signOut }
    ]);
  }

  // ---- where focus was ------------------------------------------------------
  // Opening a film and coming back must land on the same tile, in the same
  // row, with the row and the page where they were. When the library has been
  // redrawn in between (My List changed, a film was watched) the element is
  // gone, so it is found again by row and film, then by position.
  function capturePos() {
    var el = current();
    if (!el || S.screen !== 'library') return null;
    var t = stripOf(el);
    return {
      el: el,
      row: t ? t.row.getAttribute('data-rowkey') : null,
      idx: t ? t.idx : indexIn(el),
      key: el.getAttribute('data-key'),
      pos: el.getAttribute('data-pos') || (el.getAttribute('data-tab') ? 'tab:' + el.getAttribute('data-tab') : null)
    };
  }
  function indexIn(el) {
    var p = el.parentNode;
    if (!p) return 0;
    for (var i = 0; i < p.children.length; i++) if (p.children[i] === el) return i;
    return 0;
  }
  function restorePos(p) {
    if (!p) return false;
    var scope = $('library'), cand = null, i, list;
    if (p.el && p.el.offsetParent !== null && inside(p.el, scope)) cand = p.el;
    else if (p.row) {
      var rows = rowsIn(scope);
      for (i = 0; i < rows.length; i++) if (rows[i].getAttribute('data-rowkey') === p.row) {
        list = rows[i].getElementsByClassName('strip-in')[0].children;
        cand = findKey(list, p.key) || list[Math.min(p.idx, list.length - 1)];
      }
    } else if (p.key) {
      var g = scope.getElementsByClassName('grid')[0];
      list = g ? g.getElementsByClassName('tile') : [];
      cand = findKey(list, p.key) || (list.length ? list[Math.min(p.idx, list.length - 1)] : null);
    } else if (p.pos) {
      var f = focusables();
      for (i = 0; i < f.length; i++) {
        var tab = f[i].getAttribute('data-tab');
        if (f[i].getAttribute('data-pos') === p.pos || (tab && 'tab:' + tab === p.pos)) { cand = f[i]; break; }
      }
    }
    if (!cand) return false;
    setFocus(cand);
    return true;
  }
  function findKey(list, key) {
    if (!key) return null;
    for (var i = 0; i < list.length; i++) if (list[i].getAttribute('data-key') === key) return list[i];
    return null;
  }
  function inside(el, box) {
    while (el) { if (el === box) return true; el = el.parentNode; }
    return false;
  }

  // ---- moving along rows ----------------------------------------------------
  // Rows are moved through as rows, not by geometry. Left and Right walk the
  // strip and stop at its ends; Up and Down go to the next row at the column
  // that row was last left at, which is what makes a long library feel like
  // shelves rather than a field of tiles.
  var lastAbove = {};
  function stripOf(el) {
    var inn = el && el.parentNode;
    if (!inn || !inn.className || String(inn.className).indexOf('strip-in') < 0) return null;
    return { inn: inn, row: inn.parentNode.parentNode, idx: indexIn(el) };
  }
  function rowsIn(scope) {
    var all = scope.getElementsByClassName('row'), out = [];
    for (var i = 0; i < all.length; i++) if (all[i].offsetParent !== null) out.push(all[i]);
    return out;
  }
  function focusRow(row) {
    var inn = row.getElementsByClassName('strip-in')[0];
    var n = inn.children.length;
    if (!n) return;
    var col = rowCol[row.getAttribute('data-rowkey')] || 0;
    setFocus(inn.children[Math.min(col, n - 1)]);
  }
  function stripMove(from, dx, dy) {
    var t = stripOf(from);
    if (!t) return false;
    if (dx) {
      var next = t.inn.children[t.idx + dx];
      if (next) setFocus(next);
      return true;
    }
    var rows = rowsIn(focusScope()), i = rows.indexOf(t.row) + dy;
    if (i >= 0 && i < rows.length) { focusRow(rows[i]); return true; }
    if (dy > 0) return true;   // the last row is the floor
    // Up out of the top row goes back to the button you came down from.
    var a = lastAbove[S.screen];
    // The featured film redraws its buttons each time it changes, so the one
    // remembered may be gone; its twin is found by name.
    if (a && a.offsetParent === null && a.getAttribute('data-pos')) {
      var pos = a.getAttribute('data-pos'), f = focusables();
      a = null;
      for (var k = 0; k < f.length; k++) if (f[k].getAttribute('data-pos') === pos) { a = f[k]; break; }
    }
    if (!a && S.screen === 'library' && $('hero') && inside(t.row, $('pageIn')))
      a = $('hero').querySelector('[data-first]') || $('hero').querySelector('[data-f]');
    if (a && a.offsetParent !== null && inside(a, focusScope())) { setFocus(a); return true; }
    return false;
  }
  // Called by setFocus for everything it lands on.
  function afterFocus(el) {
    var t = stripOf(el);
    if (t) {
      rowCol[t.row.getAttribute('data-rowkey')] = t.idx;
      // Native focus scrolls clipped boxes to reveal the element; these are
      // positioned by transform, so put their scroll back where it belongs.
      t.inn.parentNode.scrollLeft = 0;
      scrollStrip(t.inn, el);
    } else if (!inside(el, $('menu')) && !inside(el, $('nav'))) {
      // The bar is never what Up out of the rows means: that is the button
      // just above them, which the bar is reached through.
      lastAbove[S.screen] = el;
    }
    $('page').scrollTop = 0;
    $('library').scrollTop = 0;
    if (S.screen === 'library' && S.tab === 'home') pageScrollFor(el, t);
  }
  // Keeps the focused tile inside the strip with room for it to grow, moving
  // only as far as it has to.
  function scrollStrip(inn, tile) {
    var room = rootPx() * 5;
    var view = inn.parentNode.clientWidth - room * 2;
    var x = inn._x || 0, L = tile.offsetLeft, R = L + tile.offsetWidth;
    if (L - x < 0) x = L;
    else if (R - x > view) x = R - view;
    if (x !== inn._x) {
      inn._x = x;
      var tr = 'translate3d(' + (-x) + 'px,0,0)';
      inn.style.webkitTransform = tr; inn.style.transform = tr;
    }
  }

  // Posters need an Authorization header and an <img> cannot send one, so fetch
  // the bytes and hand the element an object URL instead.
  //
  // Cached by URL, and deliberately never revoked: Home shows the same film in
  // several rows and every redraw shows them all again, so fetching afresh
  // would re-download the library over an authenticated connection on every
  // visit — on a television with a few hundred megabytes to play with, that is
  // a browser that dies after a few minutes. One blob per film for the life of
  // the page is the whole cost. Requests already in flight are shared, so a
  // film in four rows is still one download.
  var artCache = {}, artWaiting = {};
  function withArt(url, cb) {
    if (!url) return;
    if (artCache[url]) { cb(artCache[url]); return; }
    if (artWaiting[url]) { artWaiting[url].push(cb); return; }
    artWaiting[url] = [cb];
    api(url).then(function (r) { return r.ok ? r.blob() : null; }).then(function (b) {
      var waiting = artWaiting[url] || [];
      delete artWaiting[url];
      if (!b) return;
      artCache[url] = URL.createObjectURL(b);
      for (var i = 0; i < waiting.length; i++) waiting[i](artCache[url]);
      if (wallWanted === url) showWall(url);
    })['catch'](function () { delete artWaiting[url]; });
  }
  function loadArt(el, url) {
    withArt(url, function (src) {
      el.style.backgroundImage = 'url("' + src + '")';
      var gl = el.getElementsByClassName('glyph')[0];
      if (gl) gl.style.display = 'none';
    });
  }

  // ---- the wallpaper ----------------------------------------------------
  //
  // The film under the highlight, softened and dimmed, behind the whole page —
  // the way a television's own home screen takes on whatever tile is lit.
  //
  // The softening is done once per film, into a 256-pixel canvas, by halving
  // the picture down to almost nothing and doubling it back up. Each step is
  // the browser's own smoothing, and a run of them is a blur: no ctx.filter
  // (Chromium 52, and slow where it exists) and no CSS filter, which a 2018
  // set would re-run over a full-screen layer every frame. The canvas is then
  // stretched to the screen, and since there is nothing sharp left in it to
  // stretch, it stays smooth.
  var wallWanted = null, wallShown = null, wallTimer = null, wallTop = 'wallA';

  function softenArt(img, out) {
    var w = img.naturalWidth || img.width, h = img.naturalHeight || img.height;
    if (!w || !h) return false;
    // Cover the 16:9 canvas, cropping the middle of the frame.
    var sw = w, sh = Math.round(w * 9 / 16);
    if (sh > h) { sh = h; sw = Math.round(h * 16 / 9); }
    var cur = document.createElement('canvas'), cw = 256, ch = 144;
    cur.width = cw; cur.height = ch;
    cur.getContext('2d').drawImage(img, (w - sw) / 2, (h - sh) / 2, sw, sh, 0, 0, cw, ch);
    function step(nw, nh) {
      var n = document.createElement('canvas');
      n.width = nw; n.height = nh;
      n.getContext('2d').drawImage(cur, 0, 0, nw, nh);
      cur = n; cw = nw; ch = nh;
    }
    while (cw > 32) step(cw / 2, ch / 2);
    while (cw < 256) step(cw * 2, ch * 2);
    var ctx = out.getContext('2d');
    ctx.clearRect(0, 0, out.width, out.height);
    ctx.drawImage(cur, 0, 0, out.width, out.height);
    return true;
  }

  // Waits for the highlight to settle, so sweeping along a row does not
  // flicker through every film on the way past.
  function wallSoon(url) {
    wallWanted = url;
    clearTimeout(wallTimer);
    wallTimer = setTimeout(function () { showWall(url); }, 320);
  }

  function showWall(url) {
    wallWanted = url;
    clearTimeout(wallTimer);
    if (url === wallShown) return;
    if (!url) {
      wallShown = null;
      $('wallA').className = ''; $('wallB').className = '';
      return;
    }
    // Not fetched yet: loadArt calls back here when it is.
    if (!artCache[url]) return;
    var img = new Image();
    img.onload = function () {
      if (wallWanted !== url) return;   // the highlight moved on while this decoded
      var next = wallTop === 'wallA' ? 'wallB' : 'wallA';
      if (!softenArt(img, $(next))) return;
      $(next).className = 'on';
      $(wallTop).className = '';
      wallTop = next;
      wallShown = url;
    };
    img.src = artCache[url];
  }

  // ---- the clock --------------------------------------------------------
  function tickClock() {
    var d = new Date(), txt;
    try { txt = d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }); }
    catch (e) { txt = d.getHours() + ':' + pad2(d.getMinutes()); }
    $('clockFixed').textContent = txt;
    $('clockBar').textContent = txt;
  }

  // ---- detail -----------------------------------------------------------
  // Films opened from "More from your vault" stack up, so Back walks back
  // through them before returning to the library.
  function openDetail(it, popping) {
    if (S.screen === 'library') { S.libPos = capturePos(); S.stack = []; }
    if (!popping) S.stack.push(it);
    S.film = it;
    $('dTitle').textContent = titleOf(it);
    $('dMeta').innerHTML = metaHtml(it);

    // Reuse the cached still — the tile has already fetched it, so this is
    // instant and costs no second request. The wall behind the glass is the
    // same film, softened.
    var art = $('dArt');
    art.style.backgroundImage = '';
    var gl = art.getElementsByClassName('glyph')[0];
    if (gl) gl.style.display = '';
    if (it.artUrl) { loadArt(art, it.artUrl); showWall(it.artUrl); } else showWall(null);

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

    var p = progressOf(it);
    var row = $('dRow');
    row.innerHTML = '';
    row.appendChild(mkBtn(p ? 'Resume' : 'Play', function () { play(it, p ? p.row.resumeMs : 0); },
                          true, !route, false, 'play'));
    if (p && route) row.appendChild(mkBtn('Start over', function () { play(it, 0); }, false, false, true));
    var listB = mkBtn('My List', function () { toggleList(it, listB); }, !route, false, true);
    setListLabel(listB, onList(it));
    row.appendChild(listB);
    row.appendChild(mkBtn('Add to collection', openShelves, false, false, true));

    // How far through, as on the tile, and what is left in words.
    var left = p ? Math.max(0, it.durationMs - p.row.resumeMs) : 0;
    $('dProg').innerHTML = p
      ? '<span class="bar"><i style="width:' + Math.round(p.frac * 100) + '%"></i></span>' +
        (left >= 3600000 ? runtime(left) : Math.max(1, Math.round(left / 60000)) + ' min') + ' left'
      : '';
    $('dNote').textContent = it.captionsUrl
      ? 'English captions. Press ▲ during the film to turn them on or off.' : '';

    var seen = S.resume[it.filmKey], shelves = [];
    for (var i = 0; i < S.collections.length; i++)
      if (onShelf(S.collections[i], it)) shelves.push(S.collections[i].name);
    var facts = [
      ['Watched', seen && seen.watchCount > 0 ? (seen.watchCount === 1 ? 'Once' : seen.watchCount + ' times') : ''],
      ['On your shelves', shelves.join(', ')],
      ['Added', it.addedAt ? dateOf(it.addedAt) : ''],
      ['Format', [it.videoCodec, it.audioCodec].filter(function (s) { return !!s; })
                   .map(function (s) { return String(s).toUpperCase(); }).join(' · ')],
      ['File', sizeOf(it.sizeBytes)]
    ];
    var fh = $('dFacts');
    fh.innerHTML = '';
    for (var f = 0; f < facts.length; f++) {
      if (!facts[f][1]) continue;
      var line = document.createElement('div');
      line.innerHTML = '<span class="k"></span><span class="v"></span>';
      line.firstChild.textContent = facts[f][0];
      line.lastChild.textContent = facts[f][1];
      fh.appendChild(line);
    }

    // Films on the same shelves first, then whatever else is newest.
    var same = {}, more = [];
    for (i = 0; i < S.collections.length; i++) {
      if (!onShelf(S.collections[i], it)) continue;
      var films = filmsFor(shelfKeys(S.collections[i]));
      for (var j = 0; j < films.length; j++)
        if (films[j].filmKey !== it.filmKey && !same[films[j].filmKey]) { same[films[j].filmKey] = 1; more.push(films[j]); }
    }
    var newest = S.items.slice().sort(function (a, b) { return b.addedAt - a.addedAt; });
    for (i = 0; i < newest.length && more.length < 12; i++)
      if (newest[i].filmKey !== it.filmKey && !same[newest[i].filmKey]) more.push(newest[i]);
    var host = $('dMore');
    host.innerHTML = '';
    delete rowCol.more;
    addRow(host, 'more', 'More from your vault', more.slice(0, 12));

    show('detail');
  }

  function closeDetail() {
    S.stack.pop();
    if (S.stack.length) { openDetail(S.stack[S.stack.length - 1], true); return; }
    backToLibrary();
  }

  // Back to the tile the film was opened from. If something changed while
  // away — My List, a shelf, a film watched — the tab is drawn again first so
  // rows and progress bars are true, and focus is found again in the new one.
  function backToLibrary() {
    show('library', true);
    if (S.dirty) { S.dirty = false; renderTab(S.libPos); return; }
    if (!restorePos(S.libPos)) focusFirst(true);
  }

  // My List, from the film's own page. The label changes at once and the
  // server's answer — the whole list — is then taken as the truth.
  function setListLabel(b, on) {
    b.innerHTML = '<span class="ico">' + (on ? '&#10003;' : '+') + '</span>My List';
  }
  function toggleList(it, b) {
    if (b.getAttribute('data-busy')) return;
    var want = !onList(it);
    b.setAttribute('data-busy', '1');
    setListLabel(b, want);
    api('/api/v1/mylist/' + encodeURIComponent(it.filmKey), { method: want ? 'PUT' : 'DELETE' })
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (list) {
        b.removeAttribute('data-busy');
        if (list && typeof list.length === 'number') { S.myList = list; S.dirty = true; }
        else $('dNote').textContent = 'The server would not change My List just now.';
        setListLabel(b, onList(it));
      })['catch'](function () {
        b.removeAttribute('data-busy');
        $('dNote').textContent = 'Could not reach the server.';
        setListLabel(b, onList(it));
      });
  }

  function dateOf(ms) {
    try { return new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }); }
    catch (e) { return new Date(ms).toDateString(); }
  }
  function sizeOf(b) {
    if (!b) return '';
    return b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : Math.round(b / 1e6) + ' MB';
  }

  // icon: 'play' or 'info', drawn in CSS ahead of the label.
  function mkBtn(label, fn, first, disabled, ghost, icon) {
    var b = document.createElement('button');
    b.className = 'btn' + (ghost ? ' ghost' : '');
    b.textContent = label;
    if (icon) {
      var i = document.createElement('span');
      i.className = icon === 'play' ? 'tri' : 'ii';
      if (icon === 'info') i.textContent = 'i';
      b.insertBefore(i, b.firstChild);
    }
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

  // ---- collections ------------------------------------------------------
  //
  // Shelves can be made and filled from the television, not only the desktop.
  // The API is the same one the desktop uses; the only thing the set needs of
  // its own is a way to type a name, since a collection is called something
  // rather than numbered.

  function openShelves() {
    if (!S.film) return;
    $('shTitle').textContent = titleOf(S.film);
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
        S.dirty = true;   // the library's shelf rows change with it
        // Putting a film on a shelf is a finished errand, so it goes back to
        // the film. Taking one off is usually part of tidying up, so that stays
        // put and the list can be worked through.
        if (!wasOn) openDetail(S.film, true);
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
              S.dirty = true;
              renderShelves();
              // The film is on the new shelf; there is nothing further to do
              // here, so return to it rather than to a list.
              openDetail(S.film, true);
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
    // Where to come back to when the film is over or Back is pressed.
    if (S.screen === 'library') { S.libPos = capturePos(); S.stack = []; }
    S.playFrom = S.screen;
    S.film = it;
    show('player');
    $('pTitle').textContent = titleOf(it);
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
          begin(j.playlistUrl.replace('/master.m3u8?', '/index.m3u8?'), startMs);
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
    attachCaptions(S.film);
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
    // Newer engines answer with a promise that rejects when the browser says
    // no; caught so the refusal does not surface as an unhandled error.
    try {
      if (document.documentElement.requestFullscreen) {
        var fs = document.documentElement.requestFullscreen();
        if (fs && fs['catch']) fs['catch'](function () {});
      }
    } catch (e) {}
    bumpHud();
    saveTimer = setInterval(function () { pushResume(false); }, 20000);
  }
  // ---- captions ---------------------------------------------------------
  // English text captions, when the film has any, as a WebVTT <track>. On or
  // off is remembered on this television: it is a property of who watches here.
  var CC_KEY = 'vault.tv.captions', ccTrack = null;
  function captionsWanted() { try { return localStorage.getItem(CC_KEY) === 'on'; } catch (e) { return false; } }
  function attachCaptions(it) {
    detachCaptions();
    if (!it || !it.captionsUrl) { $('ccHint').textContent = ''; return; }
    ccTrack = document.createElement('track');
    ccTrack.kind = 'subtitles';
    ccTrack.label = 'English';
    ccTrack.srclang = 'en';
    ccTrack.src = it.captionsUrl;
    v.appendChild(ccTrack);
    // Cues exist only once the file has loaded; place them then.
    ccTrack.addEventListener('load', applyCaptions);
    applyCaptions();
    // Some players reset track modes once the media loads; say it again.
    v.addEventListener('loadedmetadata', applyCaptions);
  }
  function detachCaptions() {
    v.removeEventListener('loadedmetadata', applyCaptions);
    if (ccTrack && ccTrack.parentNode) ccTrack.parentNode.removeChild(ccTrack);
    ccTrack = null;
  }
  function applyCaptions() {
    var on = captionsWanted();
    // Turn off anything else the player found, so captions never show twice.
    for (var i = 0; i < v.textTracks.length; i++) {
      var t = v.textTracks[i];
      t.mode = ccTrack && t === ccTrack.track && on ? 'showing' : 'disabled';
    }
    if (ccTrack) $('ccHint').textContent = ' · ▲ captions ' + (on ? 'on' : 'off');
    placeCaptions();
  }
  // The video draws captions along its bottom edge, which is exactly where the
  // controls sit, under their dark gradient. While the controls are up the
  // captions move above them; when the controls fade they go back down.
  function placeCaptions() {
    if (!ccTrack || !ccTrack.track || !ccTrack.track.cues) return;
    var up = $('hud').className !== 'hide';
    var cues = ccTrack.track.cues;
    for (var i = 0; i < cues.length; i++) {
      try { cues[i].line = up ? -5 : 'auto'; } catch (e) {}
    }
  }
  function toggleCaptions() {
    if (!ccTrack) return;
    try { localStorage.setItem(CC_KEY, captionsWanted() ? 'off' : 'on'); } catch (e) {}
    applyCaptions();
    bumpHud();
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
    placeCaptions();
    clearTimeout(hudTimer);
    hudTimer = setTimeout(function () {
      if (!v.paused) { $('hud').className = 'hide'; placeCaptions(); }
    }, 4000);
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

    detachCaptions();
    v.pause(); v.removeAttribute('src'); v.load();
    try { if (document.exitFullscreen && document.fullscreenElement) document.exitFullscreen(); } catch (e) {}
    returnFromPlayer();
  }

  // Back where the film was started from: its page, with the progress there
  // now true, or the library tile it was played from.
  function returnFromPlayer() {
    S.dirty = true;
    if (S.playFrom === 'detail' && S.film) { openDetail(S.film, true); return; }
    backToLibrary();
  }

  // ---- boot -------------------------------------------------------------
  rescale();
  tickClock();
  setInterval(tickClock, 5000);
  armBackTrap();
  buildKeys(); renderEntry();
  (function () {
    var tabs = $('tabs').getElementsByTagName('button');
    for (var i = 0; i < tabs.length; i++) (function (b) {
      b.addEventListener('click', function () { setTab(b.getAttribute('data-tab')); });
    })(tabs[i]);
    $('acct').addEventListener('click', openAccount);
  })();
  try { S.refresh = localStorage.getItem(LS); } catch (e) { S.memoryOnly = true; }
  if (S.refresh) refresh().then(function (ok) { if (ok) enter(); else show('signin'); });
  else show('signin');
})();
</script>
</body>
</html>
""";
}
