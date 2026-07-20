import { useEffect, useRef, useState } from 'react';
import { PanResponder, StyleSheet, Text, View } from 'react-native';
import { theme } from '../theme';

interface Props {
  /** Current progress 0..1. */
  progress: number;
  /** Optional buffered fraction 0..1. */
  buffered?: number;
  /** Called continuously while dragging with the previewed fraction. */
  onScrub?: (fraction: number) => void;
  /** Called once when the drag/tap ends with the final fraction. */
  onSeek: (fraction: number) => void;
  /** When provided, a floating label with this text follows the knob while dragging. */
  labelFor?: (fraction: number) => string;
  height?: number;
}

const KNOB = 16;

const clamp = (n: number) => Math.max(0, Math.min(1, n));

/**
 * Cross-platform draggable progress bar (works on web + native without a
 * native slider dependency). Tap to jump, drag the knob to scrub.
 *
 * Positioning is computed from pageX against the bar's window-measured left
 * edge — never from locationX, which changes target mid-drag on web. The
 * latest onSeek/onScrub are kept in refs because PanResponder callbacks are
 * created once and would otherwise capture the first render's props.
 */
export function Seekbar({ progress, buffered = 0, onScrub, onSeek, labelFor, height = 6 }: Props) {
  const hitboxRef = useRef<View>(null);
  const geom = useRef({ left: 0, width: 1 });
  const [dragging, setDragging] = useState(false);
  const [dragFraction, setDragFraction] = useState(0);
  // After release, keep showing the target fraction until playback catches up
  // so the knob doesn't snap back to the old position for a frame or two.
  const [settle, setSettle] = useState<number | null>(null);

  const onScrubRef = useRef(onScrub);
  onScrubRef.current = onScrub;
  const onSeekRef = useRef(onSeek);
  onSeekRef.current = onSeek;

  const refreshGeom = (then?: () => void) => {
    const node: any = hitboxRef.current;
    if (node?.measureInWindow) {
      node.measureInWindow((x: number, _y: number, w: number) => {
        if (w > 0) geom.current = { left: x, width: w };
        then?.();
      });
    } else {
      then?.();
    }
  };

  const fractionAt = (pageX: number) =>
    clamp((pageX - geom.current.left) / geom.current.width);

  const responder = useRef(
    PanResponder.create({
      onStartShouldSetPanResponder: () => true,
      onMoveShouldSetPanResponder: () => true,
      onPanResponderGrant: (e) => {
        const pageX = e.nativeEvent.pageX;
        setDragging(true);
        // Re-measure at grab time so layout shifts never leave us stale.
        refreshGeom(() => {
          const f = fractionAt(pageX);
          setDragFraction(f);
          onScrubRef.current?.(f);
        });
      },
      onPanResponderMove: (e) => {
        const f = fractionAt(e.nativeEvent.pageX);
        setDragFraction(f);
        onScrubRef.current?.(f);
      },
      onPanResponderRelease: (e) => {
        const f = fractionAt(e.nativeEvent.pageX);
        setDragging(false);
        setSettle(f);
        onSeekRef.current(f);
      },
      onPanResponderTerminate: () => setDragging(false),
    })
  ).current;

  // Clear the settle hold once real progress reaches the seek target (or on timeout).
  useEffect(() => {
    if (settle == null) return;
    if (Math.abs(progress - settle) < 0.02) {
      setSettle(null);
      return;
    }
    const t = setTimeout(() => setSettle(null), 1500);
    return () => clearTimeout(t);
  }, [progress, settle]);

  const shown = clamp(dragging ? dragFraction : settle ?? progress);

  return (
    <View
      ref={hitboxRef}
      testID="seekbar"
      style={styles.hitbox}
      onLayout={() => refreshGeom()}
      {...responder.panHandlers}
    >
      <View style={[styles.track, { height }]}>
        <View style={[styles.buffered, { width: `${clamp(buffered) * 100}%`, height }]} />
        <View style={[styles.fill, { width: `${shown * 100}%`, height }]} />
        {dragging && labelFor && (
          <View
            pointerEvents="none"
            style={[styles.scrubBubble, { left: `${shown * 100}%`, top: height / 2 - 44 }]}
          >
            <Text style={styles.scrubBubbleText} testID="scrub-time">
              {labelFor(shown)}
            </Text>
          </View>
        )}
        <View
          pointerEvents="none"
          style={[
            styles.knob,
            {
              left: `${shown * 100}%`,
              top: height / 2 - KNOB / 2,
              // Compose scale WITH the centering translate — never replace it,
              // or the knob drifts off the fill line while dragging.
              transform: [{ translateX: -KNOB / 2 }, ...(dragging ? [{ scale: 1.2 }] : [])],
            },
            dragging && styles.knobActive,
          ]}
        />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  hitbox: { paddingVertical: 10, justifyContent: 'center', cursor: 'pointer' as any },
  track: {
    width: '100%',
    backgroundColor: theme.colors.border,
    borderRadius: theme.radius.pill,
    overflow: 'visible',
  },
  buffered: {
    position: 'absolute',
    left: 0,
    backgroundColor: theme.colors.surfaceAlt,
    borderRadius: theme.radius.pill,
  },
  fill: {
    position: 'absolute',
    left: 0,
    backgroundColor: theme.colors.accent,
    borderRadius: theme.radius.pill,
  },
  knob: {
    position: 'absolute',
    width: KNOB,
    height: KNOB,
    borderRadius: KNOB / 2,
    backgroundColor: '#fff',
    shadowColor: '#000',
    shadowOpacity: 0.3,
    shadowRadius: 3,
    elevation: 3,
  },
  knobActive: { backgroundColor: theme.colors.accent },
  scrubBubble: {
    position: 'absolute',
    width: 76,
    transform: [{ translateX: -38 }],
    alignItems: 'center',
  },
  scrubBubbleText: {
    color: '#fff',
    fontSize: theme.font.small,
    fontWeight: '700',
    fontVariant: ['tabular-nums'],
    backgroundColor: 'rgba(10, 13, 18, 0.85)',
    paddingHorizontal: 10,
    paddingVertical: 5,
    borderRadius: theme.radius.pill,
    overflow: 'hidden',
  },
});
