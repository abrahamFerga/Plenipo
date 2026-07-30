import { Platform } from "react-native";
import * as Application from "expo-application";
import * as Device from "expo-device";
import * as Localization from "expo-localization";
import * as Notifications from "expo-notifications";
import * as SecureStore from "expo-secure-store";
import { fetch as expoFetch } from "expo/fetch";
import { currencyForLocale, type FetchLike } from "@plenipo/client";
import type {
  DeviceAdapter,
  LocaleAdapter,
  MobilePlatform,
  PlenipoAdapters,
  PushAdapter,
  SecureStorageAdapter,
} from "../adapters";

/**
 * The standard Expo implementations of the shell's adapters.
 *
 * This is the ONLY module in the package that imports a native module, which is what keeps the
 * core testable in plain Node and lets a product replace any one of these without forking. Import
 * it from `@plenipo/mobile/expo` and spread it into the config:
 *
 * ```tsx
 * <PlenipoMobileApp config={{ apiBase: "…", ...expoAdapters() }} />
 * ```
 */
export function expoAdapters(options: { push?: boolean } = {}): PlenipoAdapters {
  return {
    storage: secureStorage(),
    // No IdP by default: requests fall back to the platform's dev auth, so a new app talks to a
    // local host with nothing configured. A product passes its own auth adapter to override this.
    auth: { getAccessToken: () => Promise.resolve(null) },
    device: expoDevice(),
    ...(options.push === false ? {} : { push: expoPush() }),
    locale: expoLocale(),
    // React Native's built-in fetch buffers the whole response, which would turn the AG-UI chat
    // stream into a single silent wait followed by the full answer. expo/fetch streams.
    fetch: expoFetch as unknown as FetchLike,
  };
}

/** The OS keystore — Keychain on iOS, EncryptedSharedPreferences on Android. */
export function secureStorage(): SecureStorageAdapter {
  return {
    getItem: (key) => SecureStore.getItemAsync(key),
    setItem: (key, value) => SecureStore.setItemAsync(key, value),
    removeItem: (key) => SecureStore.deleteItemAsync(key),
  };
}

export function expoDevice(): DeviceAdapter {
  return {
    /**
     * A per-installation id from the OS where one exists: Android's `getAndroidId`, iOS's
     * `identifierForVendor`. Both reset on uninstall, which is the right lifetime — a fresh
     * install genuinely is a new device to notify. Null falls back to a minted id.
     */
    installationId: async () => {
      const native =
        Platform.OS === "android"
          ? Application.getAndroidId()
          : await Application.getIosIdForVendorAsync();
      return native ?? `install-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
    },
    deviceName: () => Device.deviceName ?? Device.modelName ?? null,
    platform: (): MobilePlatform =>
      Platform.OS === "ios" ? "Ios" : Platform.OS === "android" ? "Android" : "Web",
  };
}

export function expoLocale(): LocaleAdapter {
  return {
    timeZone: () => Localization.getCalendars()[0]?.timeZone ?? undefined,
    /**
     * The device's own currency when the OS reports one; otherwise a guess from the locale's
     * region, using the same map the web shell uses. Either way it's a starting point the user can
     * change, and the shell drops it if the field's vocabulary doesn't include it.
     */
    currency: () => {
      const locale = Localization.getLocales()[0];
      return locale?.currencyCode ?? currencyForLocale(locale?.languageTag) ?? undefined;
    },
  };
}

export function expoPush(): PushAdapter {
  return {
    getPushToken: async () => {
      // A simulator can't be issued a token. Asking anyway would prompt for a permission that
      // cannot be used, so don't.
      if (!Device.isDevice) return null;

      const existing = await Notifications.getPermissionsAsync();
      const granted =
        existing.granted || (await Notifications.requestPermissionsAsync()).granted;
      if (!granted) return null;

      const token = await Notifications.getExpoPushTokenAsync();
      return token.data;
    },

    onNotificationTapped: (handler) => {
      const subscription = Notifications.addNotificationResponseReceivedListener((response) => {
        // The platform's push channel puts the notification's app-relative link in `data.link`;
        // the shell resolves it against the module manifest's tab routes.
        const link = response.notification.request.content.data?.link;
        handler(typeof link === "string" ? link : null);
      });
      return () => subscription.remove();
    },
  };
}
