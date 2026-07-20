// Desktop media pipeline: probing files with ffprobe, deciding whether
// Chromium can play them directly, and converting the ones it can't
// (typically MKVs with AC-3/DTS audio) into a playable form.
//
// Conversion copies the video stream untouched whenever possible and only
// re-encodes the audio to AAC, so it is I/O-bound and fast. Results are
// cached by file identity (path + size + mtime) so each file converts once.
//
// This module is deliberately free of Electron imports so it can be
// unit-tested in plain Node.

const { execFile, spawn } = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');

let ffmpegPath = null;
let ffprobePath = null;
try { ffmpegPath = require('ffmpeg-static'); } catch {}
try { ffprobePath = require('ffprobe-static').path; } catch {}
// Packaged apps ship the binaries outside the asar archive.
const fixAsar = (p) => (p ? p.replace('app.asar', 'app.asar.unpacked') : p);
ffmpegPath = fixAsar(ffmpegPath);
ffprobePath = fixAsar(ffprobePath);

// What Chromium's <video> can handle natively.
const DIRECT_CONTAINERS = ['mp4', 'webm', 'matroska'];
// hevc is best-effort: Windows/macOS hardware decode usually covers it, and
// re-encoding it would be extremely slow, so we always copy rather than transcode.
const COPY_VIDEO = new Set(['h264', 'hevc', 'vp8', 'vp9', 'av1']);
const DIRECT_AUDIO = new Set(['aac', 'mp3', 'opus', 'vorbis', 'flac']);

function hasEngine() {
  return Boolean(ffmpegPath && ffprobePath);
}

function probe(file) {
  return new Promise((resolve, reject) => {
    if (!ffprobePath) return reject(new Error('ffprobe unavailable'));
    execFile(
      ffprobePath,
      ['-v', 'error', '-print_format', 'json', '-show_streams', '-show_format', file],
      { maxBuffer: 16 * 1024 * 1024, windowsHide: true },
      (err, stdout) => {
        if (err) return reject(err);
        try {
          resolve(JSON.parse(stdout));
        } catch (e) {
          reject(e);
        }
      }
    );
  });
}

const audioOk = (a) => !a || DIRECT_AUDIO.has(a.codec_name) || String(a.codec_name).startsWith('pcm');
const videoOk = (v) => !v || COPY_VIDEO.has(v.codec_name);

/** Probe a file and decide whether it can play directly in Chromium. */
async function analyze(file) {
  const info = await probe(file);
  const streams = info.streams || [];
  const video = streams.find((s) => s.codec_type === 'video');
  const audio = streams.find((s) => s.codec_type === 'audio');
  const fmt = String(info.format?.format_name || '');
  const direct =
    DIRECT_CONTAINERS.some((c) => fmt.includes(c)) && videoOk(video) && audioOk(audio);
  return { direct, video, audio, duration: Number(info.format?.duration) || 0 };
}

const inFlight = new Map(); // out path -> Promise
const activeProcs = new Set();

function cacheKeyFor(file) {
  const st = fs.statSync(file);
  return crypto.createHash('sha1').update(`${file}|${st.size}|${st.mtimeMs}`).digest('hex');
}

/**
 * Ensure `file` is playable. Returns { playPath, mode } where playPath is the
 * original file (direct play) or a converted file in cacheDir.
 */
async function preparePlayable(file, cacheDir, onProgress = () => {}) {
  if (!hasEngine()) return { playPath: file, mode: 'direct' };

  let analysis;
  try {
    analysis = await analyze(file);
  } catch {
    // Probe failed — let the <video> element try the original file.
    return { playPath: file, mode: 'direct' };
  }
  if (analysis.direct) return { playPath: file, mode: 'direct' };

  fs.mkdirSync(cacheDir, { recursive: true });
  const out = path.join(cacheDir, `${cacheKeyFor(file)}.mkv`);
  if (fs.existsSync(out)) return { playPath: out, mode: 'converted' };

  if (inFlight.has(out)) {
    await inFlight.get(out);
    return { playPath: out, mode: 'converted' };
  }

  const job = convert(file, out, analysis, onProgress);
  inFlight.set(out, job);
  try {
    await job;
  } finally {
    inFlight.delete(out);
  }
  return { playPath: out, mode: 'converted' };
}

function convert(file, out, analysis, onProgress) {
  return new Promise((resolve, reject) => {
    const tmp = `${out}.tmp`;
    const vCopy = videoOk(analysis.video);
    const aCopy = audioOk(analysis.audio);
    const args = [
      '-y', '-loglevel', 'error', '-nostats',
      '-i', file,
      '-map', '0:v:0?', '-map', '0:a:0?',
      '-c:v', vCopy ? 'copy' : 'libx264',
      ...(vCopy ? [] : ['-preset', 'veryfast', '-crf', '20']),
      '-c:a', aCopy ? 'copy' : 'aac',
      ...(aCopy ? [] : ['-b:a', '192k']),
      '-f', 'matroska',
      '-progress', 'pipe:1',
      tmp,
    ];
    onProgress({ percent: 0 });
    const proc = spawn(ffmpegPath, args, { windowsHide: true });
    activeProcs.add(proc);

    let stderrTail = '';
    proc.stderr.on('data', (d) => {
      stderrTail = (stderrTail + d.toString()).slice(-2000);
    });

    let buf = '';
    proc.stdout.on('data', (d) => {
      buf += d.toString();
      const lines = buf.split('\n');
      buf = lines.pop() || '';
      for (const line of lines) {
        const m = /^out_time_us=(\d+)/.exec(line.trim());
        if (m && analysis.duration > 0) {
          const pct = Math.min(99, Math.round((Number(m[1]) / 1e6 / analysis.duration) * 100));
          onProgress({ percent: pct });
        }
      }
    });

    proc.on('error', (e) => {
      activeProcs.delete(proc);
      reject(e);
    });
    proc.on('close', (code) => {
      activeProcs.delete(proc);
      if (code === 0) {
        try {
          fs.renameSync(tmp, out);
        } catch (e) {
          return reject(e);
        }
        onProgress({ percent: 100 });
        resolve();
      } else {
        try { fs.unlinkSync(tmp); } catch {}
        const lastLine = stderrTail.split('\n').filter(Boolean).pop();
        reject(new Error(`Conversion failed: ${lastLine || `ffmpeg exited with ${code}`}`));
      }
    });
  });
}

/**
 * Stream `file` starting at `startSeconds` straight into an HTTP response as
 * Matroska: video copied, audio re-encoded to AAC on the fly. Playback starts
 * within seconds — no upfront conversion. The ffmpeg process dies with the
 * response (seek restarts, video switches, app close), and stdout backpressure
 * keeps it from racing ahead of what the player actually consumes.
 */
function streamTo(file, startSeconds, res, analysis) {
  const vCopy = videoOk(analysis?.video);
  const aCopy = audioOk(analysis?.audio);
  const args = [
    '-loglevel', 'error', '-nostats',
    ...(startSeconds > 0 ? ['-ss', String(startSeconds)] : []),
    '-i', file,
    '-map', '0:v:0?', '-map', '0:a:0?',
    '-c:v', vCopy ? 'copy' : 'libx264',
    ...(vCopy ? [] : ['-preset', 'veryfast', '-crf', '23']),
    '-c:a', aCopy ? 'copy' : 'aac',
    ...(aCopy ? [] : ['-b:a', '192k']),
    '-f', 'matroska',
    '-',
  ];
  const proc = spawn(ffmpegPath, args, { windowsHide: true });
  activeProcs.add(proc);

  let stderrTail = '';
  proc.stderr.on('data', (d) => {
    stderrTail = (stderrTail + d.toString()).slice(-1000);
  });
  proc.stdout.pipe(res);

  const kill = () => {
    activeProcs.delete(proc);
    try { proc.kill('SIGKILL'); } catch {}
  };
  res.on('close', kill);
  proc.on('close', (code) => {
    activeProcs.delete(proc);
    if (code !== 0 && code !== null && stderrTail) {
      console.error('[media] stream ffmpeg exited', code, stderrTail.split('\n').filter(Boolean).pop());
    }
    try { res.end(); } catch {}
  });
  return proc;
}

/** Path of an existing converted cache file for `file`, or null. */
function findCached(file, cacheDir) {
  try {
    const out = path.join(cacheDir, `${cacheKeyFor(file)}.mkv`);
    return fs.existsSync(out) ? out : null;
  } catch {
    return null;
  }
}

/** Delete oldest cache files until the cache is under maxBytes. */
function pruneCache(cacheDir, maxBytes = 3 * 1024 ** 3) {
  try {
    if (!fs.existsSync(cacheDir)) return;
    const files = fs
      .readdirSync(cacheDir)
      .map((name) => {
        const full = path.join(cacheDir, name);
        try {
          const st = fs.statSync(full);
          return st.isFile() ? { full, size: st.size, mtime: st.mtimeMs } : null;
        } catch {
          return null;
        }
      })
      .filter(Boolean)
      .sort((a, b) => a.mtime - b.mtime);
    let total = files.reduce((sum, f) => sum + f.size, 0);
    for (const f of files) {
      if (total <= maxBytes) break;
      try {
        fs.unlinkSync(f.full);
        total -= f.size;
      } catch {}
    }
  } catch {}
}

/** Kill any in-flight conversions (called on app quit). */
function killAll() {
  for (const p of activeProcs) {
    try { p.kill('SIGKILL'); } catch {}
  }
  activeProcs.clear();
}

module.exports = { hasEngine, analyze, preparePlayable, streamTo, findCached, pruneCache, killAll };
