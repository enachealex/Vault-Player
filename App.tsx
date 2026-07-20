import { Ionicons } from '@expo/vector-icons';
import { StatusBar } from 'expo-status-bar';
import { useCallback, useEffect, useState } from 'react';
import { Pressable, SafeAreaView, StyleSheet, Text, View } from 'react-native';
import { LibraryScreen } from './src/screens/LibraryScreen';
import { PlayerScreen } from './src/screens/PlayerScreen';
import * as folder from './src/services/videoFolder';
import {
  clearRecents as clearRecentsStore,
  getRecents,
  getSettings,
  removeRecent as removeRecentStore,
  saveSettings,
  upsertRecent,
} from './src/storage';
import { theme } from './src/theme';
import { DEFAULT_SETTINGS, RecentEntry, Settings, VideoItem } from './src/types';

export default function App() {
  const [ready, setReady] = useState(false);
  const [view, setView] = useState<'library' | 'player'>('library');

  const [folderName, setFolderName] = useState<string | null>(null);
  const [videos, setVideos] = useState<VideoItem[]>([]);
  const [recents, setRecents] = useState<RecentEntry[]>([]);
  const [settings, setSettings] = useState<Settings>(DEFAULT_SETTINGS);
  const [busy, setBusy] = useState(false);
  const [canReopen, setCanReopen] = useState(false);
  const [notice, setNotice] = useState<string | null>(null);

  const [playlist, setPlaylist] = useState<VideoItem[]>([]);
  const [index, setIndex] = useState(0);
  const [startPosition, setStartPosition] = useState<number | undefined>(undefined);

  // ---- Initial load ------------------------------------------------------
  useEffect(() => {
    (async () => {
      const [r, s, reopen] = await Promise.all([
        getRecents(),
        getSettings(),
        folder.canReopenLast(),
      ]);
      setRecents(r);
      setSettings(s);
      setCanReopen(reopen);
      setReady(true);
    })();
  }, []);

  const resumeFor = useCallback(
    (item: VideoItem): number | undefined => {
      const match = recents.find(
        (r) => r.name === item.name && (r.folderName ?? '') === (item.folderName ?? '')
      );
      if (match && match.positionSeconds > 5 && match.positionSeconds < match.durationSeconds - 15) {
        return match.positionSeconds;
      }
      return undefined;
    },
    [recents]
  );

  // ---- Folder actions ----------------------------------------------------
  const loadResult = useCallback(async (fn: () => Promise<folder.OpenFolderResult | null>) => {
    setBusy(true);
    setNotice(null);
    try {
      const result = await fn();
      if (result) {
        setFolderName(result.folderName);
        setVideos(result.videos);
        setCanReopen(await folder.canReopenLast());
        if (result.videos.length === 0) setNotice('That folder has no playable videos.');
      }
    } catch (e: any) {
      setNotice(e?.message ?? 'Could not open that folder.');
    } finally {
      setBusy(false);
    }
  }, []);

  const openFolder = useCallback(() => loadResult(folder.openFolder), [loadResult]);
  const reopenLast = useCallback(() => loadResult(folder.reopenLast), [loadResult]);

  // ---- Playback launching ------------------------------------------------
  const playList = useCallback(
    (list: VideoItem[], i: number) => {
      if (!list[i]) return;
      setPlaylist(list);
      setIndex(i);
      setStartPosition(resumeFor(list[i]));
      setView('player');
    },
    [resumeFor]
  );

  const playRecent = useCallback(
    (entry: RecentEntry) => {
      // Prefer a live item from the currently open folder (handles/urls valid).
      const liveIdx = videos.findIndex(
        (v) => v.name === entry.name && (v.folderName ?? '') === (entry.folderName ?? '')
      );
      if (liveIdx >= 0) {
        setPlaylist(videos);
        setIndex(liveIdx);
        setStartPosition(entry.positionSeconds);
        setView('player');
        return;
      }
      // Native uris and desktop file paths survive across sessions and can be
      // played standalone.
      if ((entry.kind === 'native-file' || entry.kind === 'desktop-file') && entry.uri) {
        setPlaylist([{ id: entry.id, name: entry.name, uri: entry.uri, kind: entry.kind, folderName: entry.folderName }]);
        setIndex(0);
        setStartPosition(entry.positionSeconds);
        setView('player');
        return;
      }
      // Web handles from a previous session need the folder re-opened first.
      setNotice(
        canReopen
          ? 'Re-open the folder to resume videos from a previous session.'
          : 'Open the folder again to play this video.'
      );
    },
    [videos, canReopen]
  );

  // ---- Persistence handlers ---------------------------------------------
  const handleSaveProgress = useCallback(async (entry: RecentEntry) => {
    setRecents(await upsertRecent(entry));
  }, []);

  const handleRemoveRecent = useCallback(async (id: string) => {
    setRecents(await removeRecentStore(id));
  }, []);

  const handleClearRecents = useCallback(async () => {
    await clearRecentsStore();
    setRecents([]);
  }, []);

  const handleSettings = useCallback((s: Settings) => {
    setSettings(s);
    saveSettings(s);
  }, []);

  if (!ready) {
    return <View style={styles.boot}><StatusBar style="light" /></View>;
  }

  return (
    <SafeAreaView style={styles.root}>
      <StatusBar style="light" hidden={view === 'player'} />
      {view === 'library' ? (
        <View style={styles.fill}>
          {notice && (
            <Pressable style={styles.notice} onPress={() => setNotice(null)}>
              <Ionicons name="information-circle" size={18} color={theme.colors.accent} />
              <Text style={styles.noticeText}>{notice}</Text>
              <Ionicons name="close" size={16} color={theme.colors.textMuted} />
            </Pressable>
          )}
          <LibraryScreen
            folderName={folderName}
            videos={videos}
            recents={recents}
            settings={settings}
            busy={busy}
            canReopen={canReopen}
            onOpenFolder={openFolder}
            onReopenLast={reopenLast}
            onPlayList={playList}
            onPlayRecent={playRecent}
            onRemoveRecent={handleRemoveRecent}
            onClearRecents={handleClearRecents}
            onSettingsChange={handleSettings}
          />
        </View>
      ) : (
        <PlayerScreen
          playlist={playlist}
          index={index}
          settings={settings}
          startPositionSeconds={startPosition}
          onIndexChange={(i) => {
            setStartPosition(undefined);
            setIndex(i);
          }}
          onSettingsChange={handleSettings}
          onSaveProgress={handleSaveProgress}
          onExit={() => setView('library')}
        />
      )}
    </SafeAreaView>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.colors.bg },
  fill: { flex: 1 },
  boot: { flex: 1, backgroundColor: theme.colors.bg },
  notice: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    backgroundColor: theme.colors.surfaceAlt,
    marginHorizontal: 20,
    marginTop: 12,
    paddingVertical: 10,
    paddingHorizontal: 14,
    borderRadius: theme.radius.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
  },
  noticeText: { flex: 1, color: theme.colors.text, fontSize: theme.font.small },
});
