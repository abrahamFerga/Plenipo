# Cortex UI

A minimal, **server-driven** React frontend for the Cortex .NET platform. The
dashboard hardcodes no domain routes: it fetches a module manifest from the API
and builds the sidebar tabs and routes dynamically. A module switcher in the top
bar lets the user change the active module.

## Stack

- Vite + React 18 + TypeScript
- Tailwind CSS v3
- TanStack Query (`@tanstack/react-query`) for data fetching
- `@microsoft/signalr` for the chat stream
- React Router v6 for routing
- ESLint (typescript-eslint) — `pnpm lint` passes on the project code

## Getting started

```powershell
# The frontend is a pnpm workspace; `pnpm install` here installs the whole workspace.
corepack enable   # puts pnpm on PATH (ships with Node)
pnpm install

# copy the example env and adjust if your API is not on localhost:8080
Copy-Item .env.example .env

pnpm dev          # dev server (Vite, http://localhost:5173)
pnpm build        # production build (tsc -b + vite build)
pnpm lint         # lint
pnpm test         # unit + component tests (vitest)
pnpm test:e2e     # real-browser E2E (Playwright; run `pnpm exec playwright install chromium` once — API is mocked, no backend needed)
```

## Configuration

- `VITE_API_BASE` (default `http://localhost:8080`) — base URL of the Cortex API.
  See `.env.example`.

## Theming / branding

The domain UI's accent color is driven by CSS variables (`--cortex-brand-50` … `--cortex-brand-900`, as
space-separated RGB channels), defaulting to Tailwind's indigo. Rebrand the whole shell by overriding them
in your own CSS — no component or config changes:

```css
:root {
  --cortex-brand-600: 220 38 38; /* primary buttons, active nav */
  --cortex-brand-500: 239 68 68; /* hovers, focus rings */
}
```

A host app that runs its own Tailwind includes the shipped preset (so the `brand-*` utilities resolve) and
the default variables:

```js
// tailwind.config.js
import cortexPreset from "@cortex/ui/tailwind-preset";
export default {
  presets: [cortexPreset],
  content: ["./src/**/*.{ts,tsx}", "./node_modules/@cortex/ui/dist/**/*.js"],
};
```

```ts
// app entry — the indigo defaults; override any --cortex-brand-* afterwards to rebrand
import "@cortex/ui/theme.css";
```

The product **name and logo** in the top bar are content (not CSS), set via the `branding` prop:

```tsx
<CortexApp
  branding={{ name: "Acme Ops", logo: <img src="/acme.svg" alt="Acme" className="h-7" /> }}
  moduleUi={[finance]}
/>
```

## Dev auth

Until real OIDC is wired up, every API request and the SignalR connection send
dev-auth headers, defined in `src/lib/devAuth.ts`:

- `X-Dev-Subject: dev-user`
- `X-Dev-Tenant: dev`
- `X-Dev-Roles: system_admin`
- `X-Dev-Name: Dev User`

(For the WebSocket handshake, which cannot set custom headers, the same values
are also forwarded as query-string parameters.)

## How it works

- `src/hooks/useModules.ts` fetches `GET /api/platform/modules`. `AppShell`
  turns the active module's `tabs[]` into both the sidebar nav links and the
  React Router `<Route>`s — a "Chat" tab is always injected first. Below
  Tailwind's `md` the same tabs render as a fixed bottom bar (`BottomNav`):
  the first four destinations plus a More button that opens the drawer with
  the full list (all five and no More when everything fits). Host pages should
  set `viewport-fit=cover` so the bar's safe-area padding works on iOS.
- `src/lib/activeModule.ts` holds the active-module context shared by the top-bar
  switcher and the sidebar.
- `ChatPanel` opens a SignalR connection to `/hubs/agent` and calls
  `connection.stream("Stream", { moduleId, conversationId?, message })`,
  accumulating `Token` events into the assistant message, rendering
  `ToolInvoked` chips, capturing `conversationId` on `Completed`, and surfacing
  `Error` events.
- Each non-chat tab renders through `ModuleTabView`: a host-registered React
  component if one exists for that `(moduleId, tabId)`, otherwise the built-in
  server-driven `GenericTab` (a table from the tab's `dataEndpoint`, or a
  placeholder). The base library special-cases no vertical.

## Registering custom module UIs

The API manifest (`GET /api/platform/modules`) stays the source of truth for
**which** modules/tabs exist and **who** can see them (nav + RBAC). A host app
owns **how** a tab renders: register your own React components per tab id with
`defineModule`, and pass them to `CortexApp` (or `AppShell`). A tab you don't
register falls back to `GenericTab` — so simple modules cost zero React.

```tsx
import { createRoot } from "react-dom/client";
import { CortexApp, defineModule, type ModuleTabProps } from "@cortex/ui";
import { TransactionsBoard, BudgetPlanner } from "./finance";

// Components receive { moduleId, tab } and can use everything @cortex/ui exports
// (api, useMe, hasPermission, …) plus your own domain APIs.
const finance = defineModule("finance", {
  tabs: {
    transactions: TransactionsBoard,
    budgets: BudgetPlanner,
  },
});

createRoot(document.getElementById("root")!).render(
  <CortexApp moduleUi={[finance]} />,
);
```

Registered components are first-class plugins: each tab is wrapped in an error
boundary (a crash shows a contained error card, not a white screen) and a
Suspense boundary, so a host can `React.lazy(() => import("./HeavyBoard"))` a tab
for code-splitting and it just works. Inside a component you get the platform's
RBAC primitives — gate UI the same way the server does (which still enforces it):

```tsx
import { PermissionGate, usePermission } from "@cortex/ui";

const canRecord = usePermission("tools.finance.record_transaction");
// …or declaratively:
<PermissionGate permission="tools.finance.record_transaction">
  <RecordButton />
</PermissionGate>;
```

Hosts that already own their router and React Query client can compose the shell
directly instead: `<AppShell moduleUi={[finance]} />`.
