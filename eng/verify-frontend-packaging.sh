#!/usr/bin/env bash
# =============================================================================
# verify-frontend-packaging.sh — prove @plenipo/ui is consumable as an npm package.
# -----------------------------------------------------------------------------
# The backend has eng/verify-packaging.sh (pack + build a consuming module). This is
# the frontend mirror: it `npm pack`s @plenipo/ui and then a throwaway TypeScript app
# installs that tarball and type-checks an import of the public API — so a broken
# build, missing bundle, or (the phase-32 gap) missing .d.ts fails CI instead of
# silently shipping an unusable package.
#
# Run locally (from anywhere): eng/verify-frontend-packaging.sh
# Works on Linux/macOS and on Windows via Git Bash.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
UI="$ROOT/frontend/plenipo-ui"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

echo "==> Packing @plenipo/ui (prepack rebuilds it first)"
( cd "$UI" && npm pack --pack-destination "$WORK" >/dev/null )
# npm pack names a scoped package <scope>-<name>-<version>.tgz; WORK holds only this one tarball.
TGZ="$(ls "$WORK"/*.tgz)"
echo "    packed: $(basename "$TGZ")"

CONS="$WORK/consumer"
mkdir -p "$CONS/src"
# Install the tarball by a relative path (avoids Windows path quirks in package.json).
cp "$TGZ" "$CONS/plenipo-ui.tgz"

cat > "$CONS/package.json" <<'EOF'
{
  "name": "plenipo-ui-consumer",
  "private": true,
  "type": "module",
  "dependencies": {
    "@plenipo/ui": "file:plenipo-ui.tgz",
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
  type PlenipoModuleUi,
  type ModuleTabProps,
  type PlenipoBranding,
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

export function App() {
  void [AppShell, ChatPanel, api, createAgentConnection, ApiError, useActiveModule, sampleModule, currentUser];
  return <PlenipoApp moduleUi={[finance]} branding={branding} />;
}
EOF

echo "==> Installing the consumer (pulls @plenipo/ui from the tarball + its peers)"
( cd "$CONS" && npm install --no-audit --no-fund --loglevel=error )

echo "==> Type-checking the consumer against @plenipo/ui's shipped declarations"
( cd "$CONS" && npx --no-install tsc --noEmit )

echo ""
echo "OK — @plenipo/ui packs and a fresh TypeScript app consumes it with full types."
