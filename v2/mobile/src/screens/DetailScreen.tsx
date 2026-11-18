/**
 * One film: its still at the top, and a glass sheet that slides up over a
 * blurred copy of it with the actions, the facts and more films to try.
 */
import { useEffect, useMemo, useRef } from 'react';
import { Animated, Pressable, ScrollView, StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { BlurTargetView } from 'expo-blur';
import Ionicons from '@expo/vector-icons/Ionicons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import {
  type Film, type Library, clock, dateOf, onList, progressOf, recentList, routeFor, size, titleOf,
} from '../library';
import { C } from '../theme';
import { Btn, Glass, GlassTarget, MetaLine, ProgressBar, Still, Tile } from '../ui';

export function DetailScreen({ film, lib, onBack, onPlay, onOpen, onToggleList }: {
  film: Film; lib: Library; onBack: () => void; onPlay: (f: Film, startMs: number) => void;
  onOpen: (f: Film) => void; onToggleList: (f: Film, on: boolean) => void;
}) {
  const insets = useSafeAreaInsets();
  const { width, height } = useWindowDimensions();
  const target = useRef<View>(null);
  const rise = useRef(new Animated.Value(height)).current;
  const p = progressOf(lib, film);
  const route = routeFor(film);
  const seen = lib.resume[film.filmKey];
  const saved = onList(lib, film);
  const artH = Math.min(width * 9 / 16 + insets.top, height * 0.45);
  const gutter = width < 600 ? 18 : 36;

  useEffect(() => {
    Animated.spring(rise, { toValue: 0, useNativeDriver: true, damping: 22, stiffness: 180, mass: 0.9 }).start();
  }, [rise]);

  // Films on the same shelves first, then whatever else is newest.
  const more = useMemo(() => {
    const keys = new Set<string>();
    for (const s of lib.shelves) if (s.items.some(e => e.filmKey === film.filmKey)) s.items.forEach(e => keys.add(e.filmKey));
    keys.delete(film.filmKey);
    const same = [...keys].map(k => lib.byFilmKey.get(k)).filter((f): f is Film => !!f);
    const rest = recentList(lib).filter(f => f.filmKey !== film.filmKey && !keys.has(f.filmKey));
    return [...same, ...rest].slice(0, 9);
  }, [lib, film]);

  const shelves = lib.shelves.filter(s => s.items.some(e => e.filmKey === film.filmKey)).map(s => s.name);
  const facts: [string, string][] = ([
    ['Watched', seen && seen.watchCount > 0 ? (seen.watchCount === 1 ? 'Once' : `${seen.watchCount} times`) : ''],
    ['On your shelves', shelves.join(', ')],
    ['Added', film.addedAt ? dateOf(film.addedAt) : ''],
    ['Format', [film.videoCodec, film.audioCodec].filter(Boolean).map(s => s!.toUpperCase()).join(' · ')],
    ['File', size(film.sizeBytes)],
  ] as [string, string][]).filter(([, v]) => !!v);

  const cols = 3;
  const tileW = Math.floor((width - gutter * 2 - 8 * (cols - 1)) / cols);

  return (
    <GlassTarget.Provider value={target}>
      <View style={{ flex: 1, backgroundColor: C.bg }}>
        {/* What the sheet blurs: the still, large, behind everything. */}
        <BlurTargetView ref={target} style={StyleSheet.absoluteFill}>
          <Still film={film} style={{ position: 'absolute', top: insets.top, left: 0, right: 0, height: width * 9 / 16 + 40, borderRadius: 0, borderWidth: 0 }} />
          <View style={[StyleSheet.absoluteFill, { backgroundColor: 'rgba(11,13,18,0.35)' }]} />
        </BlurTargetView>
        <LinearGradient colors={['rgba(0,0,0,0.45)', 'rgba(0,0,0,0)']} style={{ position: 'absolute', top: 0, left: 0, right: 0, height: insets.top + 70 }} />

        <Animated.View style={{ flex: 1, transform: [{ translateY: rise }] }}>
          <ScrollView contentContainerStyle={{ paddingTop: artH - 28 }} showsVerticalScrollIndicator={false}>
            <Glass strong style={[styles.sheet, { paddingHorizontal: gutter, paddingBottom: insets.bottom + 40, minHeight: height - artH + 28 }]}>
              <View style={styles.grip} />
              <Text style={styles.title}>{titleOf(film)}</Text>
              <MetaLine film={film} />

              <View style={{ gap: 10, marginTop: 18 }}>
                <Btn icon="play" label={p ? 'Resume' : 'Play'} disabled={!route}
                  onPress={() => onPlay(film, p ? p.row.resumeMs : 0)} />
                {p && film.durationMs && (
                  <View style={styles.resume}>
                    <ProgressBar frac={p.frac} style={{ flex: 1 }} />
                    <Text style={styles.left}>{clock(film.durationMs - p.row.resumeMs)} left</Text>
                  </View>
                )}
                {p && route && <Btn ghost label="Start over" onPress={() => onPlay(film, 0)} />}
              </View>

              {!route && (
                <Text style={styles.warn}>This phone can't play this film's format. It plays in the Vault desktop app.</Text>
              )}

              <View style={styles.actions}>
                <Action icon={saved ? 'checkmark' : 'add'} label="My List" on={saved}
                  onPress={() => onToggleList(film, !saved)} />
                {!!film.captionsUrl && <Action icon="text" label="Captions" note="English" />}
              </View>

              {facts.map(([k, v]) => (
                <View key={k} style={styles.fact}>
                  <Text style={styles.factK}>{k}</Text>
                  <Text style={styles.factV}>{v}</Text>
                </View>
              ))}

              {more.length > 0 && (
                <>
                  <Text style={styles.h4}>More from your vault</Text>
                  <View style={styles.grid}>
                    {more.map(f => <Tile key={f.itemKey} film={f} lib={lib} width={tileW} onPress={() => onOpen(f)} />)}
                  </View>
                </>
              )}
            </Glass>
          </ScrollView>
        </Animated.View>

        <Pressable onPress={onBack} style={[styles.close, { top: insets.top + 10, right: gutter - 4 }]}
          accessibilityLabel="Close" hitSlop={8}>
          <Glass style={styles.closeInner}><Ionicons name="close" size={22} color={C.text} /></Glass>
        </Pressable>
      </View>
    </GlassTarget.Provider>
  );
}

function Action({ icon, label, onPress, on, note }: {
  icon: 'add' | 'checkmark' | 'text'; label: string; onPress?: () => void; on?: boolean; note?: string;
}) {
  return (
    <Pressable onPress={onPress} disabled={!onPress} style={({ pressed }) => [styles.action, pressed && { opacity: 0.7 }]}
      accessibilityRole={onPress ? 'button' : 'text'} accessibilityState={on != null ? { checked: on } : undefined}>
      <Ionicons name={icon} size={24} color={C.text} />
      <Text style={styles.actionT}>{label}</Text>
      {note && <Text style={styles.actionN}>{note}</Text>}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  sheet: { borderTopLeftRadius: 22, borderTopRightRadius: 22, borderBottomWidth: 0, borderLeftWidth: 0, borderRightWidth: 0, paddingTop: 12 },
  grip: { alignSelf: 'center', width: 40, height: 4, borderRadius: 2, backgroundColor: 'rgba(255,255,255,0.3)', marginBottom: 14 },
  title: { color: C.text, fontSize: 28, lineHeight: 33, fontWeight: '800', marginBottom: 8 },
  resume: { flexDirection: 'row', alignItems: 'center', gap: 10, marginTop: -2, marginBottom: 2 },
  left: { color: C.muted, fontSize: 12.5 },
  warn: { color: C.warm, fontSize: 14, lineHeight: 20, marginTop: 14 },
  actions: { flexDirection: 'row', gap: 26, marginTop: 20, marginBottom: 18, paddingLeft: 4 },
  action: { alignItems: 'center', gap: 4, minWidth: 64 },
  actionT: { color: C.muted, fontSize: 12, fontWeight: '600' },
  actionN: { color: C.faint, fontSize: 11, marginTop: -2 },
  fact: { flexDirection: 'row', gap: 16, marginBottom: 6 },
  factK: { color: C.faint, fontSize: 14, width: 120 },
  factV: { color: C.muted, fontSize: 14, flex: 1 },
  h4: { color: C.text, fontSize: 16, fontWeight: '700', marginTop: 22, marginBottom: 10 },
  grid: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  close: { position: 'absolute' },
  closeInner: { width: 42, height: 42, borderRadius: 10, alignItems: 'center', justifyContent: 'center' },
});
