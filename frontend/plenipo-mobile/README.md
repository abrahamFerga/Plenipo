# @plenipo/mobile

A **server-driven** React Native shell for the Plenipo platform. It hardcodes no domain routes, no
screens, and no vocabulary: it reads `GET /api/platform/modules` — already filtered by the caller's
permissions — and builds the tab bar, the tables, the forms, the charts, and the chat from it.

So a product's mobile app is a config object and a brand. Add a tab to a C# `ModuleManifest` and it
appears on phones that already have the build; shipping domain capability is a backend deploy, not
an App Store review.

The architecture, the push design, and the reasoning live in [docs/MOBILE.md](../../docs/MOBILE.md).

## Stack

- Expo SDK 57 / React Native 0.86 / React 19
- `@react-navigation` (native-stack + bottom-tabs) — navigation is generated, so it has to be imperative
- `@tanstack/react-query` for data, as on the web
- `react-native-svg` for charts
- `@plenipo/client` for the manifest contract, REST, and the AG-UI chat transport
- Plain `StyleSheet` + a token object — no Tailwind, no extra build step

## Use it

```tsx
import { PlenipoMobileApp } from "@plenipo/mobile";
import { expoAdapters } from "@plenipo/mobile/expo";

export default function App() {
  return (
    <PlenipoMobileApp
      config={{ apiBase: "https://api.acme.com", ...expoAdapters() }}
      branding={{ name: "Acme Ops" }}
    />
  );
}
```

That is a complete app. See [`frontend/mobile-app`](../mobile-app) for the runnable version.

## Theming

React Native has no cascade, so the web shell's CSS variables become a token object. Setting the
brand is the common case and doesn't require restating anything else:

```tsx
<PlenipoMobileApp theme={{ both: { brand: "#2a78d6" } }} … />
```

`light` and `dark` override per scheme; `both` applies to each. Every token is in `theme.ts`.

## Custom screens

The manifest stays the source of truth for **which** tabs exist and **who** can see them. A product
owns only **how** one renders:

```tsx
import { defineModule, type ModuleTabProps, Card, Button } from "@plenipo/mobile";

function MattersBoard({ moduleId, tab }: ModuleTabProps) {
  return <Card>{/* … */}</Card>;
}

<PlenipoMobileApp moduleUi={[defineModule("legal", { tabs: { matters: MattersBoard } })]} … />;
```

Unregistered tabs fall back to the generic renderer, so a module that needs no custom mobile UI
costs zero React Native code. The package exports its primitives (`Button`, `Card`, `Sheet`,
`ConfirmDialog`, `FieldInput`, `EmptyState`, …) and hooks (`useModules`, `useMe`, `usePermission`)
so a registered screen looks like the rest of the shell rather than near it.

## Adapters

The core imports no native module — storage, auth, device identity, push, locale, and the streaming
`fetch` all arrive as injected adapters. `@plenipo/mobile/expo` implements them on the standard
Expo modules; a product can replace any single one:

```tsx
config={{
  apiBase: "…",
  ...expoAdapters(),
  auth: { getAccessToken: () => myIdp.getToken() },   // real OIDC
}}
```

Returning `null` from `getAccessToken` is supported: the shell falls back to the platform's
Development-only dev auth, which is what makes a new app work against a local host with nothing
configured.

`expoAdapters({ push: false })` opts out of notifications entirely — the OS is then never asked for
a permission the app won't use.

## Scripts

```bash
pnpm test        # jest-expo + @testing-library/react-native, against a faked API
pnpm typecheck   # tsc --noEmit
pnpm lint        # eslint
```

The tests render the real components against a fake `@plenipo/client` transport
(`src/test-support.ts`), so what they cover is the mapping from manifest to UI — not the network,
not the backend, and not a device.

## Packaging

This package ships **TypeScript source**, not a build. Metro runs every dependency through
`babel-preset-expo`, which handles TS and JSX, so there is no build step, no `dist/` to go stale,
and no dual-package hazard. The cost is that it targets an Expo-flavoured Metro — which is the
stated target anyway.
