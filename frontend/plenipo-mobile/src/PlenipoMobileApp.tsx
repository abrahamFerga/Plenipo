import { useMemo, useState } from "react";
import { StatusBar, useColorScheme } from "react-native";
import { SafeAreaProvider } from "react-native-safe-area-context";
import {
  DarkTheme,
  DefaultTheme,
  NavigationContainer,
  type Theme as NavigationTheme,
} from "@react-navigation/native";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ApiError } from "@plenipo/client";
import { configurePlenipoMobile, type PlenipoMobileConfig } from "./config";
import { BrandingContext, type PlenipoBranding } from "./lib/branding";
import { createModuleUiRegistry, type PlenipoModuleUi } from "./lib/moduleUi";
import { AppShell } from "./navigation/AppShell";
import { useDeviceRegistration } from "./push/useDeviceRegistration";
import { resolveTheme, type PlenipoThemeOverride } from "./theme";

/**
 * The batteries-included root: query client, navigation, theming, push registration, and the
 * manifest-driven shell.
 *
 * A product's entire app is usually this component plus a config object. Everything domain-shaped
 * — which modules exist, what their tabs are, what the assistant can do — arrives from the API at
 * runtime, so shipping a new capability to phones is a backend deploy, not an App Store review.
 *
 * ```tsx
 * import { PlenipoMobileApp } from "@plenipo/mobile";
 * import { expoAdapters } from "@plenipo/mobile/expo";
 *
 * export default function App() {
 *   return (
 *     <PlenipoMobileApp
 *       config={{ apiBase: "https://api.acme.com", ...expoAdapters() }}
 *       branding={{ name: "Acme Ops" }}
 *     />
 *   );
 * }
 * ```
 */
export interface PlenipoMobileAppProps {
  config: PlenipoMobileConfig;
  branding?: PlenipoBranding;
  /** Product-registered native screens, per (moduleId, tabId). Unregistered tabs stay generic. */
  moduleUi?: PlenipoModuleUi[];
  theme?: PlenipoThemeOverride;
}

export function PlenipoMobileApp({ config, branding = {}, moduleUi, theme }: PlenipoMobileAppProps) {
  // Configure before the first render commits, so no child can issue a request against an
  // unconfigured client. useMemo rather than useEffect for exactly that ordering.
  useMemo(() => configurePlenipoMobile(config), [config]);

  const client = useMemo(() => createQueryClient(), []);
  const registry = useMemo(() => createModuleUiRegistry(moduleUi), [moduleUi]);

  return (
    <QueryClientProvider client={client}>
      <BrandingContext.Provider value={branding}>
        <SafeAreaProvider>
          <ThemedShell theme={theme} registry={registry} />
        </SafeAreaProvider>
      </BrandingContext.Provider>
    </QueryClientProvider>
  );
}

/** Split out so push registration and theming sit inside the providers they depend on. */
function ThemedShell({
  theme,
  registry,
}: {
  theme?: PlenipoThemeOverride;
  registry: ReturnType<typeof createModuleUiRegistry>;
}) {
  const scheme = useColorScheme() === "dark" ? "dark" : "light";
  const tokens = resolveTheme(scheme, theme);
  const { pendingLink, clearPendingLink } = useDeviceRegistration();
  const [navTheme] = useState<NavigationTheme>(() => (scheme === "dark" ? DarkTheme : DefaultTheme));

  return (
    <NavigationContainer
      theme={{
        ...navTheme,
        dark: scheme === "dark",
        colors: {
          ...navTheme.colors,
          primary: tokens.brand,
          background: tokens.background,
          card: tokens.surface,
          text: tokens.text,
          border: tokens.border,
        },
      }}
    >
      <StatusBar barStyle={scheme === "dark" ? "light-content" : "dark-content"} />
      <AppShell moduleUi={registry} pendingLink={pendingLink} onLinkHandled={clearPendingLink} />
    </NavigationContainer>
  );
}

/**
 * Query defaults tuned for a phone rather than a desktop tab.
 *
 * The important one is the retry rule: a 401/403 means the session or the permission is the
 * problem, and hammering the endpoint three more times neither fixes it nor tells the user
 * anything. Only genuinely transient failures are worth a retry on a flaky mobile connection.
 */
function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        retry: (failureCount, error) => {
          if (error instanceof ApiError && error.status >= 400 && error.status < 500) return false;
          return failureCount < 2;
        },
        // A phone leaves and re-enters the network constantly; refetching on reconnect is the
        // cheapest way to keep a returning screen honest.
        refetchOnReconnect: true,
        staleTime: 15_000,
      },
      mutations: { retry: false },
    },
  });
}
