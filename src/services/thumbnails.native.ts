/**
 * Native (iOS / Android) thumbnail generation via expo-video-thumbnails.
 */
import * as VideoThumbnails from 'expo-video-thumbnails';

export interface ThumbSource {
  id: string;
  name: string;
  uri: string;
  kind: string;
  folderName?: string;
}

const cache = new Map<string, string | null>();

export async function getThumbnail(item: ThumbSource): Promise<string | null> {
  if (!item.uri) return null;
  if (cache.has(item.uri)) return cache.get(item.uri) ?? null;
  try {
    const { uri } = await VideoThumbnails.getThumbnailAsync(item.uri, { time: 1000, quality: 0.6 });
    cache.set(item.uri, uri);
    return uri;
  } catch {
    cache.set(item.uri, null);
    return null;
  }
}
