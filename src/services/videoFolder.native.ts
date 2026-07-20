/**
 * Native (iOS / Android) video selection. Mobile OSes sandbox the filesystem,
 * so instead of a raw folder browse we let the user pick one or more videos via
 * the system document picker; the picked uris are played directly.
 */
import * as DocumentPicker from 'expo-document-picker';
import { hasVideoExtension, PlaybackSource, VideoItem } from '../types';

export interface OpenFolderResult {
  folderName: string;
  videos: VideoItem[];
}

export const pickLabel = 'Add videos';
export const pickIcon = 'add-circle';

export function canOpenFolder(): boolean {
  return true;
}

let counter = 0;
const nextId = () => `nat-${Date.now()}-${counter++}`;

export async function openFolder(): Promise<OpenFolderResult | null> {
  const res = await DocumentPicker.getDocumentAsync({
    type: 'video/*',
    multiple: true,
    copyToCacheDirectory: false,
  });
  if (res.canceled || !res.assets?.length) return null;

  const videos: VideoItem[] = res.assets
    .filter((a) => a.mimeType?.startsWith('video') || hasVideoExtension(a.name ?? ''))
    .map((a) => ({
      id: nextId(),
      name: a.name ?? 'Video',
      uri: a.uri,
      kind: 'native-file' as const,
      folderName: 'Added videos',
      sizeBytes: a.size ?? undefined,
    }));

  videos.sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
  return videos.length ? { folderName: 'Added videos', videos } : null;
}

// Reopening a folder isn't meaningful on mobile; recents cover resume instead.
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

// Sidecar subtitle discovery isn't available through the mobile document picker.
export async function getSubtitleText(_trackId: string): Promise<string> {
  return '';
}

// Media conversion only exists on desktop.
export function subscribePrepareProgress(
  _cb: (p: { percent: number | null }) => void
): () => void {
  return () => {};
}

export function releaseUrl(_url: string): void {
  // Native uris are persistent; nothing to revoke.
}
