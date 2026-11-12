/**
 * The catalogue and this account's watch state, plus the decisions made from
 * them: what is in progress, what is new, and how this phone plays a film.
 */
import { api } from './session';

export type Film = {
  itemKey: string;
  name: string;
  sizeBytes: number;
  durationMs: number | null;
  contentType: string;
  filmKey: string;
  webPlayable: boolean;
  videoCodec: string | null;
  audioCodec: string | null;
  streamUrl: string;
  artUrl: string | null;
  addedAt: number;
  hlsAvailable: boolean;
  hlsVideoCodec: string | null;
  hlsUrl: string;
};

export type Shelf = {
  id: number;
  name: string;
  sortOrder: number;
  items: { filmKey: string; name: string }[];
};

export type ResumeRow = {
  filmKey: string;
  name: string;
  resumeMs: number;
  watchCount: number;
  lastWatchedAt: number;
  chaptersJson: string | null;
  updatedAt: number;
};

export type Library = {
  films: Film[];
  byFilmKey: Map<string, Film>;
  shelves: Shelf[];
  resume: Record<string, ResumeRow>;
  scanning: boolean;
  mediaRootPresent: boolean;
  fetchedAt: number;
};

export type LoadResult =
  | { kind: 'ok'; library: Library }
  | { kind: 'pending'; message: string }
  | { kind: 'error'; message: string };

export async function loadLibrary(): Promise<LoadResult> {
  let r: Response, j: any;
  try {
    r = await api('/api/v1/catalog');
    j = await r.json().catch(() => ({}));
  } catch (e) {
    if (e instanceof Error && e.message === 'signed-out') throw e;
    return { kind: 'error', message: 'Could not reach the server. Check your connection and try again.' };
  }
  if (r.status === 403) {
    return { kind: 'pending', message: j.error || "Your account is waiting for the owner's approval." };
  }
  if (!r.ok) return { kind: 'error', message: j.error || 'The library is not available right now.' };

  const films: Film[] = j.items || [];
  const resume: Record<string, ResumeRow> = {};
  try {
    const lr = await api('/api/v1/library');
    if (lr.ok) for (const row of (await lr.json()) as ResumeRow[]) resume[row.filmKey] = row;
  } catch {}

  return {
    kind: 'ok',
    library: {
      films,
      byFilmKey: new Map(films.map(f => [f.filmKey, f])),
      shelves: ((j.collections || []) as Shelf[]).slice().sort((a, b) => a.sortOrder - b.sortOrder),
      resume,
      scanning: !!j.scanning,
      mediaRootPresent: j.mediaRootPresent !== false,
      fetchedAt: Date.now(),
    },
  };
}

/** Push one film's position; the server merges and answers with the truth. */
export async function pushResume(row: ResumeRow): Promise<ResumeRow[] | null> {
  try {
    const r = await api('/api/v1/library', { method: 'PUT', body: JSON.stringify({ items: [row] }) });
    return r.ok ? ((await r.json()) as ResumeRow[]) : null;
  } catch { return null; }
}

// ---- what to show ---------------------------------------------------------

export function progressOf(lib: Library, f: Film): { frac: number; row: ResumeRow } | null {
  const row = lib.resume[f.filmKey];
  if (!row || !row.resumeMs || !f.durationMs) return null;
  const frac = row.resumeMs / f.durationMs;
  if (frac < 0.01 || frac > 0.95) return null;   // barely started, or into the credits
  return { frac, row };
}

const byName = (a: Film, b: Film) =>
  a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true });
const lastWatched = (lib: Library) => (a: Film, b: Film) =>
  (lib.resume[b.filmKey]?.lastWatchedAt || 0) - (lib.resume[a.filmKey]?.lastWatchedAt || 0);

export const continueList = (lib: Library) => lib.films.filter(f => progressOf(lib, f)).sort(lastWatched(lib));
export const recentList = (lib: Library) => lib.films.slice().sort((a, b) => b.addedAt - a.addedAt);
export const allList = (lib: Library) => lib.films.slice().sort(byName);
export const watchAgainList = (lib: Library) =>
  lib.films.filter(f => (lib.resume[f.filmKey]?.watchCount || 0) > 0 && !progressOf(lib, f)).sort(lastWatched(lib));
export const shelfFilms = (lib: Library, s: Shelf) =>
  s.items.map(e => lib.byFilmKey.get(e.filmKey)).filter((f): f is Film => !!f);
export const search = (lib: Library, q: string) => {
  const needle = q.trim().toLocaleLowerCase();
  return lib.films.filter(f => f.name.toLocaleLowerCase().includes(needle)).sort(byName);
};

const TWO_WEEKS = 14 * 24 * 3600 * 1000;
export const isNew = (lib: Library, f: Film) => Date.now() - f.addedAt < TWO_WEEKS && !lib.resume[f.filmKey];

// ---- how this phone plays a film -------------------------------------------
//
// Android's player (ExoPlayer, under expo-video) opens MKV and MP4 itself and
// decodes H.264, HEVC, VP9 and AV1 on practically every phone sold since 2019.
// Audio is the catch: AAC, MP3, Opus, Vorbis and FLAC are always there; AC-3,
// E-AC-3, DTS and TrueHD depend on the handset and usually are not. So the raw
// file is played when its audio is safe, and anything else goes through the
// server's HLS copy, whose audio the server converts. If the raw file still
// fails on this particular phone, the player falls back to HLS by itself.

const SAFE_VIDEO = new Set(['h264', 'hevc', 'vp9', 'av1', 'vp8', 'mpeg4']);
const SAFE_AUDIO = new Set(['aac', 'mp3', 'opus', 'vorbis', 'flac']);

export type Route = 'direct' | 'hls' | null;

export function routeFor(f: Film): Route {
  if (f.webPlayable) return 'direct';
  const v = (f.videoCodec || '').toLowerCase();
  const a = (f.audioCodec || '').toLowerCase();
  if (SAFE_VIDEO.has(v) && (!a || SAFE_AUDIO.has(a))) return 'direct';
  if (f.hlsAvailable) return 'hls';
  // Unknown audio with no HLS copy available: try the file; the player says so if it can't.
  return SAFE_VIDEO.has(v) ? 'direct' : null;
}

// ---- formatting ---------------------------------------------------------------

export function clock(ms: number) {
  const s = Math.max(0, Math.floor(ms / 1000));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = s % 60;
  const mm = String(m).padStart(h ? 2 : 1, '0'), ss = String(sec).padStart(2, '0');
  return h ? `${h}:${mm}:${ss}` : `${mm}:${ss}`;
}
export function runtime(ms: number | null) {
  if (!ms) return '';
  const min = Math.round(ms / 60000);
  const h = Math.floor(min / 60), m = min % 60;
  return h ? (m ? `${h}h ${m}m` : `${h}h`) : `${m}m`;
}
export function size(b: number) {
  if (!b) return '';
  return b >= 1e9 ? (b / 1e9).toFixed(1) + ' GB' : Math.round(b / 1e6) + ' MB';
}
export function dateOf(ms: number) {
  try { return new Date(ms).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' }); }
  catch { return ''; }
}
