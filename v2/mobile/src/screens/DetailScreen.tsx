import { Pressable, ScrollView, StyleSheet, Text, View, useWindowDimensions } from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import Ionicons from '@expo/vector-icons/Ionicons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { type Film, type Library, clock, dateOf, progressOf, routeFor, runtime, size } from '../library';
import { C } from '../theme';
import { Btn, ProgressBar, Still } from '../ui';

export function DetailScreen({ film, lib, onBack, onPlay }: {
  film: Film; lib: Library; onBack: () => void; onPlay: (f: Film, startMs: number) => void;
}) {
  const insets = useSafeAreaInsets();
  const { width, height } = useWindowDimensions();
  const p = progressOf(lib, film);
  const route = routeFor(film);
  const seen = lib.resume[film.filmKey];
  const artH = Math.min(width * 9 / 16, height * 0.55);
  const gutter = width < 600 ? 18 : 36;

  const meta = [
    runtime(film.durationMs),
    film.addedAt ? 'Added ' + dateOf(film.addedAt) : '',
    seen && seen.watchCount > 0 ? (seen.watchCount === 1 ? 'Watched once' : `Watched ${seen.watchCount} times`) : '',
  ].filter(Boolean);
  const shelves = lib.shelves.filter(s => s.items.some(e => e.filmKey === film.filmKey)).map(s => s.name);
  const facts: [string, string][] = ([
    ['On your shelves', shelves.join(', ')],
    ['Video', [film.videoCodec, film.audioCodec].filter(Boolean).map(s => s!.toUpperCase()).join(' · ')],
    ['File', size(film.sizeBytes)],
  ] as [string, string][]).filter(([, v]) => !!v);

  return (
    <View style={{ flex: 1, backgroundColor: C.bg }}>
      <ScrollView contentContainerStyle={{ paddingBottom: insets.bottom + 40 }}>
        <View style={{ height: artH + insets.top * 0.5 }}>
          <Still film={film} style={{ position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, borderRadius: 0, borderWidth: 0 }} />
          <LinearGradient colors={['rgba(11,13,18,0.5)', 'rgba(11,13,18,0)', C.bg]} locations={[0, 0.3, 1]}
            style={StyleSheet.absoluteFill} />
        </View>
        <View style={{ paddingHorizontal: gutter, marginTop: -40 }}>
          <Text style={styles.title}>{film.name}</Text>
          <Text style={styles.meta}>{meta.join('   ')}</Text>

          {p && film.durationMs && (
            <View style={{ marginBottom: 16 }}>
              <ProgressBar frac={p.frac} style={{ maxWidth: 360 }} />
              <Text style={styles.left}>{clock(film.durationMs - p.row.resumeMs)} left</Text>
            </View>
          )}

          {!route && (
            <Text style={styles.warn}>
              This phone can't play this film's format. It plays in the Vault Movies desktop app.
            </Text>
          )}

          <View style={{ gap: 12, marginBottom: 22 }}>
            <Btn icon="play" label={p ? 'Resume from ' + clock(p.row.resumeMs) : 'Play'} disabled={!route}
              onPress={() => onPlay(film, p ? p.row.resumeMs : 0)} />
            {p && route && <Btn ghost label="Start from the beginning" onPress={() => onPlay(film, 0)} />}
          </View>

          {facts.map(([k, v]) => (
            <View key={k} style={styles.fact}>
              <Text style={styles.factK}>{k}</Text>
              <Text style={styles.factV}>{v}</Text>
            </View>
          ))}
        </View>
      </ScrollView>
      <Pressable onPress={onBack} style={[styles.back, { top: insets.top + 10, left: gutter - 4 }]}
        accessibilityLabel="Back" hitSlop={8}>
        <Ionicons name="chevron-back" size={24} color={C.text} />
      </Pressable>
    </View>
  );
}

const styles = StyleSheet.create({
  title: { color: C.text, fontSize: 30, lineHeight: 35, fontWeight: '800', marginBottom: 8 },
  meta: { color: C.muted, fontSize: 14.5, marginBottom: 16 },
  left: { color: C.muted, fontSize: 13, marginTop: 6 },
  warn: { color: C.warm, fontSize: 14.5, lineHeight: 21, marginBottom: 16 },
  fact: { flexDirection: 'row', gap: 16, marginBottom: 6 },
  factK: { color: C.faint, fontSize: 14, width: 120 },
  factV: { color: C.muted, fontSize: 14, flex: 1 },
  back: {
    position: 'absolute', width: 42, height: 42, borderRadius: 10, alignItems: 'center', justifyContent: 'center',
    backgroundColor: 'rgba(11,13,18,0.7)', borderWidth: 1, borderColor: C.line,
  },
});
