import { Ionicons } from '@expo/vector-icons';
import { useMemo, useState } from 'react';
import {
  Platform,
  Pressable,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { PlaybackModeSelector } from '../components/PlaybackModeSelector';
import { Thumbnail } from '../components/Thumbnail';
import { formatBytes, formatTime, relativeTime } from '../format';
import { pickIcon, pickLabel } from '../services/videoFolder';
import { theme } from '../theme';
import { RecentEntry, Settings, VideoItem } from '../types';

type SortKey = 'original' | 'name-asc' | 'name-desc';
const SORT_MODES: { key: SortKey; label: string; icon: string }[] = [
  { key: 'original', label: 'Folder order', icon: 'list' },
  { key: 'name-asc', label: 'Name A–Z', icon: 'arrow-down' },
  { key: 'name-desc', label: 'Name Z–A', icon: 'arrow-up' },
];

interface Props {
  folderName: string | null;
  videos: VideoItem[];
  recents: RecentEntry[];
  settings: Settings;
  busy: boolean;
  canReopen: boolean;
  onOpenFolder: () => void;
  onReopenLast: () => void;
  onPlayList: (list: VideoItem[], index: number) => void;
  onPlayRecent: (entry: RecentEntry) => void;
  onRemoveRecent: (id: string) => void;
  onClearRecents: () => void;
  onSettingsChange: (s: Settings) => void;
}

export function LibraryScreen({
  folderName,
  videos,
  recents,
  settings,
  busy,
  canReopen,
  onOpenFolder,
  onReopenLast,
  onPlayList,
  onPlayRecent,
  onRemoveRecent,
  onClearRecents,
  onSettingsChange,
}: Props) {
  const [query, setQuery] = useState('');
  const [sort, setSort] = useState<SortKey>('original');

  const continueWatching = useMemo(
    () =>
      recents.filter(
        (r) => r.durationSeconds > 0 && r.positionSeconds > 5 && r.positionSeconds < r.durationSeconds - 15
      ),
    [recents]
  );

  const displayVideos = useMemo(() => {
    const q = query.trim().toLowerCase();
    let list = q ? videos.filter((v) => v.name.toLowerCase().includes(q)) : videos.slice();
    if (sort === 'name-asc') {
      list.sort((a, b) => a.name.localeCompare(b.name, undefined, { numeric: true }));
    } else if (sort === 'name-desc') {
      list.sort((a, b) => b.name.localeCompare(a.name, undefined, { numeric: true }));
    }
    return list;
  }, [videos, query, sort]);

  const sortMode = SORT_MODES.find((s) => s.key === sort)!;
  const cycleSort = () => {
    const i = SORT_MODES.findIndex((s) => s.key === sort);
    setSort(SORT_MODES[(i + 1) % SORT_MODES.length].key);
  };

  return (
    <ScrollView style={styles.root} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled">
      {/* Header */}
      <View style={styles.header}>
        <View style={styles.brand}>
          <View style={styles.logo}>
            <Ionicons name="play" size={20} color="#fff" />
          </View>
          <Text style={styles.appName}>Video Player</Text>
        </View>
        <View style={styles.headerActions}>
          {canReopen && (
            <Pressable
              style={({ hovered }: any) => [styles.ghostBtn, hovered && styles.ghostBtnHover]}
              onPress={onReopenLast}
              disabled={busy}
            >
              <Ionicons name="refresh" size={18} color={theme.colors.text} />
              <Text style={styles.ghostBtnText}>Reopen last folder</Text>
            </Pressable>
          )}
          <Pressable
            style={({ hovered }: any) => [styles.primaryBtn, hovered && styles.primaryBtnHover]}
            onPress={onOpenFolder}
            disabled={busy}
          >
            <Ionicons name={pickIcon as any} size={18} color="#fff" />
            <Text style={styles.primaryBtnText}>{pickLabel}</Text>
          </Pressable>
        </View>
      </View>

      {/* Default playback mode */}
      <View style={styles.modeCard}>
        <Text style={styles.modeLabel}>When a video ends</Text>
        <PlaybackModeSelector
          mode={settings.playbackMode}
          onChange={(m) => onSettingsChange({ ...settings, playbackMode: m })}
        />
      </View>

      {/* Continue watching */}
      {continueWatching.length > 0 && (
        <Section title="Continue watching" action={{ label: 'Clear', onPress: onClearRecents }}>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.hscroll}>
            {continueWatching.map((r) => (
              <RecentCard key={r.id} entry={r} onPlay={() => onPlayRecent(r)} onRemove={() => onRemoveRecent(r.id)} />
            ))}
          </ScrollView>
        </Section>
      )}

      {/* Folder contents */}
      {videos.length > 0 ? (
        <View style={styles.section}>
          <View style={styles.sectionHead}>
            <View>
              <Text style={styles.sectionTitle}>{folderName ? `In “${folderName}”` : 'Videos'}</Text>
              <Text style={styles.sectionSubtitle}>
                {displayVideos.length === videos.length
                  ? `${videos.length} video${videos.length === 1 ? '' : 's'}`
                  : `${displayVideos.length} of ${videos.length}`}
              </Text>
            </View>
            <Pressable
              style={styles.sectionAction}
              onPress={() => displayVideos.length > 0 && onPlayList(displayVideos, 0)}
            >
              <Ionicons name="play" size={15} color={theme.colors.accent} />
              <Text style={styles.sectionActionText}>Play all</Text>
            </Pressable>
          </View>

          {/* Search + sort toolbar */}
          <View style={styles.toolbar}>
            <View style={styles.searchBox}>
              <Ionicons name="search" size={16} color={theme.colors.textMuted} />
              <TextInput
                value={query}
                onChangeText={setQuery}
                placeholder="Search videos"
                placeholderTextColor={theme.colors.textMuted}
                style={styles.searchInput}
              />
              {query.length > 0 && (
                <Pressable onPress={() => setQuery('')} hitSlop={8}>
                  <Ionicons name="close-circle" size={16} color={theme.colors.textMuted} />
                </Pressable>
              )}
            </View>
            <Pressable
              style={({ hovered }: any) => [styles.sortBtn, hovered && styles.ghostBtnHover]}
              onPress={cycleSort}
            >
              <Ionicons name={sortMode.icon as any} size={15} color={theme.colors.text} />
              <Text style={styles.sortText}>{sortMode.label}</Text>
            </Pressable>
          </View>

          {displayVideos.length > 0 ? (
            <View style={styles.list}>
              {displayVideos.map((v, i) => (
                <FolderRow key={v.id} video={v} onPlay={() => onPlayList(displayVideos, i)} />
              ))}
            </View>
          ) : (
            <Text style={styles.noResults}>No videos match “{query}”.</Text>
          )}
        </View>
      ) : (
        <EmptyState label={pickLabel} icon={pickIcon} onOpen={onOpenFolder} busy={busy} />
      )}
    </ScrollView>
  );
}

// ---- Subcomponents -------------------------------------------------------

function Section({
  title,
  action,
  children,
}: {
  title: string;
  action?: { label: string; onPress: () => void };
  children: React.ReactNode;
}) {
  return (
    <View style={styles.section}>
      <View style={styles.sectionHead}>
        <Text style={styles.sectionTitle}>{title}</Text>
        {action && (
          <Pressable style={styles.sectionAction} onPress={action.onPress}>
            <Text style={styles.sectionActionText}>{action.label}</Text>
          </Pressable>
        )}
      </View>
      {children}
    </View>
  );
}

function RecentCard({ entry, onPlay, onRemove }: { entry: RecentEntry; onPlay: () => void; onRemove: () => void }) {
  const pct = entry.durationSeconds ? entry.positionSeconds / entry.durationSeconds : 0;
  return (
    <Pressable style={({ hovered }: any) => [styles.card, hovered && styles.cardHover]} onPress={onPlay}>
      <View style={styles.thumb}>
        <Thumbnail item={entry} style={styles.thumbFill} iconSize={30} />
        <View style={styles.playPill}>
          <Ionicons name="play" size={16} color="#fff" />
        </View>
        <Pressable style={styles.removeBtn} onPress={onRemove} hitSlop={8}>
          <Ionicons name="close" size={14} color="#fff" />
        </Pressable>
        <View style={styles.cardProgressTrack}>
          <View style={[styles.cardProgressFill, { width: `${pct * 100}%` }]} />
        </View>
      </View>
      <Text style={styles.cardTitle} numberOfLines={1}>{entry.name}</Text>
      <Text style={styles.cardMeta} numberOfLines={1}>
        {formatTime(entry.positionSeconds)} / {formatTime(entry.durationSeconds)} · {relativeTime(entry.lastPlayedAt)}
      </Text>
    </Pressable>
  );
}

function FolderRow({ video, onPlay }: { video: VideoItem; onPlay: () => void }) {
  return (
    <Pressable style={({ hovered }: any) => [styles.row, hovered && styles.rowHover]} onPress={onPlay}>
      <Thumbnail item={video} style={styles.rowThumb} iconSize={20} radius={8} />
      <View style={styles.rowText}>
        <Text style={styles.rowTitle} numberOfLines={1}>{video.name}</Text>
        {video.sizeBytes ? <Text style={styles.rowMeta}>{formatBytes(video.sizeBytes)}</Text> : null}
      </View>
      <Ionicons name="play-circle" size={26} color={theme.colors.textMuted} />
    </Pressable>
  );
}

function EmptyState({ label, icon, onOpen, busy }: { label: string; icon: string; onOpen: () => void; busy: boolean }) {
  return (
    <View style={styles.empty}>
      <View style={styles.emptyIcon}>
        <Ionicons name="folder-open-outline" size={44} color={theme.colors.accent} />
      </View>
      <Text style={styles.emptyTitle}>No videos yet</Text>
      <Text style={styles.emptyText}>
        {Platform.OS === 'web'
          ? 'Choose a folder and this player will list only the videos inside it — nothing is uploaded, everything stays on your device.'
          : 'Add videos from your device to start a playlist.'}
      </Text>
      <Pressable style={({ hovered }: any) => [styles.primaryBtn, styles.emptyBtn, hovered && styles.primaryBtnHover]} onPress={onOpen} disabled={busy}>
        <Ionicons name={icon as any} size={18} color="#fff" />
        <Text style={styles.primaryBtnText}>{label}</Text>
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: theme.colors.bg },
  content: { padding: 20, paddingBottom: 60, maxWidth: 1100, width: '100%', alignSelf: 'center' },
  header: { flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 12, marginBottom: 20 },
  brand: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  logo: {
    width: 40, height: 40, borderRadius: 12, backgroundColor: theme.colors.accent,
    alignItems: 'center', justifyContent: 'center',
  },
  appName: { color: theme.colors.text, fontSize: theme.font.title, fontWeight: '800' },
  headerActions: { flexDirection: 'row', alignItems: 'center', gap: 10 },
  primaryBtn: {
    flexDirection: 'row', alignItems: 'center', gap: 8,
    backgroundColor: theme.colors.accent, paddingVertical: 11, paddingHorizontal: 18,
    borderRadius: theme.radius.pill,
  },
  primaryBtnHover: { backgroundColor: '#4d7df0' },
  primaryBtnText: { color: '#fff', fontWeight: '700', fontSize: theme.font.body },
  ghostBtn: {
    flexDirection: 'row', alignItems: 'center', gap: 8,
    backgroundColor: theme.colors.surface, paddingVertical: 11, paddingHorizontal: 16,
    borderRadius: theme.radius.pill, borderWidth: 1, borderColor: theme.colors.border,
  },
  ghostBtnHover: { backgroundColor: theme.colors.surfaceAlt },
  ghostBtnText: { color: theme.colors.text, fontWeight: '600', fontSize: theme.font.small },

  modeCard: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', flexWrap: 'wrap', gap: 12,
    backgroundColor: theme.colors.surface, borderRadius: theme.radius.lg, padding: 16, marginBottom: 24,
    borderWidth: 1, borderColor: theme.colors.border,
  },
  modeLabel: { color: theme.colors.textMuted, fontSize: theme.font.small, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5 },

  section: { marginBottom: 28 },
  sectionHead: { flexDirection: 'row', alignItems: 'flex-end', justifyContent: 'space-between', marginBottom: 12 },
  sectionTitle: { color: theme.colors.text, fontSize: theme.font.heading, fontWeight: '700' },
  sectionSubtitle: { color: theme.colors.textMuted, fontSize: theme.font.small, marginTop: 2 },
  sectionAction: { flexDirection: 'row', alignItems: 'center', gap: 5 },
  sectionActionText: { color: theme.colors.accent, fontWeight: '700', fontSize: theme.font.small },

  toolbar: { flexDirection: 'row', alignItems: 'center', gap: 10, marginBottom: 12 },
  searchBox: {
    flex: 1, flexDirection: 'row', alignItems: 'center', gap: 8,
    backgroundColor: theme.colors.surface, borderRadius: theme.radius.md,
    borderWidth: 1, borderColor: theme.colors.border, paddingHorizontal: 12, height: 42,
  },
  searchInput: { flex: 1, color: theme.colors.text, fontSize: theme.font.body, outlineStyle: 'none' as any },
  sortBtn: {
    flexDirection: 'row', alignItems: 'center', gap: 7,
    backgroundColor: theme.colors.surface, borderRadius: theme.radius.md,
    borderWidth: 1, borderColor: theme.colors.border, paddingHorizontal: 14, height: 42,
  },
  sortText: { color: theme.colors.text, fontWeight: '600', fontSize: theme.font.small },
  noResults: { color: theme.colors.textMuted, fontSize: theme.font.body, paddingVertical: 20, textAlign: 'center' },

  hscroll: { gap: 14, paddingBottom: 4 },
  card: { width: 200 },
  cardHover: { opacity: 0.95 },
  thumb: {
    width: 200, height: 116, borderRadius: theme.radius.md,
    overflow: 'hidden', borderWidth: 1, borderColor: theme.colors.border,
    alignItems: 'center', justifyContent: 'center',
  },
  thumbFill: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0 },
  playPill: {
    position: 'absolute', width: 40, height: 40, borderRadius: 20,
    backgroundColor: 'rgba(0,0,0,0.45)', alignItems: 'center', justifyContent: 'center',
  },
  removeBtn: {
    position: 'absolute', top: 6, right: 6, width: 24, height: 24, borderRadius: 12,
    backgroundColor: 'rgba(0,0,0,0.55)', alignItems: 'center', justifyContent: 'center',
  },
  cardProgressTrack: { position: 'absolute', bottom: 0, left: 0, right: 0, height: 4, backgroundColor: 'rgba(255,255,255,0.15)' },
  cardProgressFill: { height: 4, backgroundColor: theme.colors.accent },
  cardTitle: { color: theme.colors.text, fontSize: theme.font.body, fontWeight: '600', marginTop: 8 },
  cardMeta: { color: theme.colors.textMuted, fontSize: theme.font.tiny, marginTop: 2 },

  list: { gap: 6 },
  row: {
    flexDirection: 'row', alignItems: 'center', gap: 14, padding: 10,
    borderRadius: theme.radius.md, backgroundColor: theme.colors.surface,
    borderWidth: 1, borderColor: 'transparent',
  },
  rowHover: { borderColor: theme.colors.border, backgroundColor: theme.colors.surfaceAlt },
  rowThumb: { width: 76, height: 44 },
  rowText: { flex: 1 },
  rowTitle: { color: theme.colors.text, fontSize: theme.font.body, fontWeight: '600' },
  rowMeta: { color: theme.colors.textMuted, fontSize: theme.font.tiny, marginTop: 2 },

  empty: { alignItems: 'center', paddingVertical: 60, gap: 12 },
  emptyIcon: {
    width: 92, height: 92, borderRadius: 28, backgroundColor: theme.colors.accentSoft,
    alignItems: 'center', justifyContent: 'center', marginBottom: 4,
  },
  emptyTitle: { color: theme.colors.text, fontSize: theme.font.title, fontWeight: '800' },
  emptyText: { color: theme.colors.textMuted, fontSize: theme.font.body, textAlign: 'center', maxWidth: 420, lineHeight: 22 },
  emptyBtn: { marginTop: 10 },
});
