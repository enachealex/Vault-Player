/**
 * Vault Movies for Android: the server's film library as a streaming app.
 * Screens are a small stack held in state; Android's back gesture pops it.
 */
import { type ReactNode, useCallback, useEffect, useRef, useState } from 'react';
import { ActivityIndicator, Alert, AppState, BackHandler, StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import * as SplashScreen from 'expo-splash-screen';
import { SafeAreaProvider } from 'react-native-safe-area-context';
import { type Film, type Library, type ResumeRow, loadLibrary } from './src/library';
import { restore, session, signOut, useSession } from './src/session';
import { C } from './src/theme';
import { Btn, Logo } from './src/ui';
import { SignInScreen } from './src/screens/SignInScreen';
import { HomeScreen } from './src/screens/HomeScreen';
import { DetailScreen } from './src/screens/DetailScreen';
import { PlayerScreen } from './src/screens/PlayerScreen';

SplashScreen.preventAutoHideAsync().catch(() => {});

type Screen =
  | { name: 'detail'; film: Film }
  | { name: 'player'; film: Film; startMs: number };

type Gate =
  | { kind: 'booting' }
  | { kind: 'signin'; notice?: string }
  | { kind: 'loading' }
  | { kind: 'message'; title: string; text: string }
  | { kind: 'ready' };

// Stream URLs carry signed tokens that last 12 hours; refetch well before.
const STALE_MS = 60 * 60 * 1000;

export default function App() {
  const [gate, setGate] = useState<Gate>({ kind: 'booting' });
  const [lib, setLib] = useState<Library | null>(null);
  const [stack, setStack] = useState<Screen[]>([]);
  const [refreshing, setRefreshing] = useState(false);
  const sess = useSession();
  const libRef = useRef(lib);
  libRef.current = lib;

  const load = useCallback(async (quiet: boolean) => {
    if (!quiet) setGate({ kind: 'loading' });
    try {
      const r = await loadLibrary();
      if (r.kind === 'ok') { setLib(r.library); setGate({ kind: 'ready' }); }
      else if (r.kind === 'pending') setGate({ kind: 'message', title: 'Waiting for approval', text: r.message });
      else if (!quiet || !libRef.current) setGate({ kind: 'message', title: "Can't reach your library", text: r.message });
    } catch {
      setGate({ kind: 'signin', notice: 'Your session ended. Please sign in again.' });
    }
  }, []);

  // Launch: pick the saved session back up.
  useEffect(() => {
    (async () => {
      const ok = await restore();
      if (ok === true) await load(false);
      else if (ok === null) setGate({ kind: 'message', title: "Can't reach your library", text: "The server didn't answer. Check your connection and try again." });
      else setGate({ kind: 'signin' });
      SplashScreen.hideAsync().catch(() => {});
    })();
  }, [load]);

  // A session that ends mid-use (password changed elsewhere) lands on sign-in.
  useEffect(() => {
    if (gate.kind === 'ready' && !sess.refresh) { setStack([]); setGate({ kind: 'signin', notice: 'Your session ended. Please sign in again.' }); }
  }, [sess.refresh, gate.kind]);

  // Coming back to the app after a while: fresh catalogue, fresh stream tokens.
  useEffect(() => {
    const sub = AppState.addEventListener('change', s => {
      const l = libRef.current;
      if (s === 'active' && l && Date.now() - l.fetchedAt > STALE_MS) load(true);
    });
    return () => sub.remove();
  }, [load]);

  // Back pops a screen. The player handles its own (it saves first).
  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => {
      if (stack.length && stack[stack.length - 1].name !== 'player') { setStack(s => s.slice(0, -1)); return true; }
      return false;
    });
    return () => sub.remove();
  }, [stack]);

  const onSaved = useCallback((rows: ResumeRow[]) => {
    setLib(l => {
      if (!l) return l;
      const resume = { ...l.resume };
      for (const r of rows) {
        const cur = resume[r.filmKey];
        if (!cur || r.updatedAt >= cur.updatedAt) resume[r.filmKey] = r;
      }
      return { ...l, resume };
    });
  }, []);

  const play = (film: Film, startMs: number) => {
    if (lib && Date.now() - lib.fetchedAt > 10 * 60 * 60 * 1000) {
      // Tokens in this catalogue are nearly spent: refresh first, then play.
      load(true).then(() => setStack(s => [...s, { name: 'player', film, startMs }]));
      return;
    }
    setStack(s => [...s, { name: 'player', film, startMs }]);
  };

  const account = () => {
    const me = session().me;
    Alert.alert(
      me?.displayName || me?.username || 'Account',
      `Signed in as ${me?.username ?? ''}${me?.isOwner ? ' (owner)' : ''}\n${session().server.replace(/^https?:\/\//, '')}`,
      [
        { text: 'Refresh library', onPress: () => { setRefreshing(true); load(true).finally(() => setRefreshing(false)); } },
        { text: 'Sign out', style: 'destructive', onPress: async () => { await signOut(); setStack([]); setLib(null); setGate({ kind: 'signin' }); } },
        { text: 'Close', style: 'cancel' },
      ],
    );
  };

  const top = stack[stack.length - 1];
  let body: ReactNode;
  if (gate.kind === 'booting') body = <View style={styles.fill} />;
  else if (gate.kind === 'signin') body = <SignInScreen notice={gate.notice} onSignedIn={() => load(false)} />;
  else if (gate.kind === 'loading') body = (
    <View style={[styles.fill, styles.center]}><ActivityIndicator size="large" color={C.text} /></View>
  );
  else if (gate.kind === 'message') body = (
    <View style={[styles.fill, styles.center, { padding: 24 }]}>
      <View style={styles.panel}>
        <Logo size={40} />
        <Text style={styles.h1}>{gate.title}</Text>
        <Text style={styles.p}>{gate.text}</Text>
        <View style={{ gap: 10 }}>
          <Btn label={gate.title === 'Waiting for approval' ? 'Check again' : 'Try again'} onPress={async () => {
            if (!session().access) { const ok = await restore(); if (ok !== true) { setGate(ok === false ? { kind: 'signin' } : gate); return; } }
            load(false);
          }} />
          <Btn ghost label="Sign out" onPress={async () => { await signOut(); setGate({ kind: 'signin' }); }} />
        </View>
      </View>
    </View>
  );
  else if (lib) {
    body = (
      <>
        <HomeScreen lib={lib} refreshing={refreshing}
          onRefresh={() => { setRefreshing(true); load(true).finally(() => setRefreshing(false)); }}
          onOpen={f => setStack(s => [...s, { name: 'detail', film: f }])}
          onPlay={play} onAccount={account} />
        {stack.map((sc, i) => (
          <View key={i} style={StyleSheet.absoluteFill}>
            {sc.name === 'detail' && (
              <DetailScreen film={sc.film} lib={lib} onBack={() => setStack(s => s.slice(0, i))} onPlay={play} />
            )}
            {sc.name === 'player' && (
              <PlayerScreen film={sc.film} startMs={sc.startMs} prevRow={lib.resume[sc.film.filmKey]}
                onSaved={onSaved} onExit={() => setStack(s => s.slice(0, i))} />
            )}
          </View>
        ))}
      </>
    );
  }

  return (
    <SafeAreaProvider>
      <StatusBar style="light" hidden={top?.name === 'player'} />
      <View style={styles.fill}>{body}</View>
    </SafeAreaProvider>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: C.bg },
  center: { alignItems: 'center', justifyContent: 'center' },
  panel: {
    width: '100%', maxWidth: 420, padding: 26, borderRadius: 20,
    backgroundColor: C.smokeHi, borderWidth: 1, borderColor: C.line,
  },
  h1: { color: C.text, fontSize: 22, fontWeight: '800', marginTop: 16, marginBottom: 8 },
  p: { color: C.muted, fontSize: 15, lineHeight: 22, marginBottom: 20 },
});
