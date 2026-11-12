/**
 * Full-screen playback, landscape, with the platform's own controls.
 *
 * Not the player's native fullscreen mode: on Android that pauses the JS
 * runtime, and with it the periodic resume saves. Instead this screen fills
 * the display itself, hides the system bars and locks to landscape.
 *
 * Routes (see routeFor): the file itself when this phone can decode it, else
 * the server's HLS copy. A file that fails here anyway falls back to HLS once.
 */
import { useEffect, useRef, useState } from 'react';
import { ActivityIndicator, AppState, BackHandler, Pressable, StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import { useEventListener } from 'expo';
import { VideoView, useVideoPlayer, type VideoSource } from 'expo-video';
import * as ScreenOrientation from 'expo-screen-orientation';
import { NavigationBar } from 'expo-navigation-bar';
import Ionicons from '@expo/vector-icons/Ionicons';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { type Film, type ResumeRow, pushResume, routeFor } from '../library';
import { abs, api } from '../session';
import { C } from '../theme';
import { Btn } from '../ui';

type Phase =
  | { kind: 'loading' }
  | { kind: 'preparing'; percent: number }
  | { kind: 'playing' }
  | { kind: 'error'; title: string; text: string };

export function PlayerScreen({ film, startMs, prevRow, onExit, onSaved }: {
  film: Film;
  startMs: number;
  prevRow: ResumeRow | undefined;
  onExit: () => void;
  onSaved: (rows: ResumeRow[]) => void;
}) {
  const insets = useSafeAreaInsets();
  const player = useVideoPlayer(null, p => { p.timeUpdateEventInterval = 1; });
  const [phase, setPhase] = useState<Phase>({ kind: 'loading' });
  const [chrome, setChrome] = useState(true);

  const route = useRef(routeFor(film));
  const pendingSeek = useRef<number | null>(startMs / 1000);
  const started = useRef(false);
  const pos = useRef(startMs / 1000);
  const triedHls = useRef(false);
  const alive = useRef(true);
  const pollTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  // ---- saving your place ---------------------------------------------------
  // Nothing is written until the film has actually played: backing out of
  // "getting ready" must not overwrite a good bookmark with zero.
  const save = (finished: boolean) => {
    if (!started.current) return;
    if (!finished && pos.current < 1) return;
    const now = Date.now();
    const row: ResumeRow = {
      filmKey: film.filmKey, name: film.name,
      resumeMs: finished ? 0 : Math.floor(pos.current * 1000),
      watchCount: (prevRow?.watchCount || 0) + (finished ? 1 : 0),
      lastWatchedAt: now, chaptersJson: prevRow?.chaptersJson ?? null, updatedAt: now,
    };
    onSaved([row]);                               // show it at once
    pushResume(row).then(m => { if (m) onSaved(m); });
  };
  const saveRef = useRef(save);
  saveRef.current = save;

  // ---- choosing and loading the source -------------------------------------
  function load(src: VideoSource) {
    if (!alive.current) return;
    setPhase({ kind: 'loading' });
    player.replaceAsync(src)
      .then(() => { if (alive.current) player.play(); })
      .catch(() => {});   // a failed load arrives as statusChange 'error'
  }

  function playDirect() {
    load({ uri: abs(film.streamUrl), contentType: 'progressive', metadata: { title: film.name } });
  }

  function prepareHls() {
    triedHls.current = true;
    setPhase({ kind: 'preparing', percent: 0 });
    const poll = async () => {
      if (!alive.current) return;
      let r: Response, j: any;
      try { r = await api(film.hlsUrl); j = await r.json().catch(() => null); }
      catch (e) {
        if (e instanceof Error && e.message === 'signed-out') { onExit(); return; }
        pollTimer.current = setTimeout(poll, 4000);
        return;
      }
      if (!alive.current) return;
      if (r.status === 409 || !j || j.error || j.state === 'failed' || j.state === 'unsupported') {
        setPhase({ kind: 'error', title: "Couldn't prepare this film",
          text: (j && j.error) || 'Watch it in the Vault Movies desktop app instead.' });
        return;
      }
      if (j.ready && j.playlistUrl) {
        // A film still being written is an EVENT playlist, which the player
        // treats as live and would open at its far end; the seek on ready
        // below puts it where it belongs, even when that is the very start.
        if (pendingSeek.current === null) pendingSeek.current = 0;
        load({ uri: abs(j.playlistUrl), contentType: 'hls', metadata: { title: film.name } });
        return;
      }
      setPhase({ kind: 'preparing', percent: j.percent || 0 });
      pollTimer.current = setTimeout(poll, 2000);
    };
    poll();
  }

  useEffect(() => {
    alive.current = true;
    if (route.current === 'direct') playDirect();
    else if (route.current === 'hls') prepareHls();
    else setPhase({ kind: 'error', title: "This phone can't play this film", text: 'Watch it in the Vault Movies desktop app.' });
    return () => {
      alive.current = false;
      if (pollTimer.current) clearTimeout(pollTimer.current);
      saveRef.current(false);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // ---- player events -----------------------------------------------------------
  useEventListener(player, 'statusChange', ({ status, error }) => {
    if (status === 'readyToPlay') {
      if (pendingSeek.current !== null) {
        const t = pendingSeek.current;
        pendingSeek.current = null;
        if (t > 0 || route.current === 'hls' || triedHls.current) player.currentTime = t;
      }
      setPhase(ph => (ph.kind === 'error' ? ph : { kind: 'playing' }));
    } else if (status === 'error') {
      // The file would not play on this phone after all. Its server copy has
      // the audio converted, which is nearly always the reason.
      if (!triedHls.current && film.hlsAvailable) {
        pendingSeek.current = started.current ? pos.current : startMs / 1000;
        prepareHls();
        return;
      }
      setPhase({ kind: 'error', title: 'Playback stopped',
        text: (error?.message ? error.message + '\n\n' : '') + 'This film may only play in the Vault Movies desktop app.' });
    }
  });
  useEventListener(player, 'playingChange', ({ isPlaying }) => {
    if (isPlaying) { started.current = true; bumpChrome(); }
    else { setChrome(true); saveRef.current(false); }
  });
  useEventListener(player, 'timeUpdate', ({ currentTime }) => {
    if (currentTime > 0) pos.current = currentTime;
  });
  useEventListener(player, 'playToEnd', () => {
    // Android's player also reports "ended" for the empty source it starts
    // with, the moment the real one replaces it. Only a film that has
    // actually played can have finished.
    if (!started.current) return;
    saveRef.current(true);
    started.current = false;          // already saved as finished
    onExit();
  });

  // Every 15 seconds while playing, and whenever the app goes to the background.
  useEffect(() => {
    const t = setInterval(() => { if (player.playing) saveRef.current(false); }, 15000);
    const sub = AppState.addEventListener('change', s => { if (s !== 'active') saveRef.current(false); });
    return () => { clearInterval(t); sub.remove(); };
  }, [player]);

  // ---- the screen ------------------------------------------------------------------
  useEffect(() => {
    ScreenOrientation.lockAsync(ScreenOrientation.OrientationLock.LANDSCAPE).catch(() => {});
    try { NavigationBar.setHidden(true); } catch {}
    const back = BackHandler.addEventListener('hardwareBackPress', () => { onExit(); return true; });
    return () => {
      back.remove();
      try { NavigationBar.setHidden(false); } catch {}
      ScreenOrientation.unlockAsync().catch(() => {});
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const chromeTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  function bumpChrome() {
    setChrome(true);
    if (chromeTimer.current) clearTimeout(chromeTimer.current);
    chromeTimer.current = setTimeout(() => { if (player.playing) setChrome(false); }, 3500);
  }
  useEffect(() => () => { if (chromeTimer.current) clearTimeout(chromeTimer.current); }, []);

  return (
    <View style={styles.fill}>
      <StatusBar hidden />
      <VideoView
        style={StyleSheet.absoluteFill}
        player={player}
        nativeControls
        contentFit="contain"
        fullscreenOptions={{ enable: false }}
        buttonOptions={{ showNext: false, showPrevious: false }}
        allowsPictureInPicture
        startsPictureInPictureAutomatically
        onFirstFrameRender={bumpChrome}
      />

      {(phase.kind === 'preparing' || phase.kind === 'loading' || phase.kind === 'error') && (
        <View style={[StyleSheet.absoluteFill, styles.center, phase.kind !== 'loading' && { backgroundColor: '#000' }]}
          pointerEvents={phase.kind === 'loading' ? 'none' : 'auto'}>
          {phase.kind === 'loading' && <ActivityIndicator size="large" color="#fff" />}
          {phase.kind === 'preparing' && (
            <>
              <ActivityIndicator size="large" color="#fff" style={{ marginBottom: 18 }} />
              <Text style={styles.pt}>Getting this film ready</Text>
              {phase.percent > 0 && <Text style={styles.pp}>{phase.percent}%</Text>}
              <Text style={styles.ps}>This happens once per film. Next time it starts straight away.</Text>
            </>
          )}
          {phase.kind === 'error' && (
            <>
              <Text style={styles.pt}>{phase.title}</Text>
              <Text style={[styles.ps, { marginBottom: 20 }]}>{phase.text}</Text>
              <Btn label="Back" onPress={onExit} />
            </>
          )}
        </View>
      )}

      {chrome && phase.kind !== 'error' && (
        <View style={[styles.topBar, { paddingLeft: Math.max(insets.left, 16), paddingRight: Math.max(insets.right, 16) }]}
          pointerEvents="box-none">
          <Pressable onPress={onExit} style={styles.back} hitSlop={8} accessibilityLabel="Back">
            <Ionicons name="chevron-back" size={22} color={C.text} />
          </Pressable>
          <Text style={styles.title} numberOfLines={1}>{film.name}</Text>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: '#000' },
  center: { alignItems: 'center', justifyContent: 'center', padding: 24 },
  pt: { color: C.text, fontSize: 19, fontWeight: '700', marginBottom: 8, textAlign: 'center' },
  pp: { color: C.text, fontSize: 40, fontWeight: '800', marginBottom: 8 },
  ps: { color: C.muted, fontSize: 14.5, lineHeight: 21, textAlign: 'center', maxWidth: 440 },
  topBar: { position: 'absolute', top: 14, left: 0, right: 0, flexDirection: 'row', alignItems: 'center', gap: 12 },
  back: {
    width: 40, height: 40, borderRadius: 10, alignItems: 'center', justifyContent: 'center',
    backgroundColor: 'rgba(11,13,18,0.6)', borderWidth: 1, borderColor: C.line,
  },
  title: { color: C.text, fontSize: 16, fontWeight: '700', flexShrink: 1,
    textShadowColor: 'rgba(0,0,0,0.8)', textShadowRadius: 6 },
});
