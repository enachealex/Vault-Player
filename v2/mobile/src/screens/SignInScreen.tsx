import { useState } from 'react';
import {
  ActivityIndicator, KeyboardAvoidingView, Pressable, ScrollView, StyleSheet, Text, TextInput, View,
} from 'react-native';
import { LinearGradient } from 'expo-linear-gradient';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { DEFAULT_SERVER, session, signIn } from '../session';
import { C, RADIUS } from '../theme';
import { Btn, Logo } from '../ui';

export function SignInScreen({ onSignedIn, notice }: { onSignedIn: () => void; notice?: string | null }) {
  const insets = useSafeAreaInsets();
  const [server, setServer] = useState(session().server || DEFAULT_SERVER);
  const [showServer, setShowServer] = useState(session().server !== DEFAULT_SERVER);
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [err, setErr] = useState<string | null>(notice || null);

  async function go() {
    if (!username.trim() || !password) { setErr('Enter your username and password.'); return; }
    setBusy(true); setErr(null);
    const e = await signIn(server, username, password);
    setBusy(false);
    if (e) { setErr(e); return; }
    setPassword('');
    onSignedIn();
  }

  return (
    <View style={{ flex: 1, backgroundColor: C.bg }}>
      <LinearGradient colors={['rgba(122,60,36,0.55)', 'rgba(11,13,18,0)']} style={StyleSheet.absoluteFill}
        start={{ x: 1, y: 0 }} end={{ x: 0.3, y: 0.6 }} />
      <LinearGradient colors={['rgba(11,13,18,0)', 'rgba(26,58,110,0.5)']} style={StyleSheet.absoluteFill}
        start={{ x: 0.7, y: 0.4 }} end={{ x: 0, y: 1 }} />
      <KeyboardAvoidingView behavior="height" style={{ flex: 1 }}>
        <ScrollView contentContainerStyle={[styles.wrap, { paddingTop: insets.top + 24, paddingBottom: insets.bottom + 24 }]}
          keyboardShouldPersistTaps="handled">
          <View style={styles.panel}>
            <Logo size={40} />
            <Text style={styles.h1}>Vault Movies</Text>
            <Text style={styles.p}>Sign in with your Vault account to watch your library.</Text>

            <Text style={styles.label}>Username or email</Text>
            <TextInput style={styles.input} value={username} onChangeText={setUsername}
              autoCapitalize="none" autoCorrect={false} autoComplete="username" textContentType="username"
              returnKeyType="next" placeholderTextColor={C.faint} />

            <Text style={styles.label}>Password</Text>
            <TextInput style={styles.input} value={password} onChangeText={setPassword}
              secureTextEntry autoComplete="current-password" textContentType="password"
              returnKeyType="go" onSubmitEditing={go} />

            {showServer && (
              <>
                <Text style={styles.label}>Server</Text>
                <TextInput style={styles.input} value={server} onChangeText={setServer}
                  autoCapitalize="none" autoCorrect={false} keyboardType="url" placeholder={DEFAULT_SERVER}
                  placeholderTextColor={C.faint} />
              </>
            )}

            <View style={{ marginTop: 8 }}>
              {busy ? <ActivityIndicator color={C.text} style={{ paddingVertical: 14 }} />
                : <Btn label="Sign in" onPress={go} />}
            </View>
            {!!err && <Text style={styles.err}>{err}</Text>}

            {!showServer && (
              <Pressable onPress={() => setShowServer(true)} style={{ marginTop: 18, alignSelf: 'center', padding: 6 }}>
                <Text style={styles.link}>Use a different server</Text>
              </Pressable>
            )}
          </View>
        </ScrollView>
      </KeyboardAvoidingView>
    </View>
  );
}

const styles = StyleSheet.create({
  wrap: { flexGrow: 1, justifyContent: 'center', paddingHorizontal: 18 },
  panel: {
    width: '100%', maxWidth: 420, alignSelf: 'center', padding: 26, borderRadius: 20,
    backgroundColor: C.smokeHi, borderWidth: 1, borderColor: C.line,
  },
  h1: { color: C.text, fontSize: 24, fontWeight: '800', marginTop: 16, marginBottom: 6 },
  p: { color: C.muted, fontSize: 15, lineHeight: 22, marginBottom: 18 },
  label: { color: C.muted, fontSize: 13, marginBottom: 6 },
  input: {
    color: C.text, fontSize: 16, paddingHorizontal: 14, paddingVertical: 12, borderRadius: RADIUS,
    backgroundColor: 'rgba(0,0,0,0.35)', borderWidth: 1, borderColor: C.line, marginBottom: 14,
  },
  err: { color: C.danger, fontSize: 14, marginTop: 12, lineHeight: 20 },
  link: { color: C.muted, fontSize: 14, textDecorationLine: 'underline' },
});
