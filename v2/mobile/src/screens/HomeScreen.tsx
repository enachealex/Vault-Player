import { useMemo, useState } from 'react';
import {
  FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View, useWindowDimensions,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import Ionicons from '@expo/vector-icons/Ionicons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import {
  type Film, type Library, allList, continueList, isNew, progressOf, recentList, routeFor, runtime, search,
  shelfFilms, watchAgainList,
} from '../library';
import { useSession } from '../session';
import { C, RADIUS } from '../theme';
import { Btn, FilmCard, Logo, ProgressBar, Still } from '../ui';

type Row = { key: string; title: string; films: Film[]; progress?: boolean };

export function HomeScreen({ lib, refreshing, onRefresh, onOpen, onPlay, onAccount }: {
  lib: Library;
  refreshing: boolean;
  onRefresh: () => void;
  onOpen: (f: Film) => void;
  onPlay: (f: Film, startMs: number) => void;
  onAccount: () => void;
}) {
  const insets = useSafeAreaInsets();
  const { width } = useWindowDimensions();
  const me = useSession().me;
  const [q, setQ] = useState('');
  const [searching, setSearching] = useState(false);
  const gutter = width < 600 ? 16 : 32;
  const cardW = Math.round(Math.min(280, Math.max(150, width * 0.42)));

  const rows = useMemo<Row[]>(() => {
    const out: Row[] = [];
    const cont = continueList(lib);
    if (cont.length) out.push({ key: 'continue', title: 'Continue watching', films: cont, progress: true });
    out.push({ key: 'recent', title: 'Recently added', films: recentList(lib).slice(0, 24) });
    for (const s of lib.shelves) {
      const films = shelfFilms(lib, s);
      if (films.length) out.push({ key: 'shelf-' + s.id, title: s.name, films });
    }
    const again = watchAgainList(lib);
    if (again.length) out.push({ key: 'again', title: 'Watch again', films: again });
    out.push({ key: 'all', title: 'All films', films: allList(lib), progress: true });
    return out;
  }, [lib]);

  const hero = continueList(lib)[0] || recentList(lib)[0];

  const header = (
    <View style={[styles.top, { paddingTop: insets.top + 10, paddingHorizontal: gutter }]}>
      {searching ? (
        <TextInput
          autoFocus value={q} onChangeText={setQ} placeholder="Search your films" placeholderTextColor={C.faint}
          style={styles.search} returnKeyType="search" autoCorrect={false}
        />
      ) : (
        <View style={styles.brand}><Logo size={28} /><Text style={styles.brandText}>Vault</Text></View>
      )}
      <Pressable style={styles.iconBtn} onPress={() => { if (searching) { setQ(''); setSearching(false); } else setSearching(true); }}
        accessibilityLabel={searching ? 'Close search' : 'Search'}>
        <Ionicons name={searching ? 'close' : 'search'} size={20} color={C.text} />
      </Pressable>
      <Pressable style={styles.iconBtn} onPress={onAccount} accessibilityLabel="Account">
        <Text style={[styles.iconText, { fontWeight: '800', fontSize: 15 }]}>
          {((me?.displayName || me?.username || '?')[0] || '?').toUpperCase()}
        </Text>
      </Pressable>
    </View>
  );

  if (searching && q.trim()) {
    const hits = search(lib, q);
    const cols = Math.max(2, Math.floor((width - gutter * 2 + 12) / (cardW + 12)));
    const w = Math.floor((width - gutter * 2 - 12 * (cols - 1)) / cols);
    return (
      <View style={styles.fill}>
        <FlatList
          key={'grid-' + cols}
          data={hits}
          numColumns={cols}
          keyExtractor={f => f.itemKey}
          keyboardShouldPersistTaps="handled"
          contentContainerStyle={{ paddingTop: insets.top + 74, paddingHorizontal: gutter, paddingBottom: insets.bottom + 40 }}
          columnWrapperStyle={{ gap: 12 }}
          ItemSeparatorComponent={() => <View style={{ height: 18 }} />}
          ListHeaderComponent={<Text style={[styles.h2, { paddingHorizontal: 0 }]}>Results for “{q.trim()}”  <Text style={styles.count}>{hits.length}</Text></Text>}
          ListEmptyComponent={<Text style={styles.empty}>Nothing in your library matches that.</Text>}
          renderItem={({ item }) => <FilmCard film={item} lib={lib} width={w} onPress={() => onOpen(item)} showProgress />}
        />
        {header}
      </View>
    );
  }

  return (
    <View style={styles.fill}>
      <ScrollView
        contentContainerStyle={{ paddingBottom: insets.bottom + 40 }}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} tintColor={C.text}
          colors={[C.rustA]} progressBackgroundColor={C.smokeHi} progressViewOffset={insets.top + 60} />}
      >
        {lib.films.length === 0 ? (
          <Text style={[styles.empty, { paddingTop: insets.top + 90, paddingHorizontal: gutter }]}>
            {!lib.mediaRootPresent
              ? "The server can't see its film folder right now. If the media drive was just unplugged or remounted, check it and pull down to refresh."
              : lib.scanning
                ? 'Your library is still being indexed. Films appear here as they are found. Pull down to refresh in a minute.'
                : "No films on the server yet. Add some to its film folder and they'll show up here."}
          </Text>
        ) : (
          <>
            {hero && <Hero film={hero} lib={lib} gutter={gutter} topInset={insets.top} onOpen={onOpen} onPlay={onPlay} />}
            {lib.scanning && <Text style={[styles.notice, { marginHorizontal: gutter }]}>Still indexing the server. Some films may not be here yet.</Text>}
            {rows.map(r => (
              <View key={r.key} style={{ marginBottom: 26 }}>
                <Text style={[styles.h2, { paddingHorizontal: gutter }]}>
                  {r.title}{r.key === 'all' && <Text style={styles.count}>  {r.films.length}</Text>}
                </Text>
                <FlatList
                  horizontal
                  data={r.films}
                  keyExtractor={f => r.key + f.itemKey}
                  showsHorizontalScrollIndicator={false}
                  contentContainerStyle={{ paddingHorizontal: gutter, gap: 12 }}
                  snapToInterval={cardW + 12}
                  decelerationRate="fast"
                  initialNumToRender={4}
                  windowSize={5}
                  renderItem={({ item }) => (
                    <FilmCard film={item} lib={lib} width={cardW} onPress={() => onOpen(item)} showProgress={r.progress} />
                  )}
                />
              </View>
            ))}
          </>
        )}
      </ScrollView>
      <LinearGradient colors={['rgba(6,7,10,0.85)', 'rgba(6,7,10,0)']} pointerEvents="none"
        style={[styles.topShade, { height: insets.top + 80 }]} />
      {header}
    </View>
  );
}

function Hero({ film, lib, gutter, topInset, onOpen, onPlay }: {
  film: Film; lib: Library; gutter: number; topInset: number;
  onOpen: (f: Film) => void; onPlay: (f: Film, startMs: number) => void;
}) {
  const { width, height } = useWindowDimensions();
  const p = progressOf(lib, film);
  const playable = !!routeFor(film);
  const h = Math.max(380, Math.min(height * 0.68, width * 1.25));
  return (
    <View style={{ height: h, paddingTop: topInset, justifyContent: 'flex-end', marginBottom: 22 }}>
      <Still film={film} style={{ position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, borderRadius: 0, borderWidth: 0 }} />
      <LinearGradient colors={['rgba(11,13,18,0)', 'rgba(11,13,18,0.55)', C.bg]} locations={[0.35, 0.7, 1]}
        style={StyleSheet.absoluteFill} />
      <View style={{ paddingHorizontal: gutter, paddingBottom: 6 }}>
        <Text style={styles.kicker}>{p ? 'CONTINUE WATCHING' : isNew(lib, film) ? 'NEW ON YOUR SERVER' : 'FEATURED'}</Text>
        <Text style={styles.heroTitle} numberOfLines={3}>{film.name}</Text>
        <Text style={styles.heroMeta}>
          {[runtime(film.durationMs), playable ? '' : 'Plays in the desktop app'].filter(Boolean).join('  ·  ')}
        </Text>
        {p && <ProgressBar frac={p.frac} style={{ width: 220, marginBottom: 16 }} />}
        <View style={{ flexDirection: 'row', gap: 12, flexWrap: 'wrap' }}>
          <Btn icon="play" label={p ? 'Resume' : 'Play'} disabled={!playable}
            onPress={() => onPlay(film, p ? p.row.resumeMs : 0)} />
          <Btn ghost label="More info" onPress={() => onOpen(film)} />
        </View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: C.bg },
  top: { position: 'absolute', left: 0, right: 0, top: 0, flexDirection: 'row', alignItems: 'center', gap: 10, paddingBottom: 10 },
  topShade: { position: 'absolute', left: 0, right: 0, top: 0 },
  brand: { flex: 1, flexDirection: 'row', alignItems: 'center', gap: 10 },
  brandText: { color: C.text, fontWeight: '800', fontSize: 20 },
  search: {
    flex: 1, color: C.text, fontSize: 16, paddingHorizontal: 14, paddingVertical: 9, borderRadius: RADIUS,
    backgroundColor: 'rgba(0,0,0,0.55)', borderWidth: 1, borderColor: C.focus,
  },
  iconBtn: {
    width: 40, height: 40, borderRadius: 10, alignItems: 'center', justifyContent: 'center',
    backgroundColor: 'rgba(11,13,18,0.55)', borderWidth: 1, borderColor: C.line,
  },
  iconText: { color: C.text, fontSize: 20 },
  kicker: { color: C.warm, fontSize: 12.5, letterSpacing: 1.5, fontWeight: '700', marginBottom: 8 },
  heroTitle: { color: C.text, fontSize: 36, lineHeight: 40, fontWeight: '800', marginBottom: 10 },
  heroMeta: { color: C.muted, fontSize: 14.5, marginBottom: 16 },
  h2: { color: C.text, fontSize: 18, fontWeight: '700', marginBottom: 11 },
  count: { color: C.faint, fontSize: 14, fontWeight: '600' },
  empty: { color: C.muted, fontSize: 15, lineHeight: 23, paddingTop: 20 },
  notice: {
    color: C.muted, fontSize: 14, padding: 12, borderRadius: RADIUS, marginBottom: 20,
    backgroundColor: C.glass, borderWidth: 1, borderColor: C.line,
  },
});
