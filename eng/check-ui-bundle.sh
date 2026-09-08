#!/usr/bin/env bash
# =============================================================================
# check-ui-bundle.sh — what must be true INSIDE a packed @plenipo/ui before it ships.
# -----------------------------------------------------------------------------
# 0.1.0-alpha.29 shipped with React 19's JSX runtime compiled into dist/plenipo-ui.es.js (#213):
# the library build externalised the bare "react" but not "react/jsx-runtime", so the bundle carried
# a copy of whichever React the platform built with, read the host's React internals through it,
# and crashed every React 18 host at the first component. The type-check in
# verify-frontend-packaging.sh could not see it; only a consumer's tests did. This is the guard
# that looks inside the bundle (#219). Three assertions:
#
#   1. The JSX runtime is imported from the host ("react/jsx-runtime"), never compiled in.
#   2. No React internals are inlined.
#   3. No package is declared as both a dependency and a peer — that is how a second React
#      copy sneaks into a host that resolves the dependency instead of the peer.
#
#   eng/check-ui-bundle.sh <dir with package.json and dist/>   # an extracted tarball, or frontend/plenipo-ui
#
# Exit 1 with a ::error annotation per failure; verify-frontend-packaging.sh runs it on the packed
# tarball, so CI's packaging step covers it without a workflow edit.
# =============================================================================
set -euo pipefail
PKG="${1:?usage: check-ui-bundle.sh <package dir>}"
ES="$PKG/dist/plenipo-ui.es.js"
MANIFEST="$PKG/package.json"
[ -f "$ES" ] || { echo "No $ES — build or pack the library first." >&2; exit 1; }
[ -f "$MANIFEST" ] || { echo "No $MANIFEST." >&2; exit 1; }

fail=0

# 1. The host's React supplies the JSX runtime.
if grep -q -E 'from ?"react/jsx-runtime"' "$ES"; then
  echo "ok   react/jsx-runtime is imported from the host"
else
  echo "::error title=@plenipo/ui compiles the JSX runtime in::dist/plenipo-ui.es.js never imports react/jsx-runtime, so the compiled JSX carries a copy of the platform's React runtime (#213). Externalise ^react(/.*)?$ and ^react-dom(/.*)?$ in frontend/plenipo-ui/vite.config.ts."
  fail=1
fi

# 2. No React internals inlined.
internals='recentlyCreatedOwnerStacks|__CLIENT_INTERNALS_DO_NOT_USE|__SECRET_INTERNALS_DO_NOT_USE|react-jsx-runtime\.(production|development)|react-jsx-dev-runtime'
if grep -q -E "$internals" "$ES"; then
  echo "::error title=@plenipo/ui inlines React internals::dist/plenipo-ui.es.js contains $(grep -o -E "$internals" "$ES" | sort -u | tr '\n' ' ')— React code that belongs to the host's copy, not the library (#213)."
  fail=1
else
  echo "ok   no React internals inlined"
fi

# 3. dependencies ∩ peerDependencies = ∅.
overlap="$(node -e '
  const p = require(require("path").resolve(process.argv[1]));
  const both = Object.keys(p.dependencies || {}).filter(k => p.peerDependencies && p.peerDependencies[k]);
  process.stdout.write(both.join(", "));
' "$MANIFEST")"
if [ -n "$overlap" ]; then
  echo "::error title=@plenipo/ui declares a peer as a dependency too::$overlap — declared in both dependencies and peerDependencies. A host that resolves the dependency gets a second copy (two Reacts, two routers); declare peers once and put the workspace's own copy in devDependencies."
  fail=1
else
  echo "ok   no package is both a dependency and a peer"
fi

exit $fail
