import { Ionicons } from '@expo/vector-icons';
import { Pressable, StyleSheet, ViewStyle } from 'react-native';
import { theme } from '../theme';

interface Props {
  name: keyof typeof Ionicons.glyphMap;
  onPress: () => void;
  size?: number;
  color?: string;
  active?: boolean;
  disabled?: boolean;
  style?: ViewStyle;
  accessibilityLabel?: string;
}

/** Round, tappable icon button used throughout the player controls. */
export function IconButton({
  name,
  onPress,
  size = 24,
  color = theme.colors.text,
  active = false,
  disabled = false,
  style,
  accessibilityLabel,
}: Props) {
  return (
    <Pressable
      onPress={onPress}
      disabled={disabled}
      accessibilityRole="button"
      accessibilityLabel={accessibilityLabel ?? String(name)}
      style={({ pressed, hovered }: any) => [
        styles.base,
        active && styles.active,
        (pressed || hovered) && !disabled && styles.hover,
        disabled && styles.disabled,
        style,
      ]}
    >
      <Ionicons name={name} size={size} color={active ? theme.colors.accent : color} />
    </Pressable>
  );
}

const styles = StyleSheet.create({
  base: {
    width: 44,
    height: 44,
    borderRadius: theme.radius.pill,
    alignItems: 'center',
    justifyContent: 'center',
  },
  hover: { backgroundColor: theme.colors.surfaceAlt },
  active: { backgroundColor: theme.colors.accentSoft },
  disabled: { opacity: 0.35 },
});
