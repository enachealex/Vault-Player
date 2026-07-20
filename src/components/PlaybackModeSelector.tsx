import { Ionicons } from '@expo/vector-icons';
import { Pressable, StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';
import { PLAYBACK_MODES, PlaybackMode } from '../types';

interface Props {
  mode: PlaybackMode;
  onChange: (mode: PlaybackMode) => void;
  compact?: boolean;
  /** Mostly transparent background for overlaying on video. */
  translucent?: boolean;
}

/** Segmented control for the four end-of-video behaviours. */
export function PlaybackModeSelector({ mode, onChange, compact, translucent }: Props) {
  return (
    <View style={[styles.row, translucent && styles.rowTranslucent]}>
      {PLAYBACK_MODES.map((m) => {
        const active = m.key === mode;
        return (
          <Pressable
            key={m.key}
            onPress={() => onChange(m.key)}
            accessibilityRole="button"
            accessibilityLabel={m.hint}
            style={({ hovered }: any) => [
              styles.item,
              active && styles.itemActive,
              hovered && !active && styles.itemHover,
            ]}
          >
            <Ionicons
              name={m.icon as keyof typeof Ionicons.glyphMap}
              size={18}
              color={active ? theme.colors.accent : theme.colors.textMuted}
            />
            {!compact && (
              <Text style={[styles.label, active && styles.labelActive]}>{m.label}</Text>
            )}
          </Pressable>
        );
      })}
    </View>
  );
}

const styles = StyleSheet.create({
  row: {
    flexDirection: 'row',
    backgroundColor: theme.colors.surfaceAlt,
    borderRadius: theme.radius.pill,
    padding: 4,
    gap: 4,
  },
  // Overlay variant: see the video through it, buttons still legible.
  rowTranslucent: { backgroundColor: 'rgba(10, 13, 18, 0.35)' },
  item: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 6,
    paddingVertical: 8,
    paddingHorizontal: 12,
    borderRadius: theme.radius.pill,
  },
  itemActive: { backgroundColor: theme.colors.accentSoft },
  itemHover: { backgroundColor: theme.colors.surface },
  label: { color: theme.colors.textMuted, fontSize: theme.font.small, fontWeight: '600' },
  labelActive: { color: theme.colors.text },
});
