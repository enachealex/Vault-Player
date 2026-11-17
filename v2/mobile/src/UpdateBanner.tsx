/**
 * "A new version is ready": the server publishes the app's latest build
 * (GET /api/v1/app/android) and this offers it. On Update the app downloads
 * the APK and installs it over itself; the user is asked nothing more than,
 * the very first time, to let Vault Movies install apps.
 */
import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, AppState, Platform, StyleSheet, Text, View } from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { Updater } from '../modules/vault-updater';
import { abs, api, authHeaders } from './session';
import { C, RADIUS } from './theme';
import { Btn, ProgressBar } from './ui';

type Release = {
  versionCode: number;
  versionName: string;
  notes: string | null;
  sizeBytes: number;
  sha256: string;
  url: string;
};

type Phase =
  | { kind: 'idle' }
  | { kind: 'offer'; release: Release }
  | { kind: 'permission'; release: Release }
  | { kind: 'downloading'; release: Release; frac: number }
  | { kind: 'installing'; release: Release; confirm: boolean }
  | { kind: 'error'; release: Release; message: string };

// Look again when the app comes back after this long.
const RECHECK_MS = 6 * 60 * 60 * 1000;

async function latest(): Promise<Release | null> {
  if (!Updater) return null;
  try {
    const r = await api('/api/v1/app/android');
    if (!r.ok) return null;
    const rel = (await r.json()) as Release;
    return rel.versionCode > Updater.installedVersion().versionCode ? rel : null;
  } catch { return null; }
}

export function UpdateBanner({ hidden }: { hidden: boolean }) {
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const phaseRef = useRef(phase);
  phaseRef.current = phase;
  const checkedAt = useRef(0);
  const declined = useRef<number | null>(null); // "Later" for this version, this run
  const insets = useSafeAreaInsets();

  const check = useCallback(async () => {
    if (phaseRef.current.kind !== 'idle') return;
    checkedAt.current = Date.now();
    const rel = await latest();
    if (rel && rel.versionCode !== declined.current && phaseRef.current.kind === 'idle') {
      setPhase({ kind: 'offer', release: rel });
    }
  }, []);

  const start = useCallback(async (release: Release) => {
    if (!Updater) return;
    if (!Updater.canInstall()) {
      setPhase({ kind: 'permission', release });
      Alert.alert(
        'Allow updates',
        'Android will open a settings page. Turn on "Allow from this source" for Vault Movies, then come back here and the update will carry on by itself. You only need to do this once.',
        [
          { text: 'Not now', style: 'cancel', onPress: () => setPhase({ kind: 'offer', release }) },
          { text: 'Open settings', onPress: () => { try { Updater?.openInstallSettings(); } catch {} } },
        ],
      );
      return;
    }
    setPhase({ kind: 'downloading', release, frac: 0 });
    try {
      // Asking again refreshes the sign-in if it is about to lapse, and makes
      // sure the checksum is for the file that is there now.
      const fresh = (await latest()) ?? release;
      await Updater.downloadAndInstall(abs(fresh.url), authHeaders(), fresh.sha256);
      setPhase(p => (p.kind === 'downloading' ? { kind: 'installing', release: fresh, confirm: false } : p));
    } catch (e) {
      setPhase({ kind: 'error', release, message: e instanceof Error ? e.message : 'The update could not be downloaded.' });
    }
  }, []);

  // Download progress and the installer's answer.
  useEffect(() => {
    if (!Updater) return;
    const a = Updater.addListener('onProgress', ({ received, total }) => {
      setPhase(p => (p.kind === 'downloading' && total > 0 ? { ...p, frac: received / total } : p));
    });
    const b = Updater.addListener('onStatus', s => {
      setPhase(p => {
        if (p.kind === 'idle' || p.kind === 'offer') return p;
        if (s.status === 'confirm') return { kind: 'installing', release: p.release, confirm: true };
        if (s.status === 'failed') return { kind: 'error', release: p.release, message: s.message };
        return { kind: 'idle' }; // success: the app is normally replaced before this arrives
      });
    });
    return () => { a.remove(); b.remove(); };
  }, []);

  // At launch, and on coming back: look for a release, or carry on once the
  // install permission has been granted in Settings.
  useEffect(() => {
    if (!Updater || Platform.OS !== 'android') return;
    check();
    const sub = AppState.addEventListener('change', s => {
      if (s !== 'active') return;
      const p = phaseRef.current;
      if (p.kind === 'permission') {
        if (Updater?.canInstall()) start(p.release);
        else setPhase({ kind: 'offer', release: p.release });
      } else if (p.kind === 'installing' && p.confirm) {
        // Back from Android's own Update screen without updating.
        setPhase({ kind: 'offer', release: p.release });
      } else if (Date.now() - checkedAt.current > RECHECK_MS) check();
    });
    return () => sub.remove();
  }, [check, start]);

  if (hidden || phase.kind === 'idle') return null;
  const rel = phase.release;
  const mb = Math.max(1, Math.round(rel.sizeBytes / 1048576));

  let title: string, text: string | null = null;
  switch (phase.kind) {
    case 'offer':
      title = `Update available: ${rel.versionName}`;
      text = rel.notes || `A newer version of Vault Movies is ready (${mb} MB).`;
      break;
    case 'permission':
      title = 'Waiting for permission';
      text = 'Turn on "Allow from this source" for Vault Movies in Settings, then come back.';
      break;
    case 'downloading':
      title = `Downloading ${rel.versionName}… ${Math.round(phase.frac * 100)}%`;
      break;
    case 'installing':
      title = 'Installing the update';
      text = phase.confirm
        ? 'Tap Update on the Android screen to finish.'
        : 'Vault Movies will close in a moment. Open it again to use the new version.';
      break;
    case 'error':
      title = "The update didn't install";
      text = phase.message;
      break;
  }

  return (
    <View pointerEvents="box-none" style={[StyleSheet.absoluteFill, styles.wrap, { paddingBottom: insets.bottom + 16 }]}>
      <View style={styles.panel}>
        <Text style={styles.title}>{title}</Text>
        {text && <Text style={styles.text}>{text}</Text>}
        {phase.kind === 'downloading' && <ProgressBar frac={phase.frac} style={{ marginTop: 12 }} />}
        {(phase.kind === 'offer' || phase.kind === 'error' || phase.kind === 'permission') && (
          <View style={styles.row}>
            <View style={{ flex: 1 }}>
              <Btn ghost label="Later" onPress={() => { declined.current = rel.versionCode; setPhase({ kind: 'idle' }); }} />
            </View>
            <View style={{ flex: 1 }}>
              <Btn label={phase.kind === 'error' ? 'Try again' : 'Update'} onPress={() => start(rel)} />
            </View>
          </View>
        )}
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { justifyContent: 'flex-end', paddingHorizontal: 16 },
  panel: {
    alignSelf: 'center', width: '100%', maxWidth: 480,
    padding: 18, borderRadius: RADIUS + 4,
    backgroundColor: C.smokeHi, borderWidth: 1, borderColor: C.lineHi,
  },
  title: { color: C.text, fontSize: 16, fontWeight: '800' },
  text: { color: C.muted, fontSize: 14, lineHeight: 20, marginTop: 6 },
  row: { flexDirection: 'row', gap: 10, marginTop: 14 },
});
