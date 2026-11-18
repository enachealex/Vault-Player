import { createContext, type ReactNode, type RefObject, useContext } from 'react';
import { Image } from 'expo-image';
import { BlurView } from 'expo-blur';
import { LinearGradient } from 'expo-linear-gradient';
import { Platform, Pressable, StyleSheet, Text, View, type ViewStyle } from 'react-native';
import { type Film, type Library, isNew, progressOf, routeFor, clock, runtime, titleOf } from './library';
import { abs, authHeaders } from './session';
import { C, CARD_RADIUS, RADIUS } from './theme';

/** A film's still. Posters need the bearer header; the item key is the cache key. */
export function Still({ film, style }: { film: Film; style?: ViewStyle }) {
  return (
    <View style={[styles.still, style]}>
      <Text style={styles.ph} numberOfLines={3}>{titleOf(film)}</Text>
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
      <Text style={styles.capT} numberOfLines={1}>{titleOf(film)}</Text>
      <Text style={styles.capM}>
        {p && film.durationMs ? clock(film.durationMs - p.row.resumeMs) + ' left' : runtime(film.durationMs)}
      </Text>
    </Pressable>
  );
}

// ---- glass ------------------------------------------------------------------
//
// Android blurs only what sits inside a BlurTargetView, and the glass itself
// must be outside it. Each screen wraps its scrolling content in a target and
// provides it here; Glass panels drawn on top read it from context. Without a
// target (or below Android 12) the panel is a dark translucent fill instead.

export const GlassTarget = createContext<RefObject<View | null> | null>(null);

export function Glass({ children, style, strong }: { children?: ReactNode; style?: ViewStyle | ViewStyle[]; strong?: boolean }) {
  const target = useContext(GlassTarget);
  return (
    <View style={[styles.glass, style]}>
      {Platform.OS === 'android' && target ? (
        <BlurView blurTarget={target} blurMethod="dimezisBlurViewSdk31Plus" intensity={strong ? 70 : 55}
          tint="dark" style={StyleSheet.absoluteFill} />
      ) : null}
      <View style={[StyleSheet.absoluteFill, { backgroundColor: strong ? 'rgba(16,18,26,0.62)' : 'rgba(14,16,22,0.42)' }]} />
      {children}
    </View>
  );
}

/** "CC", "HD": a small outlined tag. */
export function Tag({ label }: { label: string }) {
  return <Text style={styles.tag}>{label}</Text>;
}

/** Year · runtime · tags, as one line. */
export function MetaLine({ film, center }: { film: Film; center?: boolean }) {
  const parts = [film.year ? String(film.year) : '', runtime(film.durationMs)].filter(Boolean);
  return (
    <View style={[styles.metaRow, center && { justifyContent: 'center' }]}>
      {parts.map((p, i) => (
        <Text key={i} style={styles.metaT}>{i ? '•  ' : ''}{p}</Text>
      ))}
      {!!film.captionsUrl && <Tag label="CC" />}
      <Tag label={is4k(film) ? '4K' : 'HD'} />
    </View>
  );
}

/**
 * A film as a landscape still with its title on it: the row tile. Shows a
 * NEW flag, or a progress bar when asked to.
 */
export function Tile({ film, lib, width, onPress, showProgress, onLongPress }: {
  film: Film; lib: Library; width: number; onPress: () => void; showProgress?: boolean; onLongPress?: () => void;
}) {
  const p = showProgress ? progressOf(lib, film) : null;
  const h = Math.round(width * 9 / 16);
  return (
    <Pressable onPress={onPress} onLongPress={onLongPress}
      style={({ pressed }) => [{ width, height: h }, pressed && { opacity: 0.8, transform: [{ scale: 0.98 }] }]}
      accessibilityRole="button" accessibilityLabel={titleOf(film)}>
      <Still film={film} style={{ width, height: h }} />
      <LinearGradient colors={['rgba(0,0,0,0)', 'rgba(0,0,0,0.82)']} locations={[0.4, 1]}
        style={[StyleSheet.absoluteFill, { borderRadius: CARD_RADIUS }]} pointerEvents="none" />
      {!routeFor(film) ? <Text style={[styles.flag, styles.flagApp]}>APP ONLY</Text>
        : isNew(lib, film) && !p ? <Text style={styles.flag}>NEW</Text> : null}
      <Text style={[styles.tileT, p && { bottom: 16 }]} numberOfLines={2}>{titleOf(film)}</Text>
      {p && <ProgressBar frac={p.frac} style={styles.tileBar} />}
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
  glass: { overflow: 'hidden', borderWidth: 1, borderColor: 'rgba(255,255,255,0.14)' },
  tag: {
    color: '#E7EAF0', fontSize: 10.5, fontWeight: '800', letterSpacing: 0.5, paddingHorizontal: 5, paddingVertical: 2,
    borderRadius: 5, borderWidth: 1, borderColor: 'rgba(255,255,255,0.38)', overflow: 'hidden',
  },
  metaRow: { flexDirection: 'row', alignItems: 'center', gap: 8, flexWrap: 'wrap' },
  metaT: { color: '#D5DAE3', fontSize: 13.5 },
  flag: {
    position: 'absolute', top: 7, left: 7, paddingHorizontal: 6, paddingVertical: 3, borderRadius: 5, overflow: 'hidden',
    fontSize: 10, fontWeight: '900', letterSpacing: 0.8, color: '#fff', backgroundColor: C.rustA,
  },
  flagApp: { backgroundColor: 'rgba(11,13,18,0.85)', color: C.warm, borderWidth: 1, borderColor: 'rgba(245,199,126,0.5)' },
  tileT: {
    position: 'absolute', left: 9, right: 9, bottom: 8, color: C.text, fontSize: 13, fontWeight: '700', lineHeight: 16,
    textShadowColor: 'rgba(0,0,0,0.8)', textShadowRadius: 3, textShadowOffset: { width: 0, height: 1 },
  },
  tileBar: { position: 'absolute', left: 9, right: 9, bottom: 7, height: 3 },
});

/** The file name is the only hint of resolution the catalogue carries. */
const is4k = (f: Film) => /2160p|\b4k\b|\buhd\b/i.test(f.name);
