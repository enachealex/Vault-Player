/**
 * Lightweight persistence on top of AsyncStorage (localStorage on web).
 * Stores recents and user settings as JSON.
 */
import AsyncStorage from '@react-native-async-storage/async-storage';
import { DEFAULT_SETTINGS, RecentEntry, Settings } from './types';

const RECENTS_KEY = 'vp.recents.v1';
const SETTINGS_KEY = 'vp.settings.v1';
const MAX_RECENTS = 40;

export async function getRecents(): Promise<RecentEntry[]> {
  try {
    const raw = await AsyncStorage.getItem(RECENTS_KEY);
    if (!raw) return [];
    const parsed = JSON.parse(raw) as RecentEntry[];
    return Array.isArray(parsed) ? parsed : [];
  } catch {
    return [];
  }
}

/**
 * Insert or update a recent entry, keyed by name+folder so replaying the same
 * file updates its resume position instead of creating a duplicate.
 */
export async function upsertRecent(entry: RecentEntry): Promise<RecentEntry[]> {
  const recents = await getRecents();
  const matchKey = `${entry.folderName ?? ''}::${entry.name}`;
  const filtered = recents.filter((r) => `${r.folderName ?? ''}::${r.name}` !== matchKey);
  const next = [entry, ...filtered].slice(0, MAX_RECENTS);
  await AsyncStorage.setItem(RECENTS_KEY, JSON.stringify(next));
  return next;
}

export async function removeRecent(id: string): Promise<RecentEntry[]> {
  const recents = await getRecents();
  const next = recents.filter((r) => r.id !== id);
  await AsyncStorage.setItem(RECENTS_KEY, JSON.stringify(next));
  return next;
}

export async function clearRecents(): Promise<void> {
  await AsyncStorage.removeItem(RECENTS_KEY);
}

export async function getSettings(): Promise<Settings> {
  try {
    const raw = await AsyncStorage.getItem(SETTINGS_KEY);
    if (!raw) return { ...DEFAULT_SETTINGS };
    return { ...DEFAULT_SETTINGS, ...(JSON.parse(raw) as Partial<Settings>) };
  } catch {
    return { ...DEFAULT_SETTINGS };
  }
}

export async function saveSettings(settings: Settings): Promise<void> {
  await AsyncStorage.setItem(SETTINGS_KEY, JSON.stringify(settings));
}
