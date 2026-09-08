#!/usr/bin/env bash
# =============================================================================
# browser-e2e.sh — the served shell, in a real browser, against the real sample host (#218).
# -----------------------------------------------------------------------------
# The Playwright specs under frontend/plenipo-ui/e2e mock the platform API, so they prove the shell's
# own logic and nothing about the wire between shell and host. This script proves the wire: it
# builds the app shell, puts it where the sample host serves it from (wwwroot/app, the same path a
# product uses), starts the host on a throwaway Postgres with the Mock provider, and runs
# frontend/plenipo-ui/e2e-host in Chromium against it — a chat turn, an approval-gated write, the
# release, the data tab.
#
#   eng/browser-e2e.sh                                   # local: throwaway Postgres container on 5498
#   PLENIPO_PG="Host=…;Port=…;Username=…;Password=…" eng/browser-e2e.sh   # CI: a service container
#
# Knobs: PLENIPO_HOST_PORT (default 8080), PLENIPO_HOST_ARGS (extra host arguments, e.g.
# "--Ai:Provider=None" to watch the spec go red when the chat cannot work), PLENIPO_SKIP_BUILD=1
# to reuse the last shell and host builds. Docker (locally), .NET 10 and pnpm are required;
# `pnpm -C frontend install` and `pnpm --filter @plenipo/ui exec playwright install chromium` first.
# Works on Linux and on Windows via Git Bash.
# =============================================================================
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PORT="${PLENIPO_HOST_PORT:-8080}"
URL="http://127.0.0.1:$PORT"
BIN="$ROOT/samples/Plenipo.Sample.Host/bin/Debug/net10.0"
LOG_DIR="$ROOT/artifacts"
mkdir -p "$LOG_DIR"
HOST_LOG="$LOG_DIR/browser-e2e-host.log"

HOST_PID=""
PG_CONTAINER=""
cleanup() {
  if [ -n "$HOST_PID" ] && kill -0 "$HOST_PID" 2>/dev/null; then
    kill "$HOST_PID" 2>/dev/null || true
    wait "$HOST_PID" 2>/dev/null || true
  fi
  if [ -n "$PG_CONTAINER" ]; then
    docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# 0. A database: the caller's (CI service container) or a throwaway one.
PG="${PLENIPO_PG:-}"
if [ -z "$PG" ]; then
  PG_CONTAINER="plenipo-pg-e2e-$PORT"
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$PG_CONTAINER" -e POSTGRES_PASSWORD=postgres -p 5498:5432 pgvector/pgvector:pg16 >/dev/null
  PG="Host=127.0.0.1;Port=5498;Username=postgres;Password=postgres"
  echo "==> Waiting for the throwaway Postgres"
  for _ in $(seq 1 60); do
    docker exec "$PG_CONTAINER" pg_isready -U postgres >/dev/null 2>&1 && break
    sleep 1
  done
fi

# 1. The shell and the host, built the way they ship.
if [ "${PLENIPO_SKIP_BUILD:-0}" != "1" ]; then
  echo "==> Building the app shell (@plenipo/ui build:app, same-origin API)"
  ( cd "$ROOT/frontend/plenipo-ui" && VITE_API_BASE="" pnpm run build:app >/dev/null )
  echo "==> Building the sample host"
  dotnet build "$ROOT/samples/Plenipo.Sample.Host" --nologo -v q
fi
rm -rf "$BIN/wwwroot/app"
mkdir -p "$BIN/wwwroot"
cp -r "$ROOT/frontend/plenipo-ui/dist-app" "$BIN/wwwroot/app"

# 2. The host, from its build output so ASP.NET's ContentRoot finds appsettings.Development.json
#    (the Mock provider) and wwwroot/app (the shell) — the same gotcha .claude/skills/run-plenipo names.
echo "==> Starting the sample host on $URL (Mock provider)"
# shellcheck disable=SC2086
( cd "$BIN" && ASPNETCORE_ENVIRONMENT=Development exec dotnet Plenipo.Sample.Host.dll \
    "--ConnectionStrings:plenipo-platform=$PG;Database=plenipo_platform" \
    "--ConnectionStrings:plenipo-audit=$PG;Database=plenipo_audit" \
    "--urls=$URL" ${PLENIPO_HOST_ARGS:-} ) > "$HOST_LOG" 2>&1 &
HOST_PID=$!
for _ in $(seq 1 90); do
  if curl -fs "$URL/alive" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$HOST_PID" 2>/dev/null; then
    echo "The sample host exited before becoming ready; last lines of $HOST_LOG:" >&2
    tail -n 40 "$HOST_LOG" >&2
    exit 1
  fi
  sleep 2
done
curl -fs "$URL/alive" >/dev/null || { echo "The sample host never answered $URL/alive; see $HOST_LOG" >&2; tail -n 40 "$HOST_LOG" >&2; exit 1; }
echo "    ready; the served shell is at $URL/"

# 3. The spec, in Chromium, against the host.
echo "==> Playwright: frontend/plenipo-ui/e2e-host against $URL"
( cd "$ROOT/frontend/plenipo-ui" && PLENIPO_HOST_URL="$URL" pnpm exec playwright test -c playwright.host.config.ts )
echo "OK — the served shell chats through the Mock, parks and releases an approval, and shows the row, in a real browser against the real host."
