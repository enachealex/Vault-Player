/**
 * Shared domain types used across platforms.
 */

/** How playback should behave when the current video finishes. */
export type PlaybackMode = 'once' | 'autoplay-next' | 'loop-one' | 'loop-all';

export const PLAYBACK_MODES: {
  key: PlaybackMode;
  label: string;
  icon: string;
  hint: string;
}[] = [
  { key: 'autoplay-next', label: 'Autoplay', icon: 'play-skip-forward', hint: 'Play the next video automatically' },
  { key: 'loop-all', label: 'Loop list', icon: 'repeat', hint: 'Repeat the whole folder' },
  { key: 'loop-one', label: 'Loop one', icon: 'sync', hint: 'Repeat the current video' },
  { key: 'once', label: 'Play once', icon: 'stop-circle', hint: 'Stop after this video' },
];

/** A sidecar subtitle track (.srt/.vtt) associated with a video by file name. */
export interface SubtitleTrack {
  id: string;
  label: string;
  lang?: string;
}

/**
 * A single playable video. `id` is stable within a session so the platform
 * layer can resolve it back to an actual URL when playback starts (on web the
 * real URL is a short-lived object URL created from a File handle on demand).
 */
export interface VideoItem {
  id: string;
  name: string;
  /** Direct uri when known ahead of time (native). Empty for web handles. */
  uri: string;
  /** Where this item came from, so the resolver knows how to get a URL. */
  kind: 'web-handle' | 'native-file' | 'desktop-file' | 'media-library';
  folderName?: string;
  sizeBytes?: number;
  subtitles?: SubtitleTrack[];
}

/** Subtitle file extensions we recognise as sidecar tracks. */
export const SUBTITLE_EXTENSIONS = ['srt', 'vtt'];

/**
 * How a video will actually be played.
 * - `direct`/`converted`: a plain, natively seekable file URL.
 * - `stream`: a live transcode (desktop only) — unseekable at the element
 *   level; the player seeks by requesting a new stream at the target time,
 *   using `streamKey` + `durationSeconds` to run its own timeline clock.
 */
export interface PlaybackSource {
  url: string;
  mode: 'direct' | 'converted' | 'stream';
  durationSeconds?: number;
  streamKey?: string;
}

/** Persisted entry for the "Recent" list, including resume position. */
export interface RecentEntry {
  id: string;
  name: string;
  uri: string;
  kind: VideoItem['kind'];
  folderName?: string;
  positionSeconds: number;
  durationSeconds: number;
  lastPlayedAt: number;
}

export interface Settings {
  playbackMode: PlaybackMode;
  volume: number; // 0..1
  muted: boolean;
  playbackRate: number;
}

export const DEFAULT_SETTINGS: Settings = {
  playbackMode: 'autoplay-next',
  volume: 1,
  muted: false,
  playbackRate: 1,
};

/** Video file extensions we recognise when scanning a folder. */
export const VIDEO_EXTENSIONS = [
  'mp4', 'm4v', 'mov', 'mkv', 'webm', 'avi', 'wmv', 'flv', 'mpg', 'mpeg', 'ogv', '3gp', 'ts',
];

export function hasVideoExtension(name: string): boolean {
  const dot = name.lastIndexOf('.');
  if (dot < 0) return false;
  return VIDEO_EXTENSIONS.includes(name.slice(dot + 1).toLowerCase());
}
