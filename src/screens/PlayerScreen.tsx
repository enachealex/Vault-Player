import { Ionicons } from '@expo/vector-icons';
import { useEvent } from 'expo';
import { useVideoPlayer, VideoView } from 'expo-video';
import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Platform,
  Pressable,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { IconButton } from '../components/IconButton';
import { PlaybackModeSelector } from '../components/PlaybackModeSelector';
import { Seekbar } from '../components/Seekbar';
import { formatTime } from '../format';
import * as folder from '../services/videoFolder';
import { activeCue, Cue, parseSubtitles } from '../subtitles';
import { theme } from '../theme';
import { PlaybackMode, RecentEntry, Settings, VideoItem } from '../types';

interface Props {
  playlist: VideoItem[];
  index: number;
  settings: Settings;
  startPositionSeconds?: number;
  onIndexChange: (index: number) => void;
  onSettingsChange: (settings: Settings) => void;
  onSaveProgress: (entry: RecentEntry) => void;
  onExit: () => void;
}

const RATES = [0.5, 0.75, 1, 1.25, 1.5, 2];
const CONTROLS_TIMEOUT = 3200;

export function PlayerScreen({
  playlist,
  index,
  settings,
  startPositionSeconds,
  onIndexChange,
  onSettingsChange,
  onSaveProgress,
  onExit,
}: Props) {
  const current = playlist[index];
  const player = useVideoPlayer(null, (p) => {
    p.timeUpdateEventInterval = 0.5;
    p.volume = settings.volume;
    p.muted = settings.muted;
    p.playbackRate = settings.playbackRate;
  });

  const [controlsVisible, setControlsVisible] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  // Track play state from real play/pause events only. We must NOT trust
  // player.playing on web: replace() sets it optimistically to true even when
  // the browser blocks autoplay, which would leave the controls showing a
  // "pause" button over a frozen video.
  const [isPlaying, setIsPlaying] = useState(false);
  const [subtitleTrackId, setSubtitleTrackId] = useState<string | null>(null);
  const [cues, setCues] = useState<Cue[]>([]);
  const subtitleTracks = current?.subtitles ?? [];
  // Desktop only: ffmpeg conversion progress for files Chromium can't play as-is.
  const [convertPercent, setConvertPercent] = useState<number | null>(null);
  // Live-stream playback (desktop transcode). A live stream's element clock
  // always starts at 0, so the real movie position is offset + element time;
  // seeking restarts the stream at the target offset.
  const [streamInfo, setStreamInfo] = useState<{ key: string; duration: number } | null>(null);
  const [offset, setOffset] = useState(0);
  const streamRef = useRef<{ key: string; duration: number } | null>(null);
  const offsetRef = useRef(0);
  // Movie time a just-(re)started stream was asked to begin at. Once the
  // player is ready we derive the true offset from what the element reports:
  // ffmpeg may keep absolute timestamps (element clock ≈ target → offset ≈ 0)
  // or rebase to zero (element clock ≈ 0 → offset ≈ target). The formula
  // `target - elementTime` handles both and absorbs keyframe snapping.
  const streamTargetRef = useRef<number | null>(null);
  const streamSeekToken = useRef(0);
  const lastStreamRecovery = useRef(0);
  const urlRef = useRef<string | null>(null);
  const hideTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const lastSavedSecond = useRef(0);
  const pendingSeek = useRef<number | undefined>(startPositionSeconds);
  const isPlayingRef = useRef(false);
  // Playback intent. expo-video's VideoView mount/replace sequence can race
  // our play() call and abort it (AbortError: interrupted by a new load
  // request) — the element then sits paused forever. We record that playback
  // is wanted and re-issue play() once the player reports ready.
  const wantPlayingRef = useRef(false);

  const timeInfo = useEvent(player, 'timeUpdate', {
    currentTime: 0,
    bufferedPosition: 0,
    currentLiveTimestamp: null,
    currentOffsetFromLive: null,
  } as any);
  const status = useEvent(player, 'statusChange', { status: player.status } as any);

  const currentTime = timeInfo?.currentTime ?? 0;
  const rawDuration = player.duration || 0;
  /** Full movie duration: probed for streams, element-reported otherwise. */
  const knownDuration = streamInfo
    ? streamInfo.duration
    : isFinite(rawDuration)
      ? rawDuration
      : 0;
  /** Real movie position (stream offset + element clock). */
  const displayTime = offset + currentTime;
  const buffered = knownDuration
    ? Math.min(1, (offset + (timeInfo?.bufferedPosition ?? 0)) / knownDuration)
    : 0;

  // Drive isPlaying purely from actual play/pause events.
  useEffect(() => {
    const sub = player.addListener('playingChange', ({ isPlaying: playing }) => {
      isPlayingRef.current = playing;
      setIsPlaying(playing);
    });
    return () => sub.remove();
  }, [player]);

  // Web/desktop: reconcile with the element's real paused flag on every time
  // tick. Source reloads reset the element to paused WITHOUT a pause event,
  // which would otherwise leave isPlaying stale and break the play toggle.
  useEffect(() => {
    if (Platform.OS !== 'web' || typeof document === 'undefined') return;
    const el = document.querySelector('video');
    if (!el) return;
    const actual = !el.paused;
    if (actual !== isPlayingRef.current) {
      isPlayingRef.current = actual;
      setIsPlaying(actual);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [currentTime, status?.status]);

  // Conversion progress from the desktop main process (no-op elsewhere).
  useEffect(() => {
    return folder.subscribePrepareProgress((p) => {
      setConvertPercent(p.percent == null || p.percent >= 100 ? null : p.percent);
    });
  }, []);

  // ---- Load / swap the current video ------------------------------------
  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    setConvertPercent(null);
    // Stop the previous video immediately; resolveUrl may take a while when
    // the desktop pipeline needs to convert audio first.
    try {
      player.pause();
    } catch {}
    (async () => {
      try {
        // Live streams start straight at the resume point; direct files apply
        // the resume seek once the player reports readyToPlay.
        const resumeAt = pendingSeek.current && pendingSeek.current > 1 ? pendingSeek.current : 0;
        const src = await folder.resolveSource(current, resumeAt);
        if (cancelled) {
          folder.releaseUrl(src.url);
          return;
        }
        if (src.mode === 'stream') {
          const info = { key: src.streamKey ?? '', duration: src.durationSeconds ?? 0 };
          streamRef.current = info;
          setStreamInfo(info);
          streamTargetRef.current = resumeAt;
          offsetRef.current = 0;
          setOffset(0);
          pendingSeek.current = undefined; // consumed by the stream start offset
        } else {
          streamRef.current = null;
          setStreamInfo(null);
          offsetRef.current = 0;
          setOffset(0);
        }
        if (urlRef.current) folder.releaseUrl(urlRef.current);
        urlRef.current = src.url;
        player.replace(src.url);
        // Never auto-mute. On desktop (Electron) and native, autoplay with
        // sound is always permitted. In a plain browser, if autoplay is
        // blocked the video simply stays paused showing the play button —
        // the user's first tap starts it WITH sound (the tap is the gesture).
        player.muted = settings.muted;
        wantPlayingRef.current = true;
        player.play();
        // Backup retry in case the first play() was aborted by view mount races.
        setTimeout(() => {
          if (!cancelled && wantPlayingRef.current) {
            try { player.play(); } catch {}
          }
        }, 700);
      } catch (e: any) {
        if (!cancelled) setError(e?.message ?? 'Unable to open this video.');
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [current?.id]);

  // Release the object URL when leaving the player entirely.
  useEffect(() => {
    return () => {
      if (urlRef.current) folder.releaseUrl(urlRef.current);
    };
  }, []);

  // ---- React to status: clear loading, surface errors, apply resume seek -
  useEffect(() => {
    const s = status?.status;
    if (s === 'readyToPlay') {
      setLoading(false);
      // Fix up the stream clock offset from what the element actually reports.
      if (streamRef.current && streamTargetRef.current != null) {
        const off = Math.max(0, streamTargetRef.current - player.currentTime);
        streamTargetRef.current = null;
        offsetRef.current = off;
        setOffset(off);
      }
      if (pendingSeek.current && pendingSeek.current > 1 && rawDuration && !streamRef.current) {
        player.currentTime = Math.min(pendingSeek.current, rawDuration - 1);
        pendingSeek.current = undefined;
      }
      // Re-issue play() if our earlier attempt was aborted by load races.
      // Unconditional on purpose: expo-video's VideoView re-render can reload
      // the src, killing pending play() calls and resetting the element to
      // paused WITHOUT a pause event — so isPlaying state can be stale-true.
      // play() on an already-playing element is a no-op, so this is safe.
      if (wantPlayingRef.current) {
        try { player.play(); } catch {}
      }
    } else if (s === 'loading') {
      setLoading(true);
    } else if (s === 'error') {
      setLoading(false);
      setError('This file could not be played. The codec may be unsupported.');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status?.status, rawDuration]);

  // ---- Subtitles: auto-select the first sidecar track per video ---------
  useEffect(() => {
    setSubtitleTrackId(current?.subtitles?.[0]?.id ?? null);
  }, [current?.id]); // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!subtitleTrackId) {
      setCues([]);
      return;
    }
    let alive = true;
    folder
      .getSubtitleText(subtitleTrackId)
      .then((txt) => alive && setCues(parseSubtitles(txt)))
      .catch(() => alive && setCues([]));
    return () => {
      alive = false;
    };
  }, [subtitleTrackId]);

  const cue = cues.length ? activeCue(cues, displayTime) : null;

  /**
   * Seek to an absolute movie time. Direct files seek natively; live streams
   * restart ffmpeg at the target offset (the element itself is unseekable).
   */
  const seekToTime = useCallback(
    async (target: number) => {
      const info = streamRef.current;
      if (!info) {
        const dur = player.duration || 0;
        if (dur && isFinite(dur)) player.currentTime = Math.max(0, Math.min(target, dur - 0.25));
        return;
      }
      const clamped = Math.max(0, Math.min(target, Math.max(0, info.duration - 0.5)));
      const token = ++streamSeekToken.current;
      setLoading(true);
      try {
        const url = await folder.streamUrlAt(info.key, clamped);
        if (token !== streamSeekToken.current) return; // superseded by a newer seek
        streamTargetRef.current = clamped;
        if (urlRef.current) folder.releaseUrl(urlRef.current);
        urlRef.current = url;
        player.replace(url);
        player.muted = settings.muted;
        wantPlayingRef.current = true;
        player.play();
      } catch {
        setLoading(false);
      }
    },
    [player, settings.muted]
  );

  const cycleSubtitle = useCallback(() => {
    if (!subtitleTracks.length) return;
    const order: (string | null)[] = [null, ...subtitleTracks.map((t) => t.id)];
    const i = order.indexOf(subtitleTrackId);
    setSubtitleTrackId(order[(i + 1) % order.length]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [subtitleTracks, subtitleTrackId]);

  // ---- Loop-one is handled by the player; other modes on playToEnd ------
  // (streams can't element-loop — loop-one restarts the stream on playToEnd)
  useEffect(() => {
    player.loop = settings.playbackMode === 'loop-one' && !streamInfo;
  }, [player, settings.playbackMode, streamInfo]);

  const goToNext = useCallback(
    (wrap: boolean) => {
      if (index < playlist.length - 1) onIndexChange(index + 1);
      else if (wrap && playlist.length > 0) onIndexChange(0);
      else {
        wantPlayingRef.current = false;
        player.pause();
      }
    },
    [index, playlist.length, onIndexChange, player]
  );

  const goToPrev = useCallback(() => {
    // Restart current if we're past 3s, otherwise go to previous track.
    if (displayTime > 3 || index === 0) {
      seekToTime(0);
    } else {
      onIndexChange(index - 1);
    }
  }, [displayTime, index, onIndexChange, seekToTime]);

  useEffect(() => {
    const sub = player.addListener('playToEnd', () => {
      const info = streamRef.current;
      if (info) {
        const t = offsetRef.current + player.currentTime;
        // A live stream ending far from the real duration means ffmpeg died —
        // resume once where we stopped rather than skipping to the next video.
        if (info.duration - t > 15) {
          if (Date.now() - lastStreamRecovery.current > 10000) {
            lastStreamRecovery.current = Date.now();
            seekToTime(t);
          } else {
            setError('Playback stream ended unexpectedly.');
          }
          return;
        }
        if (settings.playbackMode === 'loop-one') {
          seekToTime(0);
          return;
        }
      }
      switch (settings.playbackMode) {
        case 'autoplay-next':
          goToNext(false);
          break;
        case 'loop-all':
          goToNext(true);
          break;
        case 'loop-one':
          // handled by player.loop (direct files)
          break;
        case 'once':
        default:
          wantPlayingRef.current = false;
          player.pause();
          break;
      }
    });
    return () => sub.remove();
  }, [player, settings.playbackMode, goToNext, seekToTime]);

  // ---- Persist resume position periodically -----------------------------
  useEffect(() => {
    if (!current || !knownDuration) return;
    const sec = Math.floor(displayTime);
    if (Math.abs(sec - lastSavedSecond.current) >= 5) {
      lastSavedSecond.current = sec;
      onSaveProgress({
        id: current.id,
        name: current.name,
        uri: current.uri,
        kind: current.kind,
        folderName: current.folderName,
        positionSeconds: displayTime,
        durationSeconds: knownDuration,
        lastPlayedAt: Date.now(),
      });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [displayTime, knownDuration]);

  // ---- Auto-hide controls ------------------------------------------------
  const revealControls = useCallback(() => {
    setControlsVisible(true);
    if (hideTimer.current) clearTimeout(hideTimer.current);
    hideTimer.current = setTimeout(() => {
      if (isPlayingRef.current) setControlsVisible(false);
    }, CONTROLS_TIMEOUT);
  }, []);

  useEffect(() => {
    revealControls();
    return () => {
      if (hideTimer.current) clearTimeout(hideTimer.current);
    };
  }, [revealControls, isPlaying]);

  // ---- Controls actions --------------------------------------------------
  const togglePlay = useCallback(() => {
    // Use our own event-driven state, not player.playing (unreliable on web).
    if (isPlayingRef.current) {
      wantPlayingRef.current = false;
      player.pause();
    } else {
      wantPlayingRef.current = true;
      player.play();
    }
    revealControls();
  }, [player, revealControls]);

  const seekBy = useCallback(
    (seconds: number) => {
      seekToTime(offsetRef.current + player.currentTime + seconds);
      revealControls();
    },
    [player, seekToTime, revealControls]
  );

  const seekToFraction = useCallback(
    (f: number) => {
      if (knownDuration) seekToTime(f * knownDuration);
      revealControls();
    },
    [knownDuration, seekToTime, revealControls]
  );

  const patchSettings = useCallback(
    (patch: Partial<Settings>) => onSettingsChange({ ...settings, ...patch }),
    [settings, onSettingsChange]
  );

  const setMode = (mode: PlaybackMode) => patchSettings({ playbackMode: mode });

  const toggleMute = useCallback(() => {
    const muted = !player.muted;
    player.muted = muted;
    patchSettings({ muted });
  }, [player, patchSettings]);

  const [rateMenuOpen, setRateMenuOpen] = useState(false);

  const selectRate = useCallback(
    (rate: number) => {
      player.playbackRate = rate;
      patchSettings({ playbackRate: rate });
      setRateMenuOpen(false);
    },
    [player, patchSettings]
  );

  // Don't leave the speed menu floating after the controls hide.
  useEffect(() => {
    if (!controlsVisible) setRateMenuOpen(false);
  }, [controlsVisible]);

  const [isFullscreen, setIsFullscreen] = useState(false);
  const toggleFullscreen = useCallback(async () => {
    if (Platform.OS === 'web') {
      const doc: any = document;
      if (doc.fullscreenElement) {
        await doc.exitFullscreen?.();
        setIsFullscreen(false);
      } else {
        await doc.documentElement.requestFullscreen?.();
        setIsFullscreen(true);
      }
    }
  }, []);

  // ---- Keyboard shortcuts (desktop / web) -------------------------------
  useEffect(() => {
    if (Platform.OS !== 'web') return;
    const onKey = (e: KeyboardEvent) => {
      switch (e.key) {
        case ' ': case 'k': e.preventDefault(); togglePlay(); break;
        case 'ArrowRight': seekBy(5); break;
        case 'ArrowLeft': seekBy(-5); break;
        case 'l': seekBy(10); break;
        case 'j': seekBy(-10); break;
        case 'ArrowUp': patchSettings({ volume: Math.min(1, settings.volume + 0.1) }); break;
        case 'ArrowDown': patchSettings({ volume: Math.max(0, settings.volume - 0.1) }); break;
        case 'm': toggleMute(); break;
        case 'f': toggleFullscreen(); break;
        case 'n': goToNext(false); break;
        case 'p': goToPrev(); break;
        case 'Escape': if (!document.fullscreenElement) onExit(); break;
      }
      revealControls();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [togglePlay, seekBy, toggleMute, toggleFullscreen, goToNext, goToPrev, onExit, revealControls, patchSettings, settings.volume]);

  // Keep player volume in sync when changed via keyboard/UI.
  useEffect(() => {
    player.volume = settings.volume;
    player.muted = settings.muted;
  }, [player, settings.volume, settings.muted]);

  const progress = knownDuration ? displayTime / knownDuration : 0;

  return (
    <View style={styles.root}>
      <Pressable style={styles.videoArea} onPress={togglePlay} onHoverIn={revealControls}>
        <VideoView
          player={player}
          style={styles.video}
          contentFit="contain"
          nativeControls={false}
          allowsPictureInPicture
        />

        {(loading && !error) && (
          <View style={styles.centerOverlay} pointerEvents="none">
            <ActivityIndicator size="large" color={theme.colors.accent} />
            {convertPercent != null && (
              <Text style={styles.convertText}>
                Optimizing audio for playback · {convertPercent}%
              </Text>
            )}
          </View>
        )}

        {error && (
          <View style={styles.centerOverlay}>
            <Ionicons name="alert-circle" size={40} color={theme.colors.danger} />
            <Text style={styles.errorText}>{error}</Text>
            <Pressable style={styles.retryBtn} onPress={() => goToNext(false)}>
              <Text style={styles.retryText}>Skip to next</Text>
            </Pressable>
          </View>
        )}

        {!isPlaying && !loading && !error && (
          <View style={styles.centerOverlay} pointerEvents="none">
            <View style={styles.bigPlay}>
              <Ionicons name="play" size={44} color="#fff" />
            </View>
          </View>
        )}
      </Pressable>

      {/* Subtitle cue overlay */}
      {cue && (
        <View style={[styles.subtitleWrap, { bottom: controlsVisible ? 120 : 44 }]} pointerEvents="none">
          <Text style={styles.subtitleText}>{cue.text}</Text>
        </View>
      )}

      {/* Top bar */}
      <View
        style={[styles.topBar, !controlsVisible && styles.hidden]}
        pointerEvents={controlsVisible ? 'auto' : 'none'}
      >
        <View style={styles.topRow}>
          <IconButton name="chevron-back" onPress={onExit} accessibilityLabel="Back to library" />
          <View style={styles.titleWrap}>
            <Text style={styles.title} numberOfLines={1}>{current?.name ?? ''}</Text>
            {current?.folderName ? (
              <Text style={styles.subtitle} numberOfLines={1}>
                {current.folderName} · {index + 1} of {playlist.length}
              </Text>
            ) : null}
          </View>
          <View style={styles.topSpacer} />
        </View>
        {/* Playback options: centered, see-through so the video stays visible */}
        <View style={styles.modeRow} pointerEvents="box-none">
          <PlaybackModeSelector mode={settings.playbackMode} onChange={setMode} translucent />
        </View>
      </View>

      {/* Bottom controls */}
      <View
        style={[styles.bottomBar, !controlsVisible && styles.hidden]}
        pointerEvents={controlsVisible ? 'auto' : 'none'}
      >
        <View style={styles.seekRow}>
          <Text style={styles.time}>{formatTime(displayTime)}</Text>
          <View style={styles.seekbar}>
            <Seekbar
              progress={progress}
              buffered={buffered}
              onScrub={() => revealControls()}
              onSeek={seekToFraction}
              labelFor={(f) => formatTime(f * knownDuration)}
            />
          </View>
          <Text style={styles.time}>{formatTime(knownDuration)}</Text>
        </View>

        <View style={styles.controlsRow}>
          <View style={styles.controlsGroup}>
            <IconButton name="play-skip-back" onPress={goToPrev} accessibilityLabel="Previous" />
            <Pressable
              style={({ hovered }: any) => [styles.skipBtn, hovered && styles.skipBtnHover]}
              onPress={() => seekBy(-10)}
              accessibilityRole="button"
              accessibilityLabel="Back 10 seconds"
            >
              <Ionicons name="play-back" size={13} color={theme.colors.text} />
              <Text style={styles.skipText}>-10sec</Text>
            </Pressable>
            <IconButton
              name={isPlaying ? 'pause' : 'play'}
              size={30}
              onPress={togglePlay}
              style={styles.playBtn}
              accessibilityLabel={isPlaying ? 'Pause' : 'Play'}
            />
            <Pressable
              style={({ hovered }: any) => [styles.skipBtn, hovered && styles.skipBtnHover]}
              onPress={() => seekBy(10)}
              accessibilityRole="button"
              accessibilityLabel="Forward 10 seconds"
            >
              <Text style={styles.skipText}>+10sec</Text>
              <Ionicons name="play-forward" size={13} color={theme.colors.text} />
            </Pressable>
            <IconButton
              name="play-skip-forward"
              onPress={() => goToNext(true)}
              disabled={playlist.length <= 1}
              accessibilityLabel="Next"
            />
          </View>

          <View style={styles.controlsGroup}>
            <IconButton
              name={settings.muted || settings.volume === 0 ? 'volume-mute' : settings.volume < 0.5 ? 'volume-low' : 'volume-high'}
              onPress={toggleMute}
              accessibilityLabel="Mute"
            />
            <View style={styles.volumeTrack}>
              <Seekbar
                progress={settings.muted ? 0 : settings.volume}
                onSeek={(f) => patchSettings({ volume: f, muted: false })}
                height={4}
              />
            </View>
            {subtitleTracks.length > 0 && (
              <Pressable
                style={[styles.ccBtn, subtitleTrackId ? styles.ccBtnActive : null]}
                onPress={cycleSubtitle}
                accessibilityRole="button"
                accessibilityLabel="Subtitles"
              >
                <Text style={[styles.ccText, subtitleTrackId ? styles.ccTextActive : null]}>CC</Text>
              </Pressable>
            )}
            <View style={styles.rateWrap}>
              {rateMenuOpen && (
                <View style={styles.rateMenu}>
                  {RATES.map((r) => (
                    <Pressable
                      key={r}
                      style={({ hovered }: any) => [
                        styles.rateItem,
                        hovered && styles.rateItemHover,
                        r === settings.playbackRate && styles.rateItemActive,
                      ]}
                      onPress={() => selectRate(r)}
                      accessibilityRole="button"
                      accessibilityLabel={`Speed ${r}x`}
                    >
                      <Text
                        style={[styles.rateItemText, r === settings.playbackRate && styles.rateItemTextActive]}
                      >
                        {r}×
                      </Text>
                    </Pressable>
                  ))}
                </View>
              )}
              <Pressable
                style={({ hovered }: any) => [styles.rateBtn, hovered && styles.skipBtnHover]}
                onPress={() => setRateMenuOpen((o) => !o)}
                accessibilityRole="button"
                accessibilityLabel="Playback speed"
              >
                <Text style={styles.rateText}>{settings.playbackRate}×</Text>
                <Ionicons
                  name={rateMenuOpen ? 'chevron-down' : 'chevron-up'}
                  size={13}
                  color={theme.colors.textMuted}
                />
              </Pressable>
            </View>
            {Platform.OS === 'web' && (
              <IconButton
                name={isFullscreen ? 'contract' : 'expand'}
                onPress={toggleFullscreen}
                accessibilityLabel="Fullscreen"
              />
            )}
          </View>
        </View>
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1, backgroundColor: '#000' },
  videoArea: { position: 'absolute', top: 0, left: 0, right: 0, bottom: 0, alignItems: 'center', justifyContent: 'center' },
  video: { width: '100%', height: '100%', backgroundColor: '#000' },
  centerOverlay: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    bottom: 0,
    alignItems: 'center',
    justifyContent: 'center',
    gap: 12,
  },
  bigPlay: {
    width: 88,
    height: 88,
    borderRadius: 44,
    backgroundColor: 'rgba(0,0,0,0.45)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  convertText: { color: theme.colors.text, fontSize: theme.font.small, fontWeight: '600' },
  subtitleWrap: { position: 'absolute', left: 0, right: 0, alignItems: 'center', paddingHorizontal: 24 },
  subtitleText: {
    color: '#fff',
    fontSize: 20,
    fontWeight: '600',
    textAlign: 'center',
    backgroundColor: 'rgba(0,0,0,0.72)',
    paddingHorizontal: 12,
    paddingVertical: 4,
    borderRadius: 6,
    overflow: 'hidden',
    // subtle outline for readability over bright frames
    textShadowColor: 'rgba(0,0,0,0.9)',
    textShadowOffset: { width: 0, height: 1 },
    textShadowRadius: 3,
  },
  ccBtn: {
    minWidth: 40,
    height: 30,
    paddingHorizontal: 8,
    borderRadius: 6,
    borderWidth: 1.5,
    borderColor: theme.colors.textMuted,
    alignItems: 'center',
    justifyContent: 'center',
  },
  ccBtnActive: { borderColor: theme.colors.accent, backgroundColor: theme.colors.accentSoft },
  ccText: { color: theme.colors.textMuted, fontWeight: '800', fontSize: theme.font.small, letterSpacing: 1 },
  ccTextActive: { color: theme.colors.accent },
  errorText: { color: theme.colors.text, fontSize: theme.font.body, textAlign: 'center', maxWidth: 360 },
  retryBtn: {
    marginTop: 8,
    paddingVertical: 10,
    paddingHorizontal: 18,
    backgroundColor: theme.colors.surfaceAlt,
    borderRadius: theme.radius.pill,
  },
  retryText: { color: theme.colors.text, fontWeight: '600' },
  topBar: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    paddingHorizontal: 16,
    paddingTop: 14,
    paddingBottom: 24,
  },
  topRow: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  topSpacer: { width: 44 },
  modeRow: { alignItems: 'center', marginTop: 10 },
  titleWrap: { flex: 1 },
  title: { color: '#fff', fontSize: theme.font.heading, fontWeight: '700' },
  subtitle: { color: theme.colors.textMuted, fontSize: theme.font.small, marginTop: 2 },
  bottomBar: {
    position: 'absolute',
    bottom: 0,
    left: 0,
    right: 0,
    paddingHorizontal: 16,
    paddingBottom: 18,
    paddingTop: 48,
  },
  hidden: { opacity: 0 },
  seekRow: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  seekbar: { flex: 1 },
  time: { color: theme.colors.text, fontSize: theme.font.small, fontVariant: ['tabular-nums'], width: 54, textAlign: 'center' },
  controlsRow: {
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    marginTop: 4,
    flexWrap: 'wrap',
    gap: 8,
  },
  controlsGroup: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  playBtn: { backgroundColor: theme.colors.accentSoft },
  volumeTrack: { width: 90 },
  skipBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
    height: 40,
    paddingHorizontal: 10,
    borderRadius: theme.radius.pill,
  },
  skipBtnHover: { backgroundColor: theme.colors.surfaceAlt },
  skipText: { color: theme.colors.text, fontWeight: '700', fontSize: theme.font.small },
  rateWrap: { position: 'relative' },
  rateBtn: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
    paddingHorizontal: 12,
    height: 44,
    justifyContent: 'center',
    borderRadius: theme.radius.pill,
  },
  rateText: { color: theme.colors.text, fontWeight: '700', fontSize: theme.font.body },
  rateMenu: {
    position: 'absolute',
    bottom: 50,
    right: 0,
    minWidth: 88,
    backgroundColor: 'rgba(13, 16, 22, 0.94)',
    borderRadius: theme.radius.md,
    borderWidth: 1,
    borderColor: theme.colors.border,
    paddingVertical: 4,
    zIndex: 60,
  },
  rateItem: { paddingVertical: 9, paddingHorizontal: 16 },
  rateItemHover: { backgroundColor: theme.colors.surfaceAlt },
  rateItemActive: { backgroundColor: theme.colors.accentSoft },
  rateItemText: { color: theme.colors.text, fontSize: theme.font.small, fontWeight: '600', textAlign: 'center' },
  rateItemTextActive: { color: theme.colors.accent, fontWeight: '800' },
});
