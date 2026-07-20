// Electron main process. Wraps the exported Expo Web build into a desktop app.
//
// The build is served over http://127.0.0.1 (via a tiny built-in static
// server) rather than file://, because Chromium disables the File System
// Access API (window.showDirectoryPicker) on file:// origins.
//
// On desktop, folder access goes through Electron's native dialog instead of
// the File System Access API (see preload.js). That gives us real file paths,
// which the same local server streams back with Range support — and lets the
// media pipeline (media.js) convert files whose audio Chromium can't decode
// (MKV with AC-3/DTS being the classic case).

const { app, BrowserWindow, dialog, ipcMain, shell } = require('electron');
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
const path = require('path');
const media = require('./media');
const { serveMedia } = require('./mediaServer');

const isDev = process.env.ELECTRON_DEV === '1';
const DEV_URL = process.env.ELECTRON_DEV_URL || 'http://localhost:8081';
const DIST_DIR = path.join(__dirname, '..', 'dist');

// Random per-run token so only our renderer can read local files via /media.
const TOKEN = crypto.randomBytes(24).toString('hex');
let serverBase = null; // e.g. http://127.0.0.1:52341
let mainWindow = null;

const VIDEO_EXTS = new Set(['mp4', 'm4v', 'mov', 'mkv', 'webm', 'avi', 'wmv', 'flv', 'mpg', 'mpeg', 'ogv', '3gp', 'ts']);
const SUB_EXTS = new Set(['srt', 'vtt']);

const STATIC_MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.map': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.ttf': 'font/ttf',
  '.otf': 'font/otf',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

// ---- Persistent desktop state (last opened folder) -----------------------

const stateFile = () => path.join(app.getPath('userData'), 'vp-state.json');

function loadState() {
  try {
    return JSON.parse(fs.readFileSync(stateFile(), 'utf8'));
  } catch {
    return {};
  }
}

function saveState(patch) {
  try {
    fs.writeFileSync(stateFile(), JSON.stringify({ ...loadState(), ...patch }));
  } catch {}
}

const cacheDir = () => path.join(app.getPath('userData'), 'convert-cache');

// ---- Local HTTP server: static build + media streaming --------------------

function mediaUrl(filePath) {
  return `${serverBase}/media?src=${encodeURIComponent(filePath)}&token=${TOKEN}`;
}

function serveStatic(pathname, res) {
  try {
    let rel = pathname === '/' ? 'index.html' : pathname.replace(/^\/+/, '');
    let filePath = path.join(DIST_DIR, rel);

    // Prevent path traversal outside the build directory.
    if (!filePath.startsWith(DIST_DIR)) {
      res.writeHead(403);
      return res.end('Forbidden');
    }

    const ext = path.extname(filePath).toLowerCase();
    // SPA fallback: extensionless routes serve index.html.
    if (!fs.existsSync(filePath) && ext === '') {
      filePath = path.join(DIST_DIR, 'index.html');
    }
    if (!fs.existsSync(filePath) || fs.statSync(filePath).isDirectory()) {
      res.writeHead(404);
      return res.end('Not found');
    }

    res.writeHead(200, {
      'Content-Type': STATIC_MIME[path.extname(filePath).toLowerCase()] || 'application/octet-stream',
      'Cache-Control': 'no-cache',
    });
    fs.createReadStream(filePath).pipe(res);
  } catch {
    res.writeHead(500);
    res.end('Server error');
  }
}

// Probe results per file, so stream seek restarts don't re-run ffprobe.
const analysisCache = new Map();

async function analyzeCached(file) {
  if (analysisCache.has(file)) return analysisCache.get(file);
  const a = await media.analyze(file);
  analysisCache.set(file, a);
  return a;
}

function streamUrl(filePath, t) {
  return `${serverBase}/stream?src=${encodeURIComponent(filePath)}&t=${encodeURIComponent(t)}&token=${TOKEN}`;
}

/**
 * Live transcode stream: ffmpeg starts at `t`, video copied, audio → AAC,
 * piped straight to the response. Unseekable by design — the renderer
 * restarts the stream at a new `t` to seek.
 */
async function serveStream(u, res) {
  if (u.searchParams.get('token') !== TOKEN) {
    res.writeHead(403);
    return res.end('Forbidden');
  }
  const src = u.searchParams.get('src') || '';
  const t = Math.max(0, Number(u.searchParams.get('t')) || 0);
  if (!fs.existsSync(src) || !fs.statSync(src).isFile()) {
    res.writeHead(404);
    return res.end('Not found');
  }
  let analysis = null;
  try {
    analysis = await analyzeCached(src);
  } catch {}
  res.writeHead(200, {
    'Content-Type': 'video/x-matroska',
    'Accept-Ranges': 'none',
    'Access-Control-Allow-Origin': '*',
    'Cache-Control': 'no-store',
  });
  media.streamTo(src, t, res, analysis);
}

function startServer() {
  return new Promise((resolve, reject) => {
    const server = http.createServer((req, res) => {
      try {
        const u = new URL(req.url || '/', 'http://127.0.0.1');
        if (u.pathname === '/media') return serveMedia(u, req, res, TOKEN);
        if (u.pathname === '/stream') return void serveStream(u, res);
        serveStatic(decodeURIComponent(u.pathname), res);
      } catch {
        res.writeHead(500);
        res.end('Server error');
      }
    });
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      resolve(`http://127.0.0.1:${port}`);
    });
  });
}

// ---- IPC: folder picking, media preparation, subtitle reading -------------

function scanFolder(dir) {
  const out = { folderName: path.basename(dir) || dir, path: dir, files: [] };
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  for (const e of entries) {
    if (!e.isFile()) continue;
    const ext = path.extname(e.name).slice(1).toLowerCase();
    if (!VIDEO_EXTS.has(ext) && !SUB_EXTS.has(ext)) continue;
    const full = path.join(dir, e.name);
    let size = 0;
    try {
      size = fs.statSync(full).size;
    } catch {}
    out.files.push({ name: e.name, path: full, size });
  }
  return out;
}

function registerIpc() {
  ipcMain.handle('vp-pick-folder', async () => {
    const r = await dialog.showOpenDialog(mainWindow, { properties: ['openDirectory'] });
    if (r.canceled || !r.filePaths[0]) return null;
    const scan = scanFolder(r.filePaths[0]);
    saveState({ lastFolder: r.filePaths[0] });
    return scan;
  });

  ipcMain.handle('vp-get-last-folder', () => {
    const lf = loadState().lastFolder;
    return lf && fs.existsSync(lf) ? lf : null;
  });

  ipcMain.handle('vp-reopen-last', () => {
    const lf = loadState().lastFolder;
    if (!lf || !fs.existsSync(lf)) return null;
    return scanFolder(lf);
  });

  ipcMain.handle('vp-direct-url', (_e, p) => mediaUrl(p));

  // Decide how to play a file. Direct-playable files and previously converted
  // cache hits stream as plain files (natively seekable). Everything else
  // plays instantly as a live ffmpeg stream — no upfront conversion wait.
  ipcMain.handle('vp-prepare-media', async (_e, p, startAt = 0) => {
    if (!media.hasEngine()) return { url: mediaUrl(p), mode: 'direct' };
    let analysis;
    try {
      analysis = await analyzeCached(p);
    } catch {
      return { url: mediaUrl(p), mode: 'direct' };
    }
    if (analysis.direct) return { url: mediaUrl(p), mode: 'direct' };
    const cached = media.findCached(p, cacheDir());
    if (cached) return { url: mediaUrl(cached), mode: 'converted' };
    return {
      url: streamUrl(p, startAt),
      mode: 'stream',
      durationSeconds: analysis.duration,
      streamKey: p,
    };
  });

  ipcMain.handle('vp-stream-url', (_e, p, t) => streamUrl(p, Math.max(0, Number(t) || 0)));

  ipcMain.handle('vp-read-text', (_e, p) => {
    const ext = path.extname(p).slice(1).toLowerCase();
    if (!SUB_EXTS.has(ext)) throw new Error('Not a subtitle file');
    const st = fs.statSync(p);
    if (st.size > 5 * 1024 * 1024) throw new Error('Subtitle file too large');
    return fs.readFileSync(p, 'utf8');
  });
}

// ---- Window ----------------------------------------------------------------

async function createWindow() {
  mainWindow = new BrowserWindow({
    width: 1280,
    height: 820,
    minWidth: 720,
    minHeight: 480,
    backgroundColor: '#0d0f14',
    title: 'Video Player',
    icon: path.join(__dirname, '..', 'assets', 'icon.png'),
    autoHideMenuBar: true,
    show: false,
    webPreferences: {
      preload: path.join(__dirname, 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      // Let videos start playing on their own without a click, and allow the
      // player to autoplay the next video in a playlist.
      autoplayPolicy: 'no-user-gesture-required',
    },
  });

  mainWindow.once('ready-to-show', () => mainWindow.show());

  mainWindow.webContents.on('did-finish-load', () =>
    console.log('[video-player] loaded:', mainWindow.webContents.getURL())
  );
  mainWindow.webContents.on('did-fail-load', (_e, code, desc, url) =>
    console.error('[video-player] failed to load', url, code, desc)
  );

  // Open external links in the user's browser, not inside the app.
  mainWindow.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('http')) shell.openExternal(url);
    return { action: 'deny' };
  });

  await mainWindow.loadURL(isDev ? DEV_URL : serverBase);
}

app.whenReady().then(async () => {
  media.pruneCache(cacheDir());
  serverBase = await startServer();
  registerIpc();
  await createWindow();
});

app.on('activate', () => {
  if (BrowserWindow.getAllWindows().length === 0) createWindow();
});

app.on('before-quit', () => media.killAll());

app.on('window-all-closed', () => {
  if (process.platform !== 'darwin') app.quit();
});
