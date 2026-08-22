namespace VideoPlayer.Rendezvous;

/// <summary>
/// How to run a Vault library on hardware you own, served at GET /setup.
///
/// Reached from the desktop app's setup screen ("How do I set one up?") and
/// from a link somebody sends a friend. Three routes in descending order of how
/// many people can follow them, and the honest limits of each stated where they
/// apply rather than at the bottom.
///
/// The point of the page is the sentence at the end: the films live on the
/// reader's own disk under every one of these options. Nothing here uploads
/// anything to us, and there is no option that does.
/// </summary>
public static class SetupPage
{
    public static string Render(string origin) => Template.Replace("__ORIGIN__", origin);

    private const string Template = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Run your own Vault library</title>
<style>
  :root { --bg:#1A0D09; --surface:#24100B; --line:#43281F;
          --text:#F6ECE5; --muted:#D9C0B3; --accent:#C8442A; --warm:#F0A070; }
  * { box-sizing:border-box; }
  body { margin:0; background:var(--bg); color:var(--text); line-height:1.6;
         font-family:'Segoe UI',Roboto,system-ui,-apple-system,sans-serif; }
  .wrap { max-width:46rem; margin:0 auto; padding:3rem 1.5rem 5rem; }
  h1 { font-size:2rem; margin:0 0 .4rem; }
  h2 { font-size:1.3rem; margin:3rem 0 .2rem; color:var(--warm); }
  h3 { font-size:1.02rem; margin:1.8rem 0 .4rem; }
  p, li { color:var(--muted); }
  li { margin:.4rem 0; }
  ol, ul { padding-left:1.3rem; }
  code { background:var(--surface); border:1px solid var(--line); border-radius:.3rem;
         padding:.15rem .4rem; font-size:.9em; color:var(--text); word-break:break-word; }
  pre { background:var(--surface); border:1px solid var(--line); border-radius:.5rem;
        padding:.9rem 1rem; overflow-x:auto; }
  pre code { background:none; border:none; padding:0; word-break:normal; }
  .lead { font-size:1.05rem; }
  .card { background:var(--surface); border:1px solid var(--line); border-radius:.8rem;
          padding:1.2rem 1.4rem; margin:1.2rem 0; }
  .card h3 { margin-top:0; }
  .tag { display:inline-block; font-size:.72rem; font-weight:700; letter-spacing:.06em;
         text-transform:uppercase; color:var(--bg); background:var(--warm);
         border-radius:.3rem; padding:.15rem .5rem; margin-left:.5rem;
         vertical-align:.15em; }
  .note { border-left:3px solid var(--warm); padding:.2rem 0 .2rem 1rem; margin:1.4rem 0; }
  .warn { border-left:3px solid var(--accent); padding:.2rem 0 .2rem 1rem; margin:1.4rem 0; }
  table { border-collapse:collapse; width:100%; margin:1rem 0; font-size:.94rem; }
  th, td { text-align:left; padding:.55rem .7rem; border-bottom:1px solid var(--line);
           color:var(--muted); vertical-align:top; }
  th { color:var(--text); font-size:.78rem; text-transform:uppercase; letter-spacing:.05em; }
  .privacy { background:var(--surface); border:1px solid var(--line); border-radius:.8rem;
             padding:1.4rem 1.5rem; margin:3rem 0 0; }
  .privacy h2 { margin-top:0; }
  a { color:var(--warm); }
</style>
</head>
<body>
<div class="wrap">

  <h1>Run your own Vault library</h1>
  <p class="lead">Your films, on a machine you own, watchable on your PC, your
  phone and your television. Pick whichever of these three matches the hardware
  you already have — they all end up in the same place.</p>

  <h2>Which one is for you</h2>
  <table>
    <tr><th>If you have…</th><th>Do this</th><th>Films reachable</th></tr>
    <tr>
      <td>A Windows PC with your films on it</td>
      <td><a href="#app">The app hosts it</a></td>
      <td>While that PC is awake</td>
    </tr>
    <tr>
      <td>A NAS, a home server, an old PC, a Raspberry Pi</td>
      <td><a href="#docker">Docker</a></td>
      <td>Always, at home</td>
    </tr>
    <tr>
      <td>A spare Windows machine you want left on</td>
      <td><a href="#windows">The server on its own</a></td>
      <td>Always, at home</td>
    </tr>
  </table>
  <p>Any of them can additionally be reached from outside your house — see
  <a href="#outside">watching away from home</a>, and read it before you open
  anything to the internet.</p>

  <h2 id="app">1. Let the app host it <span class="tag">easiest</span></h2>
  <p>Nothing to install beyond Vault Movies itself. The desktop app carries the
  server inside it.</p>
  <ol>
    <li>Open Vault Movies, then <b>Settings &rarr; Your films &rarr; Change</b>.</li>
    <li>Choose <b>Host my films on this PC</b> and point it at your films folder.</li>
    <li>Leave <b>Let other devices on my home network watch</b> ticked if you want
        the TV and phones to work. The first time, Windows asks whether to allow
        Vault Movies on your network — say yes, for <b>Private networks</b>.</li>
    <li>Press <b>Start hosting</b>. It shows you the address to use elsewhere in
        the house, something like <code>http://192.168.1.20:5555</code>.</li>
    <li>Create your account in the app. <b>The first account made on a new
        library becomes its owner</b> — make it yours before anyone else signs up.</li>
  </ol>
  <p>Films are indexed where they sit. Nothing is copied, moved or renamed, and
  the folder is only ever read from.</p>
  <div class="note">
    <p><b>The catch, stated plainly.</b> The library is only up while that PC is
    awake and the app is open. If your household watches at odd hours, one of the
    next two is the better answer.</p>
  </div>

  <h2 id="docker">2. Docker, on anything that stays on</h2>
  <p>A NAS with Docker (Synology, QNAP, Unraid, TrueNAS), a Raspberry Pi 5, an
  old laptop with Linux, or a rented box. You need Docker and about ten minutes.</p>

  <h3>Get the code onto it</h3>
<pre><code>git clone https://github.com/enachealex/Vault-Player.git
cd Vault-Player/v2</code></pre>

  <h3>Tell it where your films are</h3>
  <p>Two files. First <code>deploy/.env</code>, which decides what gets mounted:</p>
<pre><code>MEDIA_DIR=/mnt/storage/films
HLS_CACHE_DIR=/mnt/storage/.vault-hls</code></pre>
  <p><code>MEDIA_DIR</code> is your film folder — wherever it already is. It is
  mounted <b>read-only</b>, so nothing the server does can alter or delete
  anything in it. <code>HLS_CACHE_DIR</code> holds browser-playable copies made
  for phones and televisions; give it a disk with tens of gigabytes spare. It is
  pure cache and safe to delete at any time.</p>

  <p>Then <code>deploy/vault.env</code>, which holds the secrets:</p>
<pre><code>cp deploy/vault.env.example deploy/vault.env
openssl rand -base64 48        # paste into VAULT_JWT_SECRET</code></pre>
  <div class="warn">
    <p><b>Do not skip the signing key.</b> Without one the server falls back to a
    key published in its own source code, and anyone who has read it can mint a
    valid login for your library.</p>
  </div>

  <h3>Start it</h3>
<pre><code>docker compose -f deploy/docker-compose.yml up -d --build
curl http://localhost:5555/health     # -> ok</code></pre>
  <p>The first scan reads every file to work out runtimes and cut posters. A
  large library takes a few minutes; it serves films while it works.</p>

  <h3>Point the app at it</h3>
  <p>In Vault Movies: <b>Settings &rarr; Your films &rarr; Change &rarr; Connect to
  a server I run</b>, and enter <code>http://&lt;that machine's address&gt;:5555</code>.
  Then create your account — <b>the first one becomes the owner</b>.</p>

  <h2 id="windows">3. The server on its own, on Windows</h2>
  <p>For a spare Windows machine you want to leave running without the player
  open on it. The server ships inside the app's installation folder.</p>
  <ol>
    <li>Install Vault Movies on that machine.</li>
    <li>Find <code>server\VideoPlayer.Rendezvous.exe</code> inside the
        installation folder.</li>
    <li>Run it with your folders in the environment:</li>
  </ol>
<pre><code>$env:VAULT_MEDIA     = "D:\Films"
$env:VAULT_UPLOADS   = "D:\Films"
$env:VAULT_DB        = "D:\VaultServer\vault.db"
$env:VAULT_HLS_CACHE = "D:\VaultServer\hls"
$env:VAULT_JWT_SECRET = [Convert]::ToBase64String((1..48 | ForEach-Object { Get-Random -Max 256 }))
.\server\VideoPlayer.Rendezvous.exe</code></pre>
  <p>Keep that signing key somewhere and reuse it: change it and everybody is
  signed out. To have it start with the machine, wrap it in a scheduled task set
  to run at boot whether or not anyone logs in.</p>

  <h2>Getting films onto it</h2>
  <p>Two ways, and the folder layout is the same either way — one film per file,
  in the folder you pointed <code>VAULT_MEDIA</code> at. Subfolders are fine.</p>
  <ul>
    <li><b>Copy them over the network.</b> A Windows share, an SMB mount, rsync,
        or dragging them onto a NAS in a file browser. Then press the
        <b>Vault</b> button above your library in the app, which asks the server
        to look again.</li>
    <li><b>Upload from the app.</b> Signed in as the owner, Vault Movies can push
        films to the server over the network, resuming where it left off if the
        connection drops. Uploads land in the films folder and appear once the
        scan notices them.</li>
  </ul>
  <p>Name files the way you want them read: <code>The Third Man (1949).mkv</code>
  shows up as <i>The Third Man</i>. Almost every container works — MKV, MP4, AVI,
  MOV, WebM, TS — and anything a browser cannot open is repackaged on the fly for
  phones and televisions.</p>

  <h2>Your television</h2>
  <p>Once the server is up, open <code>http://&lt;server address&gt;/tv</code> in
  the TV's browser, or <a href="__ORIGIN__/tv/install">install it as a proper LG
  app</a> so the remote's arrow keys work.</p>

  <h2 id="outside">Watching away from home</h2>
  <p>Everything above is reachable on your own network only. Reaching it from
  elsewhere means one of:</p>
  <ul>
    <li><b>A tunnel</b> — Cloudflare Tunnel or Tailscale. Nothing is opened on
        your router, and both handle encryption for you. This is the one to pick.</li>
    <li><b>A port forward plus a reverse proxy</b> — the included Caddy
        configuration will get you a certificate automatically if you have a
        domain name.</li>
  </ul>
  <div class="warn">
    <p><b>Do not simply forward port 5555.</b> The server speaks plain HTTP by
    design and expects something in front of it to handle TLS. Exposed directly,
    your password and everything you watch cross the internet in the clear.</p>
  </div>

  <div class="privacy">
    <h2>What we hold</h2>
    <p>Nothing of yours. Under every option on this page your films sit on a disk
    you own and stream from it to your own devices. They are never uploaded to
    us. We hold no copy of them, no list of their titles, and no record of what
    you watch or when.</p>
    <p>If you host the library yourself, your accounts and watch history live in
    a SQLite file on your own machine too — the one at <code>VAULT_DB</code>. Back
    that file up and you have backed up everything.</p>
    <p>Our own server, at <code>party.thejumpvault.com</code>, exists to pass
    Watch Party messages between people watching together and to serve the
    television app. If you use somebody's shared library, you have an account on
    <i>their</i> server, not on ours.</p>
  </div>

  <p style="margin-top:2.5rem"><a href="__ORIGIN__/tv/install">Set up your television &rarr;</a></p>

</div>
</body>
</html>
""";
}
