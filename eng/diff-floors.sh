#!/usr/bin/env bash
# =============================================================================
# diff-floors.sh — which dependency floors did this candidate raise since the last release?
# -----------------------------------------------------------------------------
# A raised floor is a breaking change nobody classifies: when the platform bumped
# Microsoft.Extensions.AI from 10.8.x to 10.9.0, every consumer that referenced it directly at
# the older version failed restore with NU1605 (consumer-conformance run 33437058312), and the
# changelog said nothing. This compares the <dependency> floors declared in each Plenipo.* nuspec
# of a candidate feed against the same package in the baseline feed and prints the raised ones
# — as GitHub annotations under Actions, as plain lines elsewhere. Exit 0 always: it informs the
# reviewer and announce-release; the consumer build is what decides.
#
#   eng/diff-floors.sh <baseline-feed-dir> <candidate-feed-dir>
#   eng/diff-floors.sh artifacts/baseline-feed artifacts/packages
# =============================================================================
set -euo pipefail

BASE="${1:?baseline feed directory}"
CAND="${2:?candidate feed directory}"

deps() {
  # "<package> <dependency> <version>" lines from a nupkg's nuspec, excluding Plenipo.* siblings
  # (their versions move with every pack and are never a consumer's own floor).
  local nupkg="$1" pkg="$2"
  unzip -p "$nupkg" "$pkg.nuspec" 2>/dev/null \
    | grep -o '<dependency id="[^"]*" version="[^"]*"' \
    | sed -E 's/<dependency id="([^"]*)" version="([^"]*)"/\1 \2/' \
    | grep -v '^Plenipo\.' \
    | sort -u
}

version_lt() {
  # $1 < $2 by dotted numeric comparison of the leading numeric parts (prerelease labels ignored).
  local a="${1%%-*}" b="${2%%-*}"
  [ "$(printf '%s\n%s\n' "$a" "$b" | sort -V | head -1)" = "$a" ] && [ "$a" != "$b" ]
}

raised=0
for cand in "$CAND"/Plenipo.*.nupkg; do
  name="$(basename "$cand")"
  pkg="$(printf '%s' "$name" | sed -E 's/^(Plenipo(\.[A-Za-z]+)+)\.[0-9].*$/\1/')"
  base="$(ls "$BASE"/"$pkg".[0-9]*.nupkg 2>/dev/null | head -1 || true)"
  if [ -z "$base" ]; then
    echo "::notice title=New package::$pkg has no baseline — first release of this package"
    continue
  fi

  while read -r dep bver; do
    [ -z "$dep" ] && continue
    cver="$(deps "$cand" "$pkg" | awk -v d="$dep" '$1==d {print $2}')"
    if [ -z "$cver" ]; then
      echo "::notice title=Dependency dropped::$pkg no longer depends on $dep (was >= $bver)"
    elif version_lt "$bver" "$cver"; then
      raised=$((raised+1))
      echo "::warning title=Dependency floor raised::$pkg now requires $dep >= $cver (baseline $bver). A consumer that references $dep directly below $cver fails restore with NU1605 — a breaking change for announce-release to name."
    fi
  done < <(deps "$base" "$pkg")

  while read -r dep cver; do
    [ -z "$dep" ] && continue
    if ! deps "$base" "$pkg" | awk -v d="$dep" '$1==d' | grep -q .; then
      echo "::notice title=New dependency::$pkg now depends on $dep >= $cver"
    fi
  done < <(deps "$cand" "$pkg")
done

echo "Raised floors: $raised"
