import { useEffect, useState } from "react";
import { api } from "@plenipo/client";
import { INSTALLATION_ID_KEY, mobileConfig } from "../config";

/**
 * Registers this installation for push, and reports back the link of any notification the user
 * taps.
 *
 * Registration runs on every launch on purpose. Push tokens rotate — an OS update, a reinstall, a
 * restore from backup — and the server matches on the installation id, so re-registering is an
 * update rather than a new row. An app that only registered once would go quietly unreachable.
 *
 * Every step is allowed to fail without consequence. Notifications are an enhancement; a declined
 * permission, an offline launch, or a simulator with no push support must all leave a perfectly
 * working app.
 */
export function useDeviceRegistration(): { pendingLink: string | null; clearPendingLink: () => void } {
  const [pendingLink, setPendingLink] = useState<string | null>(null);

  useEffect(() => {
    const { push, device } = mobileConfig();
    if (push == null || device == null) return undefined;

    let cancelled = false;

    void (async () => {
      try {
        const token = await push.getPushToken();
        // Declining notifications is a normal, respected answer — not an error, and not something
        // to ask about again on the next launch.
        if (token == null || cancelled) return;

        await api.notifications.devices.register({
          installationId: await installationId(),
          pushToken: token,
          platform: device.platform(),
          deviceName: device.deviceName() ?? undefined,
        });
      } catch {
        // Offline, or the API rejected it. The app is fully usable either way, and the next
        // launch tries again — so there is nothing worth interrupting the user about.
      }
    })();

    const unsubscribe = push.onNotificationTapped((link) => setPendingLink(link));

    return () => {
      cancelled = true;
      unsubscribe();
    };
  }, []);

  return { pendingLink, clearPendingLink: () => setPendingLink(null) };
}

/**
 * This installation's stable id: minted once, kept in secure storage. It outlives token rotation,
 * which is exactly what lets the server treat re-registration as an update.
 */
export async function installationId(): Promise<string> {
  const { storage, device } = mobileConfig();

  const stored = await storage.getItem(INSTALLATION_ID_KEY);
  if (stored != null && stored !== "") return stored;

  const minted = (await device?.installationId()) ?? fallbackId();
  await storage.setItem(INSTALLATION_ID_KEY, minted);
  return minted;
}

/**
 * A last-resort id for a device adapter that can't supply one. Uniqueness matters (it keys a row);
 * unguessability does not — this identifies an installation to its own account, nothing more.
 */
function fallbackId(): string {
  return `install-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
}

/**
 * Stop notifying this device. Call on sign-out — leaving a registration behind would keep pushing
 * a signed-out user's notifications at a phone that can no longer open them.
 */
export async function forgetThisDevice(): Promise<void> {
  try {
    await api.notifications.devices.forget(await installationId());
  } catch {
    // Best-effort: an offline sign-out must still sign the user out locally. The server prunes
    // the token anyway the first time the push service reports it as gone.
  }
}
