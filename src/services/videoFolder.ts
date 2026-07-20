/**
 * Fallback module used only for TypeScript resolution. At runtime Metro picks
 * `videoFolder.web.ts` (web / Electron) or `videoFolder.native.ts` (iOS /
 * Android) via platform extensions, so these stubs never actually run.
 */
import { PlaybackSource, VideoItem } from '../types';

export interface OpenFolderResult {
  folderName: string;
  videos: VideoItem[];
}

export const pickLabel = 'Open folder';
export const pickIcon = 'folder-open';

export function canOpenFolder(): boolean {
  return false;
}

export async function openFolder(): Promise<OpenFolderResult | null> {
  return null;
}

export async function canReopenLast(): Promise<boolean> {
  return false;
}

export async function reopenLast(): Promise<OpenFolderResult | null> {
  return null;
}

export async function resolveUrl(item: VideoItem): Promise<string> {
  return item.uri;
}

export async function resolveSource(item: VideoItem, _startAt = 0): Promise<PlaybackSource> {
  return { url: item.uri, mode: 'direct' };
}

export async function streamUrlAt(_streamKey: string, _t: number): Promise<string> {
  throw new Error('Streaming is desktop-only');
}

export async function resolveThumbUrl(item: VideoItem): Promise<string> {
  return item.uri;
}

export function releaseUrl(_url: string): void {}

export async function getSubtitleText(_trackId: string): Promise<string> {
  return '';
}

export function subscribePrepareProgress(
  _cb: (p: { percent: number | null }) => void
): () => void {
  return () => {};
}
