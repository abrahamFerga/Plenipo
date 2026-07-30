#!/usr/bin/env bash
# =============================================================================
# verify-frontend-packaging.sh — prove the frontend packages are consumable from npm.
# -----------------------------------------------------------------------------
# The backend has eng/verify-packaging.sh (pack + build a consuming module). This is
# the frontend mirror: it packs @plenipo/client and @plenipo/ui and then a throwaway
# TypeScript app installs both tarballs and type-checks an import of the public API —
# so a broken build, missing bundle, or (the phase-32 gap) missing .d.ts fails CI
# instead of silently shipping an unusable package.
#
# Both are packed with `pnpm pack`, not `npm pack`, for two reasons the published
# artifacts depend on: pnpm rewrites @plenipo/ui's `workspace:*` dependency on
# @plenipo/client to a concrete version, and it applies @plenipo/client's
# publishConfig, which swaps its entry points from TypeScript source (what the
# workspace resolves) to the compiled dist (what consumers get). npm would ship the
# workspace protocol verbatim and point consumers at .ts files.
#
# Run locally (from anywhere): eng/verify-frontend-packaging.sh
# Works on Linux/macOS and on Windows via Git Bash.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UI="$ROOT/frontend/plenipo-ui"
CLIENT="$ROOT/frontend/plenipo-client"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

CONS="$WORK/consumer"
mkdir -p "$CONS/src"

# Pack into per-package directories so each glob matches exactly one tarball.
for pkg in client ui; do
  case "$pkg" in
    client) DIR="$CLIENT" ;;
    ui)     DIR="$UI" ;;
  esac
  echo "==> Packing @plenipo/$pkg (prepack rebuilds it first)"
  mkdir -p "$WORK/$pkg"
  ( cd "$DIR" && pnpm pack --pack-destination "$WORK/$pkg" >/dev/null )
  TGZ="$(ls "$WORK/$pkg"/*.tgz)"
  echo "    packed: $(basename "$TGZ")"
  # Copy in by a relative path (avoids Windows path quirks in package.json).
  cp "$TGZ" "$CONS/plenipo-$pkg.tgz"
done

# @plenipo/client is a direct dependency here, not just a transitive one, because that is how a
# non-React consumer takes it — the mobile shell installs the client and never touches @plenipo/ui.
# Declaring it also pins the ui's own dependency on it to this tarball rather than the registry.
cat > "$CONS/package.json" <<'EOF'
{
  "name": "plenipo-ui-consumer",
  "private": true,
  "type": "module",
  "dependencies": {
    "@plenipo/ui": "file:plenipo-ui.tgz",
    "@plenipo/client": "file:plenipo-client.tgz",
    "react": "^18.3.1",
    "react-dom": "^18.3.1",
    "react-router-dom": "^6.27.0",
    "@tanstack/react-query": "^5.59.16",
    "@microsoft/signalr": "^8.0.7"
  },
  "devDependencies": {
    "typescript": "^5.6.3",
    "@types/react": "^18.3.12",
    "@types/react-dom": "^18.3.1"
  }
}
EOF

cat > "$CONS/tsconfig.json" <<'EOF'
{
  "compilerOptions": {
    "target": "ES2020",
    "lib": ["ES2020", "DOM", "DOM.Iterable"],
    "module": "ESNext",
    "moduleResolution": "Bundler",
    "jsx": "react-jsx",
    "strict": true,
    "skipLibCheck": true,
    "noEmit": true,
    "esModuleInterop": true
  },
  "include": ["src"]
}
EOF

# Reproduce the composition the README documents — the batteries-included PlenipoApp with a host module
# registered via defineModule and a branding prop — plus references to the rest of the public surface
# (registry, RBAC primitives, typed errors, hooks, types). If the package shipped no types, or any of
# these weren't exported with correct types, this fails to compile.
cat > "$CONS/src/consume.tsx" <<'EOF'
import {
  PlenipoApp,
  AppShell,
  ChatPanel,
  defineModule,
  PermissionGate,
  usePermission,
  useActiveModule,
  ApiError,
  api,
  createAgentConnection,
  // Authentication. A product must be able to sign someone in WITHOUT forking this package, so the
  // whole seam has to be reachable from the published entry point — including `configureClient`,
  // which must come from here rather than from "@plenipo/client": importing it from the sibling
  // package configures a DIFFERENT module instance than the one these components read, which is
  // exactly why overriding it from outside never reached the shipped shell.
  configureClient,
  configurePlenipoWeb,
  createOidcAuth,
  devAuthOnly,
  initPlenipoWebAuth,
  isSignInCallback,
  plenipoWebAuth,
  signOutPlenipoWeb,
  type AuthAdapter,
  type SecureStorageAdapter,
  type AuthConfig,
  type PlenipoModuleUi,
  type ModuleTabProps,
  type PlenipoBranding,
  type AppShellProps,
  type Module,
  type Me,
} from "@plenipo/ui";

// A host registers its own React page for a module tab; unregistered tabs fall back to the generic view.
function TransactionsBoard({ moduleId, tab }: ModuleTabProps) {
  const canRecord = usePermission("tools.finance.record_transaction");
  return (
    <PermissionGate permission="chat.use">
      <div>{moduleId}/{tab.id}{canRecord ? " ✓" : ""}</div>
    </PermissionGate>
  );
}

const finance: PlenipoModuleUi = defineModule("finance", { tabs: { transactions: TransactionsBoard } });
const branding: PlenipoBranding = { name: "Acme Ops" };
const sampleModule: Module = { id: "demo", displayName: "Demo", tabs: [] };
const currentUser: Me | null = null;

// A product with its own identity stack implements the adapter directly; one with a plain OIDC
// provider takes the shipped browser adapter. Both must type-check from the TARBALL.
const byoAuth: AuthAdapter = { getAccessToken: async () => "token" };
const store: SecureStorageAdapter = {
  getItem: async () => null,
  setItem: async () => {},
  removeItem: async () => {},
};
const shellProps: AppShellProps = { branding };

export async function signInFlow() {
  const config: AuthConfig = { mode: "oidc", authority: "https://idp.example.com", clientId: "spa" };
  const oidc = createOidcAuth({ authority: config.authority!, clientId: config.clientId! });
  configurePlenipoWeb({ apiBase: "https://api.example.com", auth: oidc, storage: store, mode: config.mode });
  configureClient({ baseUrl: "https://api.example.com" });
  void [isSignInCallback(), plenipoWebAuth(), devAuthOnly(), shellProps];
  await initPlenipoWebAuth({ apiBase: "https://api.example.com" });
  await signOutPlenipoWeb();
}

export function App() {
  void [AppShell, ChatPanel, api, createAgentConnection, ApiError, useActiveModule, sampleModule, currentUser];
  // The one-liner a product writes to let someone log in.
  return <PlenipoApp moduleUi={[finance]} branding={branding} config={{ auth: byoAuth }} />;
}
EOF

# The renderer-free package on its own — the shape a non-React consumer (the mobile shell, a CLI,
# a script) installs. Also asserts the two packages agree on the manifest contract: a `Module` from
# @plenipo/client must be assignable to a `Module` from @plenipo/ui. If the ui ever stopped
# re-exporting the shared types and grew its own copy, that assignment is where it would show up.
cat > "$CONS/src/consume-client.ts" <<'EOF'
import {
  configureClient,
  api,
  hasPermission,
  resolveFieldDefault,
  runAgui,
  ApiError,
  type Module as ClientModule,
  type ModuleTab,
  type TabEditorField,
} from "@plenipo/client";
import type { Module as UiModule } from "@plenipo/ui";

configureClient({
  baseUrl: "https://api.example.com",
  authHeaders: async () => ({ Authorization: "Bearer token" }),
});

const tab: ModuleTab = { id: "cases", label: "Cases", route: "/legal/cases" };
const shared: ClientModule = { id: "legal", displayName: "Legal", tabs: [tab] };

// One contract, two renderers: this must compile without a cast.
const sameShape: UiModule = shared;

const field: TabEditorField = { field: "currencyCode", label: "Currency", defaultFrom: "browser-currency" };

export async function probe() {
  void [api, runAgui, ApiError, sameShape];
  void hasPermission(["tools.legal.*"], "tools.legal.draft_clause");
  void resolveFieldDefault(field);
}
EOF

echo "==> Installing the consumer (pulls both tarballs + the ui's peers)"
( cd "$CONS" && npm install --no-audit --no-fund --loglevel=error )

echo "==> Type-checking the consumer against @plenipo/ui's shipped declarations"
( cd "$CONS" && npx --no-install tsc --noEmit )

echo ""
echo "OK — @plenipo/client and @plenipo/ui pack, and a fresh TypeScript app consumes both with full types."
