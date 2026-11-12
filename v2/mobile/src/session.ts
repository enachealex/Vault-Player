/**
 * Who is signed in, to which server, and the one function every request goes
 * through. The contract is v2/docs/TV-APP-INTEGRATION.md.
 *
 * The refresh token lives in SecureStore (the Android Keystore); the access
 * token only in memory. Refresh tokens are single-use, so every caller shares
 * one in-flight refresh: two racing with the same token would end the session.
 */
import * as SecureStore from 'expo-secure-store';
import { useSyncExternalStore } from 'react';

export const DEFAULT_SERVER = 'https://party.thejumpvault.com';
const KEY_RT = 'vault.refresh';
const KEY_SERVER = 'vault.server';

export type Me = {
  displayName: string;
  username: string;
  status: string;
  isOwner: boolean;
};

type AuthResponse = Me & { accessToken: string; refreshToken: string; expiresAt: number };

type State = {
  server: string;
  access: string | null;
  refresh: string | null;
  expiresAt: number;
  me: Me | null;
};

let state: State = { server: DEFAULT_SERVER, access: null, refresh: null, expiresAt: 0, me: null };
const listeners = new Set<() => void>();
function set(patch: Partial<State>) {
  state = { ...state, ...patch };
  listeners.forEach(l => l());
}

export function useSession(): State {
  return useSyncExternalStore(
    l => { listeners.add(l); return () => { listeners.delete(l); }; },
    () => state,
  );
}
export const session = () => state;

/** A path from the API (always relative) made absolute against the server. */
export const abs = (path: string) => state.server + path;

export class SignedOut extends Error {
  constructor() { super('signed-out'); }
}

async function store(j: AuthResponse) {
  set({
    access: j.accessToken, refresh: j.refreshToken, expiresAt: j.expiresAt || 0,
    me: { displayName: j.displayName, username: j.username, status: j.status, isOwner: j.isOwner },
  });
  // Persist the NEW refresh token at once; the old one is already spent.
  try { await SecureStore.setItemAsync(KEY_RT, j.refreshToken); } catch {}
}

let refreshing: Promise<boolean | null> | null = null;
/** true = refreshed; false = the session is over; null = the server could not be reached. */
export function refresh(): Promise<boolean | null> {
  if (refreshing) return refreshing;
  refreshing = (async () => {
    try {
      const r = await fetch(abs('/api/v1/refresh'), {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: state.refresh }),
      });
      if (r.ok) { await store(await r.json()); return true; }
      return r.status === 401 ? false : null;
    } catch { return null; }
  })().finally(() => { refreshing = null; });
  return refreshing;
}

/** Restore a saved session at launch. Same three answers as refresh(). */
export async function restore(): Promise<boolean | null> {
  try {
    const server = await SecureStore.getItemAsync(KEY_SERVER);
    if (server) set({ server });
    const rt = await SecureStore.getItemAsync(KEY_RT);
    if (!rt) return false;
    set({ refresh: rt });
  } catch { return false; }
  const ok = await refresh();
  if (ok === false) await forget();
  return ok;
}

export async function signIn(server: string, username: string, password: string): Promise<string | null> {
  const base = normaliseServer(server);
  let r: Response;
  try {
    r = await fetch(base + '/api/v1/login', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: username.trim(), password }),
    });
  } catch {
    return 'Could not reach the server. Check the address and your connection.';
  }
  const j = await r.json().catch(() => ({}));
  if (!r.ok) return j.error || 'Could not sign in.';
  set({ server: base });
  try { await SecureStore.setItemAsync(KEY_SERVER, base); } catch {}
  await store(j);
  return null;
}

export async function signOut() {
  const rt = state.refresh;
  if (rt) {
    fetch(abs('/api/v1/logout'), {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: rt }),
    }).catch(() => {});
  }
  await forget();
}

async function forget() {
  set({ access: null, refresh: null, expiresAt: 0, me: null });
  try { await SecureStore.deleteItemAsync(KEY_RT); } catch {}
}

export function normaliseServer(s: string): string {
  let v = s.trim().replace(/\/+$/, '');
  if (!v) return DEFAULT_SERVER;
  if (!/^https?:\/\//i.test(v)) v = 'https://' + v;
  return v;
}

/**
 * fetch() against the server with the bearer token. A token about to lapse is
 * renewed first; a 401 gets one refresh and one retry. Throws SignedOut when
 * the session is over, so callers can leave quietly.
 */
export async function api(path: string, init: RequestInit = {}): Promise<Response> {
  if (state.refresh && state.expiresAt && Date.now() > state.expiresAt - 60_000) await refresh();
  const go = () => fetch(abs(path), {
    ...init,
    headers: {
      ...(init.headers as Record<string, string> | undefined),
      ...(state.access ? { Authorization: 'Bearer ' + state.access } : {}),
      ...(init.body ? { 'Content-Type': 'application/json' } : {}),
    },
  });
  let r = await go();
  if (r.status === 401 && state.refresh) {
    const ok = await refresh();
    if (ok === false) { await forget(); throw new SignedOut(); }
    if (ok) r = await go();
  }
  return r;
}

/** Headers for an image the server only gives to signed-in people (posters). */
export const authHeaders = (): Record<string, string> =>
  state.access ? { Authorization: 'Bearer ' + state.access } : {};
