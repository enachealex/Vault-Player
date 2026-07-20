/**
 * Web / Electron folder access.
 *
 * In the Electron desktop app a `desktopBridge` (see electron/preload.js) is
 * present: folders are picked with the native dialog, files stream from the
 * app's local server by real path, and unplayable audio (MKV + AC-3/DTS) is
 * converted by ffmpeg in the main process before playback.
 *
 * In a plain browser we use the File System Access API, with a graceful
 * fallback to a hidden <input webkitdirectory>. Playback URLs are short-lived
 * object URLs created on demand from the underlying File.
 */
import { hasVideoExtension, PlaybackSource, SubtitleTrack, SUBTITLE_EXTENSIONS, VideoItem } from '../types';

export interface OpenFolderResult {
  folderName: string;
  videos: VideoItem[];
}

interface DesktopFolderResult {
  folderName: string;
  path: string;
  files: { name: string; path: string; size: number }[];
}

interface DesktopBridge {
  pickFolder(): Promise<DesktopFolderResult | null>;
  getLastFolder(): Promise<string | null>;
  reopenLast(): Promise<DesktopFolderResult | null>;
  prepareMedia(path: string, startAt?: number): Promise<PlaybackSource>;
  streamUrl(path: string, t: number): Promise<string>;
  directUrl(path: string): Promise<string>;
  readText(path: string): Promise<string>;
  onPrepareProgress(cb: (p: { percent: number | null }) => void): () => void;
}

const bridge = (): DesktopBridge | null =>
  ((globalThis as any).desktopBridge as DesktopBridge | undefined) ?? null;

type Entry = { file?: File; handle?: FileSystemFileHandle; path?: string; size?: number };

// Session registry mapping VideoItem.id -> how to fetch its File.
const registry = new Map<string, Entry>();
// Session registry mapping SubtitleTrack.id -> how to fetch its text.
const subRegistry = new Map<string, Entry>();
let lastDirHandle: FileSystemDirectoryHandle | null = null;

let counter = 0;
const nextId = () => `web-${Date.now()}-${counter++}`;

const LANG_NAMES: Record<string, string> = {
  en: 'English', eng: 'English', es: 'Spanish', spa: 'Spanish', fr: 'French', fre: 'French',
  de: 'German', ger: 'German', it: 'Italian', pt: 'Portuguese', ru: 'Russian', ja: 'Japanese',
  ko: 'Korean', zh: 'Chinese', ar: 'Arabic', hi: 'Hindi', nl: 'Dutch', pl: 'Polish', tr: 'Turkish',
};

const stripExt = (name: string) => {
  const dot = name.lastIndexOf('.');
  return dot < 0 ? name : name.slice(0, dot);
};

const subtitleExt = (name: string): string | null => {
  const dot = name.lastIndexOf('.');
  if (dot < 0) return null;
  const ext = name.slice(dot + 1).toLowerCase();
  return SUBTITLE_EXTENSIONS.includes(ext) ? ext : null;
};

const prettyLang = (seg: string) => LANG_NAMES[seg.toLowerCase()] ?? seg.toUpperCase();

/**
 * Turn a flat list of files into VideoItems, attaching any sidecar subtitle
 * files (`movie.srt`, `movie.en.srt`, …) to the matching video by base name.
 */
function buildVideos(folderName: string, entries: { name: string; entry: Entry }[]): VideoItem[] {
  const subs = entries.filter((e) => subtitleExt(e.name));
  const items: VideoItem[] = [];

  for (const { name, entry } of entries) {
    if (!hasVideoExtension(name)) continue;
    const id = nextId();
    registry.set(id, entry);

    const vbase = stripExt(name);
    const tracks: SubtitleTrack[] = [];
    for (const s of subs) {
      const sbase = stripExt(s.name);
      let label: string | null = null;
      if (sbase.toLowerCase() === vbase.toLowerCase()) {
        label = 'Subtitles';
      } else if (sbase.toLowerCase().startsWith(vbase.toLowerCase() + '.')) {
        label = prettyLang(sbase.slice(vbase.length + 1));
      }
      if (label) {
        const trackId = nextId();
        subRegistry.set(trackId, s.entry);
        tracks.push({ id: trackId, label });
      }
    }

    items.push({
      id,
      name,
      // Desktop items carry their real path so recents can replay them across
      // sessions; web handles carry nothing and resolve via the registry.
      uri: entry.path ?? '',
      kind: entry.path ? 'desktop-file' : 'web-handle',
      folderName,
      sizeBytes: entry.file?.size ?? entry.size,
      subtitles: tracks.length ? tracks : undefined,
    });
  }

  items.sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
  return items;
}

/** Read the text of a sidecar subtitle track. */
export async function getSubtitleText(trackId: string): Promise<string> {
  const entry = subRegistry.get(trackId);
  if (!entry) throw new Error('Subtitle unavailable. Re-open the folder.');
  if (entry.path) return bridge()!.readText(entry.path);
  const file = entry.file ?? (await entry.handle!.getFile());
  return file.text();
}

export const pickLabel = 'Open folder';
export const pickIcon = 'folder-open';

export function canOpenFolder(): boolean {
  return true; // Always: FS Access API when present, input fallback otherwise.
}

function supportsFsAccess(): boolean {
  return typeof (globalThis as any).showDirectoryPicker === 'function';
}

/** Map a desktop (Electron) folder scan onto VideoItems. */
function fromDesktopResult(res: DesktopFolderResult | null): OpenFolderResult | null {
  if (!res) return null;
  const videos = buildVideos(
    res.folderName,
    res.files.map((f) => ({ name: f.name, entry: { path: f.path, size: f.size } }))
  );
  return { folderName: res.folderName, videos };
}

/** Prompt the user to choose a folder and return only the video files in it. */
export async function openFolder(): Promise<OpenFolderResult | null> {
  const b = bridge();
  if (b) return fromDesktopResult(await b.pickFolder());
  if (supportsFsAccess()) {
    return openFolderViaFsAccess();
  }
  return openFolderViaInput();
}

async function openFolderViaFsAccess(): Promise<OpenFolderResult | null> {
  let dir: FileSystemDirectoryHandle;
  try {
    dir = await (globalThis as any).showDirectoryPicker({ id: 'video-player', mode: 'read' });
  } catch {
    return null; // user cancelled
  }
  lastDirHandle = dir;
  await saveDirHandle(dir).catch(() => {});
  const videos = await scanDirectory(dir);
  return { folderName: dir.name, videos };
}

async function scanDirectory(dir: FileSystemDirectoryHandle): Promise<VideoItem[]> {
  const entries: { name: string; entry: Entry }[] = [];
  for await (const [name, handle] of (dir as any).entries()) {
    if (handle.kind === 'file' && (hasVideoExtension(name) || subtitleExt(name))) {
      entries.push({ name, entry: { handle: handle as FileSystemFileHandle } });
    }
  }
  return buildVideos(dir.name, entries);
}

async function openFolderViaInput(): Promise<OpenFolderResult | null> {
  return new Promise((resolve) => {
    const input = document.createElement('input');
    input.type = 'file';
    input.multiple = true;
    (input as any).webkitdirectory = true;
    input.accept = 'video/*';
    input.style.display = 'none';
    input.onchange = () => {
      const files = Array.from(input.files ?? []).filter(
        (f) => hasVideoExtension(f.name) || subtitleExt(f.name)
      );
      let folderName = 'Videos';
      const first = files[0] as any;
      if (first?.webkitRelativePath) folderName = String(first.webkitRelativePath).split('/')[0] || folderName;
      const videos = buildVideos(folderName, files.map((file) => ({ name: file.name, entry: { file } })));
      input.remove();
      resolve(videos.length ? { folderName, videos } : null);
    };
    input.oncancel = () => {
      input.remove();
      resolve(null);
    };
    document.body.appendChild(input);
    input.click();
  });
}

// ---- Reopen last folder ---------------------------------------------------

export async function canReopenLast(): Promise<boolean> {
  const b = bridge();
  if (b) return (await b.getLastFolder()) != null;
  if (!supportsFsAccess()) return false;
  if (lastDirHandle) return true;
  return (await loadDirHandle()) != null;
}

export async function reopenLast(): Promise<OpenFolderResult | null> {
  const b = bridge();
  if (b) return fromDesktopResult(await b.reopenLast());
  let dir = lastDirHandle ?? (await loadDirHandle());
  if (!dir) return null;
  const perm = await ensurePermission(dir);
  if (!perm) return null;
  lastDirHandle = dir;
  const videos = await scanDirectory(dir);
  return { folderName: dir.name, videos };
}

async function ensurePermission(handle: FileSystemDirectoryHandle): Promise<boolean> {
  const anyHandle = handle as any;
  if (!anyHandle.queryPermission) return true;
  if ((await anyHandle.queryPermission({ mode: 'read' })) === 'granted') return true;
  return (await anyHandle.requestPermission({ mode: 'read' })) === 'granted';
}

// ---- URL resolution -----------------------------------------------------

/** The real file path behind a VideoItem on desktop, or null elsewhere. */
function desktopPathOf(item: VideoItem): string | null {
  if (!bridge()) return null;
  const entry = registry.get(item.id);
  if (entry?.path) return entry.path;
  // Desktop recents survive restarts: the stored uri is a real file path.
  if (item.kind === 'desktop-file' && item.uri) return item.uri;
  return null;
}

/**
 * Resolve a VideoItem to its full playback source. On desktop this probes the
 * file: directly playable files (and previous conversions) come back as plain
 * seekable URLs, everything else as a live ffmpeg stream that starts within
 * seconds. On web it is always a direct object URL.
 */
export async function resolveSource(item: VideoItem, startAt = 0): Promise<PlaybackSource> {
  const p = desktopPathOf(item);
  if (p) return bridge()!.prepareMedia(p, startAt);
  return { url: await resolveUrl(item), mode: 'direct' };
}

/** A fresh stream URL starting at `t`, for seeking within a live stream. */
export async function streamUrlAt(streamKey: string, t: number): Promise<string> {
  return bridge()!.streamUrl(streamKey, t);
}

/**
 * Resolve a VideoItem to a playable URL (object URL for web). Desktop callers
 * should prefer resolveSource; this remains for web playback and thumbnails.
 */
export async function resolveUrl(item: VideoItem): Promise<string> {
  const p = desktopPathOf(item);
  if (p) return (await bridge()!.prepareMedia(p)).url;
  const entry = registry.get(item.id);
  if (entry) {
    const file = entry.file ?? (await entry.handle!.getFile());
    return URL.createObjectURL(file);
  }
  // Fall back to a direct url when we have no live handle (e.g. a recent that
  // already carries an http/blob uri).
  if (item.uri) return item.uri;
  throw new Error('This video is no longer available. Re-open its folder.');
}

/**
 * Resolve a URL for thumbnail capture only. On desktop this streams the
 * original file directly — no ffmpeg conversion — because thumbnails only
 * need video frames and must never trigger a conversion per library row.
 */
export async function resolveThumbUrl(item: VideoItem): Promise<string> {
  const entry = registry.get(item.id);
  if (entry?.path) return bridge()!.directUrl(entry.path);
  if (item.kind === 'desktop-file' && item.uri && bridge()) {
    return bridge()!.directUrl(item.uri);
  }
  return resolveUrl(item);
}

export function releaseUrl(url: string): void {
  if (url && url.startsWith('blob:')) URL.revokeObjectURL(url);
}

/**
 * Subscribe to conversion progress events from the desktop main process.
 * Returns an unsubscribe function; a no-op outside the desktop app.
 */
export function subscribePrepareProgress(
  cb: (p: { percent: number | null }) => void
): () => void {
  return bridge()?.onPrepareProgress(cb) ?? (() => {});
}

// ---- IndexedDB persistence of the directory handle ----------------------

const DB_NAME = 'video-player';
const STORE = 'handles';
const HANDLE_KEY = 'lastFolder';

function openDb(): Promise<IDBDatabase | null> {
  return new Promise((resolve) => {
    if (typeof indexedDB === 'undefined') return resolve(null);
    const req = indexedDB.open(DB_NAME, 1);
    req.onupgradeneeded = () => req.result.createObjectStore(STORE);
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => resolve(null);
  });
}

async function saveDirHandle(handle: FileSystemDirectoryHandle): Promise<void> {
  const db = await openDb();
  if (!db) return;
  await new Promise<void>((resolve) => {
    const tx = db.transaction(STORE, 'readwrite');
    tx.objectStore(STORE).put(handle, HANDLE_KEY);
    tx.oncomplete = () => resolve();
    tx.onerror = () => resolve();
  });
}

async function loadDirHandle(): Promise<FileSystemDirectoryHandle | null> {
  const db = await openDb();
  if (!db) return null;
  return new Promise((resolve) => {
    const tx = db.transaction(STORE, 'readonly');
    const req = tx.objectStore(STORE).get(HANDLE_KEY);
    req.onsuccess = () => resolve((req.result as FileSystemDirectoryHandle) ?? null);
    req.onerror = () => resolve(null);
  });
}
