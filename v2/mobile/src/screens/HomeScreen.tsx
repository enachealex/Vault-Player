/**
 * The front of the app: a featured film filling the top of the screen, rows of
 * films to scroll sideways, and a glass tab bar for Home, Search and My List.
 */
import { useEffect, useMemo, useRef, useState } from 'react';
import {
  Animated, FlatList, Pressable, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View,
  useWindowDimensions,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { BlurTargetView } from 'expo-blur';
import Ionicons from '@expo/vector-icons/Ionicons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import {
  type Film, type Library, allList, continueList, featuredList, isNew, myListFilms, progressOf, recentList,
  routeFor, search, shelfFilms, titleOf, watchAgainList,
} from '../library';
import { useSession } from '../session';
import { C, RADIUS } from '../theme';
import { Btn, Glass, GlassTarget, Logo, MetaLine, ProgressBar, Still, Tile } from '../ui';

type Tab = 'home' | 'search' | 'mylist';
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
  const target = useRef<View>(null);
  const [tab, setTab] = useState<Tab>('home');
  const [q, setQ] = useState('');
  const gutter = width < 600 ? 16 : 32;
  const tileW = Math.round(Math.min(260, Math.max(148, width * 0.4)));
  const bottomPad = insets.bottom + 96;

  const rows = useMemo<Row[]>(() => {
    const out: Row[] = [];
    const cont = continueList(lib);
    if (cont.length) out.push({ key: 'continue', title: 'Continue watching', films: cont, progress: true });
    out.push({ key: 'recent', title: 'Recently added', films: recentList(lib).slice(0, 24) });
    const mine = myListFilms(lib);
    if (mine.length) out.push({ key: 'mylist', title: 'My List', films: mine });
    for (const s of lib.shelves) {
      const films = shelfFilms(lib, s);
      if (films.length) out.push({ key: 'shelf-' + s.id, title: s.name, films });
    }
    const again = watchAgainList(lib);
    if (again.length) out.push({ key: 'again', title: 'Watch again', films: again });
    out.push({ key: 'all', title: 'All films', films: allList(lib), progress: true });
    return out;
  }, [lib]);

  // A fresh pick of featured films each time the catalogue is fetched, not on
  // every resume save (which also replaces lib).
  const featured = useMemo(() => featuredList(lib), [lib.fetchedAt]); // eslint-disable-line react-hooks/exhaustive-deps

  const initial = ((me?.displayName || me?.username || '?')[0] || '?').toUpperCase();

  let body;
  if (tab === 'home') {
    body = (
      <ScrollView
        contentContainerStyle={{ paddingBottom: bottomPad }}
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
            {featured.length > 0 && <Hero films={featured} lib={lib} gutter={gutter} onOpen={onOpen} onPlay={onPlay} />}
            {lib.scanning && <Text style={[styles.notice, { marginHorizontal: gutter }]}>Still indexing the server. Some films may not be here yet.</Text>}
            {rows.map(r => (
              <View key={r.key} style={{ marginBottom: 24 }}>
                <Text style={[styles.h2, { paddingHorizontal: gutter }]}>
                  {r.title}{r.key === 'all' && <Text style={styles.count}>  {r.films.length}</Text>}
                </Text>
                <FlatList
                  horizontal
                  data={r.films}
                  keyExtractor={f => r.key + f.itemKey}
                  showsHorizontalScrollIndicator={false}
                  contentContainerStyle={{ paddingHorizontal: gutter, gap: 10 }}
                  snapToInterval={tileW + 10}
                  decelerationRate="fast"
                  initialNumToRender={4}
                  windowSize={5}
                  renderItem={({ item }) => (
                    <Tile film={item} lib={lib} width={tileW} onPress={() => onOpen(item)} showProgress={r.progress} />
                  )}
                />
              </View>
            ))}
          </>
        )}
      </ScrollView>
    );
  } else {
    const films = tab === 'mylist' ? myListFilms(lib) : q.trim() ? search(lib, q) : allList(lib);
    const cols = Math.max(2, Math.floor((width - gutter * 2 + 10) / (tileW + 10)));
    const w = Math.floor((width - gutter * 2 - 10 * (cols - 1)) / cols);
    const heading = tab === 'mylist' ? 'My List'
      : q.trim() ? `Results for “${q.trim()}”` : 'All films';
    body = (
      <FlatList
        key={tab + cols}
        data={films}
        numColumns={cols}
        keyExtractor={f => f.itemKey}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode="on-drag"
        contentContainerStyle={{ paddingTop: insets.top + (tab === 'search' ? 128 : 72), paddingHorizontal: gutter, paddingBottom: bottomPad }}
        columnWrapperStyle={{ gap: 10 }}
        ItemSeparatorComponent={() => <View style={{ height: 10 }} />}
        ListHeaderComponent={<Text style={styles.h2}>{heading}  <Text style={styles.count}>{films.length}</Text></Text>}
        ListEmptyComponent={<Text style={styles.empty}>
          {tab === 'mylist' ? 'Nothing saved yet. Open any film and tap My List to keep it here.'
            : 'Nothing in your library matches that.'}
        </Text>}
        renderItem={({ item }) => <Tile film={item} lib={lib} width={w} onPress={() => onOpen(item)} showProgress />}
      />
    );
  }

  return (
    <GlassTarget.Provider value={target}>
      <View style={styles.fill}>
        <BlurTargetView ref={target} style={styles.fill}>{body}</BlurTargetView>

        {/* Top: brand and account over a soft shade; the search field on Search. */}
        <LinearGradient colors={['rgba(6,7,10,0.8)', 'rgba(6,7,10,0)']} pointerEvents="none"
          style={[styles.topShade, { height: insets.top + 90 }]} />
        <View style={[styles.top, { paddingTop: insets.top + 8, paddingHorizontal: gutter }]}>
          <View style={styles.brand}><Logo size={28} /><Text style={styles.brandText}>Vault</Text></View>
          <Pressable onPress={onAccount} accessibilityLabel="Account">
            <Glass style={styles.iconBtn}><Text style={styles.initial}>{initial}</Text></Glass>
          </Pressable>
        </View>
        {tab === 'search' && (
          <Glass style={[styles.searchBox, { top: insets.top + 60, left: gutter, right: gutter }]}>
            <Ionicons name="search" size={18} color={C.muted} />
            <TextInput
              autoFocus value={q} onChangeText={setQ} placeholder="Films, years…" placeholderTextColor={C.faint}
              style={styles.searchInput} returnKeyType="search" autoCorrect={false}
            />
            {!!q && <Pressable onPress={() => setQ('')} hitSlop={10}><Ionicons name="close-circle" size={18} color={C.muted} /></Pressable>}
          </Glass>
        )}

        {/* Bottom: the tab bar. */}
        <Glass strong style={[styles.tabbar, { bottom: insets.bottom + 10, left: gutter - 4, right: gutter - 4 }]}>
          <TabBtn icon="home" label="Home" on={tab === 'home'} onPress={() => setTab('home')} />
          <TabBtn icon="search" label="Search" on={tab === 'search'} onPress={() => setTab('search')} />
          <TabBtn icon="add" label="My List" on={tab === 'mylist'} onPress={() => setTab('mylist')} />
          <TabBtn icon="person-circle" label="Account" on={false} onPress={onAccount} />
        </Glass>
      </View>
    </GlassTarget.Provider>
  );
}

function TabBtn({ icon, label, on, onPress }: {
  icon: 'home' | 'search' | 'add' | 'person-circle'; label: string; on: boolean; onPress: () => void;
}) {
  return (
    <Pressable onPress={onPress} style={styles.tabBtn} accessibilityRole="tab" accessibilityState={{ selected: on }}>
      <Ionicons name={on ? icon : (icon + '-outline') as any} size={23} color={on ? C.text : C.muted} />
      <Text style={[styles.tabLabel, on && { color: C.text }]}>{label}</Text>
    </Pressable>
  );
}

/**
 * The featured film: a full-bleed still with title and actions, moving on to
 * the next pick every few seconds with a cross-fade. Dots show where it is.
 */
function Hero({ films, lib, gutter, onOpen, onPlay }: {
  films: Film[]; lib: Library; gutter: number;
  onOpen: (f: Film) => void; onPlay: (f: Film, startMs: number) => void;
}) {
  const { width, height } = useWindowDimensions();
  const [i, setI] = useState(0);
  const fade = useRef(new Animated.Value(1)).current;
  const h = Math.max(440, Math.min(height * 0.7, width * 1.4));

  useEffect(() => {
    if (films.length < 2) return;
    const t = setInterval(() => {
      Animated.timing(fade, { toValue: 0, duration: 350, useNativeDriver: true }).start(() => {
        setI(n => (n + 1) % films.length);
        Animated.timing(fade, { toValue: 1, duration: 450, useNativeDriver: true }).start();
      });
    }, 9000);
    return () => clearInterval(t);
  }, [films, fade]);

  const film = films[i % films.length];
  const p = progressOf(lib, film);
  const playable = !!routeFor(film);
  return (
    <View style={{ height: h, justifyContent: 'flex-end', marginBottom: 20 }}>
      <Animated.View style={[StyleSheet.absoluteFill, { opacity: fade }]}>
        <Still film={film} style={{ position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, borderRadius: 0, borderWidth: 0 }} />
      </Animated.View>
      <LinearGradient colors={['rgba(11,13,18,0.5)', 'rgba(11,13,18,0)', 'rgba(11,13,18,0)', 'rgba(11,13,18,0.85)', C.bg]}
        locations={[0, 0.2, 0.45, 0.8, 1]} style={StyleSheet.absoluteFill} />
      <Animated.View style={{ paddingHorizontal: gutter, alignItems: 'center', opacity: fade }}>
        <Text style={styles.kicker}>{p ? 'CONTINUE WATCHING' : isNew(lib, film) ? 'NEW IN YOUR VAULT' : 'FROM YOUR VAULT'}</Text>
        <Text style={styles.heroTitle} numberOfLines={3}>{titleOf(film)}</Text>
        <MetaLine film={film} center />
        {p && <ProgressBar frac={p.frac} style={{ width: 200, marginTop: 12 }} />}
        {!playable && <Text style={styles.appOnly}>Plays in the desktop app</Text>}
      </Animated.View>
      <View style={{ flexDirection: 'row', gap: 10, paddingHorizontal: gutter, marginTop: 16 }}>
        <View style={{ flex: 1 }}>
          <Btn icon="play" label={p ? 'Resume' : 'Play'} disabled={!playable}
            onPress={() => onPlay(film, p ? p.row.resumeMs : 0)} />
        </View>
        <View style={{ flex: 1 }}>
          <Btn ghost label="More info" onPress={() => onOpen(film)} />
        </View>
      </View>
      {films.length > 1 && (
        <View style={styles.dots}>
          {films.map((_, n) => <View key={n} style={[styles.dot, n === i % films.length && styles.dotOn]} />)}
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: C.bg },
  top: { position: 'absolute', left: 0, right: 0, top: 0, flexDirection: 'row', alignItems: 'center', gap: 10 },
  topShade: { position: 'absolute', left: 0, right: 0, top: 0 },
  brand: { flex: 1, flexDirection: 'row', alignItems: 'center', gap: 10 },
  brandText: { color: C.text, fontWeight: '800', fontSize: 21 },
  iconBtn: { width: 40, height: 40, borderRadius: 10, alignItems: 'center', justifyContent: 'center' },
  initial: { color: C.text, fontWeight: '800', fontSize: 15 },
  searchBox: {
    position: 'absolute', height: 48, borderRadius: RADIUS, flexDirection: 'row', alignItems: 'center',
    gap: 10, paddingHorizontal: 14,
  },
  searchInput: { flex: 1, color: C.text, fontSize: 16, paddingVertical: 8 },
  tabbar: {
    position: 'absolute', height: 64, borderRadius: 18, flexDirection: 'row', alignItems: 'center',
    justifyContent: 'space-around',
  },
  tabBtn: { alignItems: 'center', justifyContent: 'center', gap: 3, minWidth: 64, paddingVertical: 6 },
  tabLabel: { color: C.muted, fontSize: 11, fontWeight: '600' },
  kicker: { color: C.warm, fontSize: 11.5, letterSpacing: 2.2, fontWeight: '800', marginBottom: 8 },
  heroTitle: {
    color: C.text, fontSize: 36, lineHeight: 40, fontWeight: '800', marginBottom: 10, textAlign: 'center',
    textShadowColor: 'rgba(0,0,0,0.6)', textShadowRadius: 18,
  },
  appOnly: { color: C.warm, fontSize: 13, marginTop: 8 },
  dots: { flexDirection: 'row', gap: 6, justifyContent: 'center', marginTop: 14 },
  dot: { width: 6, height: 6, borderRadius: 2, backgroundColor: 'rgba(255,255,255,0.3)' },
  dotOn: { backgroundColor: '#fff', width: 16 },
  h2: { color: C.text, fontSize: 18, fontWeight: '700', marginBottom: 10 },
  count: { color: C.faint, fontSize: 14, fontWeight: '600' },
  empty: { color: C.muted, fontSize: 15, lineHeight: 23, paddingTop: 20 },
  notice: {
    color: C.muted, fontSize: 14, padding: 12, borderRadius: RADIUS, marginBottom: 20,
    backgroundColor: C.glass, borderWidth: 1, borderColor: C.line,
  },
});
