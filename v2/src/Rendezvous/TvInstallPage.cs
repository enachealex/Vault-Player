namespace VideoPlayer.Rendezvous;

/// <summary>
/// Instructions for putting the television app on an LG set, served at
/// GET /tv/install and read on a phone or a computer — never on the television,
/// which is the whole point of the page.
///
/// webOS cannot install an app from a file downloaded in its own browser and has
/// no add-to-home-screen, so sideloading from a computer over the network is the
/// only route. Rather than leave that as folklore, the steps and the package
/// live at a URL that can be sent to somebody.
/// </summary>
public static class TvInstallPage
{
    public static string Render(string origin) => Template.Replace("__ORIGIN__", origin);

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Vault Movies on your LG TV</title>
<style>
  /* The app's palette: smoked glass on a dusk sky. */
  :root { --bg:#0B0D12; --surface:rgba(255,255,255,.06); --line:rgba(255,255,255,.12);
          --text:#F4F6FA; --muted:#A6AFBD; --accent:#005ED9; --link:#6CB2FF;
          --warm:#F5C77E; --danger:#FF7A7A; }
  * { box-sizing:border-box; }
  body { margin:0; background:radial-gradient(ellipse 70% 60% at 82% 8%, rgba(122,60,36,.5), rgba(11,13,18,0) 70%), radial-gradient(ellipse 80% 70% at 8% 100%, rgba(26,58,110,.5), rgba(11,13,18,0) 70%), #0B0D12; background-attachment:fixed; color:var(--text); line-height:1.6;
         font-family:'Segoe UI',Roboto,system-ui,-apple-system,sans-serif; }
  .wrap { max-width:44rem; margin:0 auto; padding:3rem 1.5rem 5rem; }
  h1 { font-size:2rem; margin:0 0 .4rem; }
  h2 { font-size:1.15rem; margin:2.4rem 0 .6rem; color:var(--text); }
  p, li { color:var(--muted); }
  li { margin:.4rem 0; }
  code { background:var(--surface); border:1px solid var(--line); border-radius:.3rem;
         padding:.15rem .4rem; font-size:.9em; color:var(--text); }
  pre { background:rgba(0,0,0,.35); border:1px solid var(--line); border-radius:.8rem;
        padding:.9rem 1rem; overflow-x:auto; }
  pre code { background:none; border:none; padding:0; }
  .dl { display:inline-block; background:var(--text); color:var(--bg); text-decoration:none;
        border-radius:.75rem; padding:.8rem 1.6rem; font-weight:700; margin:.6rem 0 0; }
  .note { border-left:3px solid var(--warm); padding:.2rem 0 .2rem 1rem; margin:1.4rem 0; }
</style>
</head>
<body>
<div class="wrap">

  <h1>Vault Movies on your LG TV</h1>
  <p>Puts a tile on the television's home screen, so it opens like any other app.</p>

  <div class="note">
    <p><b>Worth doing for more than the icon.</b> In LG's web browser the arrow
    keys drive the browser's own cursor and never reach the page, which makes
    signing in with the remote painful. In an installed app they go straight to
    the page and the D-pad works properly.</p>
  </div>

  <h2>What you are installing</h2>
  <p>Almost nothing — an icon, a name, and a redirect. The app itself is served
  from this server and always will be, so <b>the television picks up
  improvements on its own</b> and never needs reinstalling. It also tries your
  home network first, so at home it takes one hop instead of a round trip
  through the internet.</p>

  <a class="dl" href="__ORIGIN__/tv/app.ipk">Download the package</a>

  <h2>1. Put the TV in Developer Mode</h2>
  <ul>
    <li>On the TV, open the <b>LG Content Store</b> and install <b>Developer Mode</b>.</li>
    <li>Open it and sign in with a free LG developer account from
        <code>webostv.developer.lge.com</code>.</li>
    <li>Turn <b>Dev Mode Status</b> on. The TV restarts.</li>
    <li>Reopen it and note the <b>passphrase</b> and the TV's IP address.</li>
  </ul>

  <h2>2. On your computer</h2>
  <p>Needs Node.js. Everything from here runs on the computer, not the TV.</p>
<pre><code>npm install -g @webos-tools/cli
ares-setup-device        # name: tv, the TV's IP, port 9922, user prisoner
ares-novacom --device tv --getkey     # asks for the passphrase</code></pre>

  <h2>3. Install it</h2>
<pre><code>ares-install --device tv com.jumpvault.vaultmovies_1.0.0_all.ipk</code></pre>
  <p>The tile appears on the home screen.</p>

  <h2>The one ongoing cost</h2>
  <p>LG's Developer Mode expires after <b>1000 hours, about 41 days</b>, and when
  it does, every app installed this way is removed from the television. The Dev
  Mode app on the TV has an <b>Extend</b> button that resets the clock. To stop
  thinking about it, run this from the computer on a monthly schedule:</p>
<pre><code>ares-launch --device tv com.palmdts.devmode -p '{"extend":"true"}'</code></pre>

  <p style="margin-top:2.5rem"><a href="__ORIGIN__/tv" style="color:var(--warm)">
  Or just open the app in a browser &rarr;</a></p>

</div>
</body>
</html>
""";
}
