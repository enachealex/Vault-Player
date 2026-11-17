/**
 * Self-update for the sideloaded Android app: download a newer APK from the
 * Vault server and hand it to Android's package installer. On Android 12 and
 * later an app may update itself without a confirmation screen, once the user
 * has allowed it to install apps.
 */
import { requireOptionalNativeModule } from 'expo';

type EventSubscription = { remove(): void };

export type InstallStatus =
  | { status: 'confirm' }              // Android is showing its own Update screen
  | { status: 'success' }
  | { status: 'failed'; message: string };

type Native = {
  installedVersion(): { versionCode: number; versionName: string };
  canInstall(): boolean;
  openInstallSettings(): void;
  downloadAndInstall(url: string, headers: Record<string, string>, sha256: string): Promise<void>;
  addListener(event: 'onProgress', cb: (e: { received: number; total: number }) => void): EventSubscription;
  addListener(event: 'onStatus', cb: (e: InstallStatus) => void): EventSubscription;
};

// Null where the module is not compiled in (Expo Go, iOS, web).
export const Updater = requireOptionalNativeModule<Native>('VaultUpdater');
