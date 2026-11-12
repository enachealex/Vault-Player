import { Image } from 'expo-image';
import { Pressable, StyleSheet, Text, View, type ViewStyle } from 'react-native';
import { type Film, type Library, isNew, progressOf, routeFor, clock, runtime } from './library';
import { abs, authHeaders } from './session';
import { C, CARD_RADIUS, RADIUS } from './theme';

/** A film's still. Posters need the bearer header; the item key is the cache key. */
export function Still({ film, style }: { film: Film; style?: ViewStyle }) {
  return (
    <View style={[styles.still, style]}>
      <Text style={styles.ph} numberOfLines={3}>{film.name}</Text>
      {film.artUrl && (
        <Image
          style={StyleSheet.absoluteFill}
          source={{ uri: abs(film.artUrl), headers: authHeaders(), cacheKey: 'art-' + film.itemKey }}
          contentFit="cover"
          transition={250}
          recyclingKey={film.itemKey}
        />
      )}
    </View>
  );
}

export function Btn({ label, onPress, ghost, disabled, icon }: {
  label: string; onPress: () => void; ghost?: boolean; disabled?: boolean; icon?: 'play';
}) {
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled}
      android_ripple={{ color: ghost ? 'rgba(255,255,255,0.15)' : 'rgba(0,0,0,0.12)' }}
      style={({ pressed }) => [
        styles.btn, ghost && styles.btnGhost, disabled && { opacity: 0.45 }, pressed && { opacity: 0.85 },
      ]}
    >
      {icon === 'play' && <View style={[styles.tri, ghost && { borderLeftColor: C.text }]} />}
      <Text style={[styles.btnText, ghost && { color: C.text }]}>{label}</Text>
    </Pressable>
  );
}

export function ProgressBar({ frac, style }: { frac: number; style?: ViewStyle }) {
  return (
    <View style={[styles.bar, style]}>
      <View style={[styles.barFill, { width: `${Math.min(100, frac * 100)}%` }]} />
    </View>
  );
}

export function FilmCard({ film, lib, width, onPress, showProgress }: {
  film: Film; lib: Library; width: number; onPress: () => void; showProgress?: boolean;
}) {
  const p = showProgress ? progressOf(lib, film) : null;
  const playable = !!routeFor(film);
  return (
    <Pressable onPress={onPress} style={({ pressed }) => [{ width }, pressed && { opacity: 0.8 }]}
      accessibilityRole="button" accessibilityLabel={film.name}>
      <View>
        <Still film={film} style={{ width, height: Math.round(width * 9 / 16) }} />
        {!playable && <Text style={[styles.badge, { color: C.warm }]}>App only</Text>}
        {playable && isNew(lib, film) && <Text style={styles.badge}>New</Text>}
        {p && <ProgressBar frac={p.frac} style={styles.cardBar} />}
      </View>
      <Text style={styles.capT} numberOfLines={1}>{film.name}</Text>
      <Text style={styles.capM}>
        {p && film.durationMs ? clock(film.durationMs - p.row.resumeMs) + ' left' : runtime(film.durationMs)}
      </Text>
    </Pressable>
  );
}

export function Logo({ size = 30 }: { size?: number }) {
  return (
    <View style={[styles.logo, { width: size, height: size, borderRadius: size * 0.27 }]}>
      <View style={{
        borderLeftWidth: size * 0.3, borderTopWidth: size * 0.2, borderBottomWidth: size * 0.2,
        borderLeftColor: '#fff', borderTopColor: 'transparent', borderBottomColor: 'transparent', marginLeft: size * 0.1,
      }} />
    </View>
  );
}

const styles = StyleSheet.create({
  still: {
    borderRadius: CARD_RADIUS, overflow: 'hidden', backgroundColor: '#161B26',
    borderWidth: StyleSheet.hairlineWidth, borderColor: C.line,
    alignItems: 'center', justifyContent: 'center',
  },
  ph: { color: C.muted, fontWeight: '800', fontSize: 14, textAlign: 'center', paddingHorizontal: 10 },
  btn: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 10,
    backgroundColor: C.text, borderRadius: RADIUS, paddingVertical: 13, paddingHorizontal: 20,
    borderWidth: 1, borderColor: C.text, overflow: 'hidden',
  },
  btnGhost: { backgroundColor: C.glass, borderColor: C.line },
  btnText: { color: C.bg, fontWeight: '700', fontSize: 16 },
  tri: {
    borderLeftWidth: 12, borderTopWidth: 8, borderBottomWidth: 8,
    borderLeftColor: C.bg, borderTopColor: 'transparent', borderBottomColor: 'transparent',
  },
  bar: { height: 4, backgroundColor: 'rgba(255,255,255,0.25)', borderRadius: 2, overflow: 'hidden' },
  barFill: { height: '100%', backgroundColor: '#fff' },
  cardBar: { position: 'absolute', left: 0, right: 0, bottom: 0, borderRadius: 0 },
  badge: {
    position: 'absolute', top: 7, left: 7, paddingHorizontal: 6, paddingVertical: 3, borderRadius: 6,
    fontSize: 11, fontWeight: '700', color: '#fff', backgroundColor: 'rgba(11,13,18,0.82)',
    borderWidth: 1, borderColor: 'rgba(255,255,255,0.4)', overflow: 'hidden',
  },
  capT: { color: C.text, fontSize: 13.5, fontWeight: '600', marginTop: 7 },
  capM: { color: C.faint, fontSize: 12, marginTop: 1 },
  logo: { backgroundColor: C.rustA, alignItems: 'center', justifyContent: 'center' },
});
