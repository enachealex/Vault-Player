/**
 * Web / Electron thumbnail generation. Grabs a frame from the video with a
 * hidden <video> + <canvas>, caches the result in memory and IndexedDB (so
 * recents keep their poster across sessions), and limits how many decode at
 * once to keep large folders responsive.
 */
import * as folder from './videoFolder';

export interface ThumbSource {
  id: string;
  name: string;
  uri: string;
  kind: string;
  folderName?: string;
}

const memCache = new Map<string, string | null>();
const inFlight = new Map<string, Promise<string | null>>();

const keyOf = (item: ThumbSource) => `${item.folderName ?? ''}::${item.name}`;

// ---- Concurrency gate ----------------------------------------------------
const MAX_CONCURRENT = 3;
let active = 0;
const queue: (() => void)[] = [];

function acquire(): Promise<void> {
  if (active < MAX_CONCURRENT) {
    active++;
    return Promise.resolve();
  }
  return new Promise((resolve) => queue.push(resolve));
}
function release() {
  active--;
  const next = queue.shift();
  if (next) {
    active++;
    next();
  }
}

// ---- Public API ----------------------------------------------------------
export async function getThumbnail(item: ThumbSource): Promise<string | null> {
  const key = keyOf(item);
  if (memCache.has(key)) return memCache.get(key) ?? null;
  const existing = inFlight.get(key);
  if (existing) return existing;

  const task = (async () => {
    const cached = await idbGet(key);
    if (cached) {
      memCache.set(key, cached);
      return cached;
    }
    let url: string | null = null;
    try {
      // Direct stream only — must never trigger an ffmpeg conversion per row.
      url = await folder.resolveThumbUrl(item as any);
    } catch {
      memCache.set(key, null);
      return null;
    }
    await acquire();
    try {
      const dataUrl = await captureFrame(url);
      memCache.set(key, dataUrl);
      if (dataUrl) idbSet(key, dataUrl).catch(() => {});
      return dataUrl;
    } catch {
      memCache.set(key, null);
      return null;
    } finally {
      release();
      folder.releaseUrl(url);
    }
  })();

  inFlight.set(key, task);
  try {
    return await task;
  } finally {
    inFlight.delete(key);
  }
}

function captureFrame(url: string): Promise<string | null> {
  return new Promise((resolve) => {
    const video = document.createElement('video');
    video.muted = true;
    video.preload = 'metadata';
    video.crossOrigin = 'anonymous';
    video.src = url;

    let settled = false;
    const done = (result: string | null) => {
      if (settled) return;
      settled = true;
      video.removeAttribute('src');
      video.load();
      resolve(result);
    };
    const timer = setTimeout(() => done(null), 8000);

    video.onloadeddata = () => {
      const seekTo = Math.min(1, (video.duration || 2) * 0.1) || 0.1;
      try {
        video.currentTime = seekTo;
      } catch {
        done(null);
      }
    };
    video.onseeked = () => {
      try {
        const w = video.videoWidth;
        const h = video.videoHeight;
        if (!w || !h) return done(null);
        const targetW = 400;
        const scale = targetW / w;
        const canvas = document.createElement('canvas');
        canvas.width = targetW;
        canvas.height = Math.round(h * scale);
        const ctx = canvas.getContext('2d');
        if (!ctx) return done(null);
        ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
        clearTimeout(timer);
        done(canvas.toDataURL('image/jpeg', 0.72));
      } catch {
        clearTimeout(timer);
        done(null);
      }
    };
    video.onerror = () => {
      clearTimeout(timer);
      done(null);
    };
  });
}

// ---- IndexedDB cache -----------------------------------------------------
const DB_NAME = 'vp-thumbs';
const STORE = 'thumbs';

function openDb(): Promise<IDBDatabase | null> {
  return new Promise((resolve) => {
    if (typeof indexedDB === 'undefined') return resolve(null);
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => resolve(null);
  });
}

async function idbGet(key: string): Promise<string | null> {
  const db = await openDb();
  if (!db) return null;
  return new Promise((resolve) => {
    const tx = db.transaction(STORE, 'readonly');
    const req = tx.objectStore(STORE).get(key);
    req.onsuccess = () => resolve((req.result as string) ?? null);
    req.onerror = () => resolve(null);
  });
}

async function idbSet(key: string, value: string): Promise<void> {
  const db = await openDb();
  if (!db) return;
  await new Promise<void>((resolve) => {
    const tx = db.transaction(STORE, 'readwrite');
    tx.objectStore(STORE).put(value, key);
    tx.oncomplete = () => resolve();
    tx.onerror = () => resolve();
  });
}
