/** Fallback used for TypeScript resolution; platform files override at runtime. */
export interface ThumbSource {
  id: string;
  name: string;
  uri: string;
  kind: string;
  folderName?: string;
}

export async function getThumbnail(_item: ThumbSource): Promise<string | null> {
  return null;
}
