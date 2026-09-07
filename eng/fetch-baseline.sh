#!/usr/bin/env bash
# =============================================================================
# fetch-baseline.sh — put the last released packages where package validation can find them.
# -----------------------------------------------------------------------------
# Directory.Build.targets turns on .NET SDK package validation for every packable project, with
# PackageValidationBaselineVersion = the last published release. `dotnet pack` then compares each
# package's public surface against that baseline and fails on a removed or changed public member
# unless a CompatibilitySuppressions.xml names it — the deterministic breaking-change detector the
# fleet used to do by hand (docs/TESTING_CONTRACT.md §4.1, #193).
#
# The baseline has to be restorable. Plenipo's primary package channel is the GitHub Release assets
# (anonymously downloadable, no feed credentials), so this script downloads that release's nupkgs
# into artifacts/baseline-feed, which the targets add as a restore source when the folder exists.
# Validation is skipped wherever the folder is absent, so a plain clone still builds; CI runs this
# before restore so every pull request is validated.
#
#   eng/fetch-baseline.sh            # version read from Directory.Build.targets
#   eng/fetch-baseline.sh 0.1.0-alpha.28
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FEED="$ROOT/artifacts/baseline-feed"

VERSION="${1:-$(sed -n 's/.*<PackageValidationBaselineVersion>\(.*\)<\/PackageValidationBaselineVersion>.*/\1/p' "$ROOT/Directory.Build.targets" | head -1)}"
if [ -z "$VERSION" ]; then
  echo "No PackageValidationBaselineVersion in Directory.Build.targets and none given." >&2
  exit 1
fi

if ls "$FEED"/Plenipo.Core."$VERSION".nupkg >/dev/null 2>&1; then
  echo "==> Baseline $VERSION already in $FEED"
  exit 0
fi

mkdir -p "$FEED"
echo "==> Downloading the v$VERSION release packages into $FEED"
if command -v gh >/dev/null 2>&1; then
  gh release download "v$VERSION" -R abrahamFerga/Plenipo -p "*.nupkg" -D "$FEED" --clobber
else
  # No gh: the assets are public, so plain curl over the release's asset list works too.
  api="https://api.github.com/repos/abrahamFerga/Plenipo/releases/tags/v$VERSION"
  curl -fsSL "$api" | grep -o '"browser_download_url": *"[^"]*\.nupkg"' | sed 's/.*"\(https[^"]*\)"/\1/' | while read -r url; do
    curl -fsSL -o "$FEED/$(basename "$url")" "$url"
  done
fi
echo "    $(ls "$FEED"/*.nupkg | wc -l) packages"
