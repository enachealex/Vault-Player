/**
 * Central design tokens. Dark, media-player-friendly palette with a single
 * accent so the UI stays calm while video is playing.
 */
export const theme = {
  colors: {
    bg: '#0d0f14',
    surface: '#161a22',
    surfaceAlt: '#1e2430',
    border: '#2a3240',
    text: '#f4f6fb',
    textMuted: '#9aa4b5',
    accent: '#5b8cff',
    accentSoft: 'rgba(91, 140, 255, 0.16)',
    danger: '#ff5c6c',
    overlay: 'rgba(8, 10, 14, 0.72)',
  },
  radius: {
    sm: 8,
    md: 12,
    lg: 18,
    pill: 999,
  },
  space: (n: number) => n * 4,
  font: {
    title: 22,
    heading: 17,
    body: 15,
    small: 13,
    tiny: 11,
  },
} as const;

export type Theme = typeof theme;
