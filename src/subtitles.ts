/**
 * Minimal SubRip (.srt) and WebVTT (.vtt) parser. Produces a flat list of
 * timed cues that the player renders itself, so subtitles work identically on
 * web and native without relying on the platform's <track> support.
 */
export interface Cue {
  start: number;
  end: number;
  text: string;
}

/** Parse "HH:MM:SS,mmm" / "HH:MM:SS.mmm" / "MM:SS.mmm" into seconds. */
function parseTimestamp(ts: string): number {
  const clean = ts.trim().replace(',', '.');
  const parts = clean.split(':');
  if (parts.length === 0) return 0;
  let h = 0,
    m = 0,
    s = 0;
  if (parts.length === 3) {
    [h, m, s] = [Number(parts[0]), Number(parts[1]), Number(parts[2])];
  } else if (parts.length === 2) {
    [m, s] = [Number(parts[0]), Number(parts[1])];
  } else {
    s = Number(parts[0]);
  }
  return (h || 0) * 3600 + (m || 0) * 60 + (s || 0);
}

/** Strip formatting tags (<i>, <b>, {\an8}, etc.) for plain-text display. */
function stripTags(text: string): string {
  return text
    .replace(/<[^>]+>/g, '')
    .replace(/\{[^}]*\}/g, '')
    .trim();
}

const TIME_LINE = /(\d{1,2}:)?\d{1,2}:\d{2}[.,]\d{1,3}\s*-->\s*(\d{1,2}:)?\d{1,2}:\d{2}[.,]\d{1,3}/;

export function parseSubtitles(raw: string): Cue[] {
  if (!raw) return [];
  const text = raw.replace(/\r\n/g, '\n').replace(/\r/g, '\n').replace(/^﻿/, '');
  const blocks = text.split(/\n\s*\n/);
  const cues: Cue[] = [];

  for (const block of blocks) {
    const lines = block.split('\n');
    const timeIdx = lines.findIndex((l) => l.includes('-->'));
    if (timeIdx === -1) continue;

    const timeLine = lines[timeIdx];
    if (!TIME_LINE.test(timeLine)) continue;
    const [startRaw, rest] = timeLine.split('-->');
    // VTT cue settings can trail the end timestamp; keep only the time token.
    const endRaw = rest.trim().split(/\s+/)[0];

    const start = parseTimestamp(startRaw);
    const end = parseTimestamp(endRaw);
    if (!(end > start)) continue;

    const body = lines
      .slice(timeIdx + 1)
      .join('\n')
      .trim();
    const cueText = stripTags(body);
    if (cueText) cues.push({ start, end, text: cueText });
  }

  cues.sort((a, b) => a.start - b.start);
  return cues;
}

/** Binary-search-ish lookup of the active cue for a given time. */
export function activeCue(cues: Cue[], time: number): Cue | null {
  for (let i = 0; i < cues.length; i++) {
    if (time >= cues[i].start && time <= cues[i].end) return cues[i];
    if (cues[i].start > time) break;
  }
  return null;
}
