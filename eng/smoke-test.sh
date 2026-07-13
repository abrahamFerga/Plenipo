#!/usr/bin/env bash
# =============================================================================
# smoke-test.sh — prove a RUNNING Plenipo instance actually works, end-to-end.
# -----------------------------------------------------------------------------
# `dotnet test` verifies throwaway instances it spins up itself. This verifies
# YOUR running instance — the exact thing a newcomer wants to confirm after
# following GETTING_STARTED, and a useful post-deploy smoke against a live URL.
#
# It is the executable form of the "See the security pipeline" tour: modules
# load, the demo ledger is seeded, the agent makes a real audited tool call, and
# a side-effecting tool is held for human approval — all via the zero-key Mock
# provider, so no AI key is required.
#
#   Start the demo:  docker compose up -d && dotnet run --project samples/Plenipo.Sample.Host
#   Then:            eng/smoke-test.sh                 # defaults to http://localhost:8080
#                    eng/smoke-test.sh https://my-deployment   # or any running instance
#
# Works on Linux/macOS and on Windows via Git Bash. Needs only curl.
# Exits non-zero if any check fails (so it can gate a deploy).
# =============================================================================
set -uo pipefail  # deliberately NOT -e: run every check and report them all

BASE="${1:-http://localhost:8080}"
FAILED=0
pass() { printf '  PASS  %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; FAILED=1; }

get()  { curl -fsS --max-time 30 "$BASE$1"; }
post() { curl -fsS --max-time 30 -X POST -H 'Content-Type: application/json' -d "$2" "$BASE$1"; }

echo "==> Smoke-testing Plenipo at $BASE"

# 1. Liveness — the host is up.
code="$(curl -fsS -o /dev/null -w '%{http_code}' --max-time 10 "$BASE/alive" 2>/dev/null || true)"
if [ "$code" = "200" ]; then pass "/alive responds 200"; else
  fail "/alive did not respond 200 (got '${code:-no response}') — is the host running at $BASE?"
  echo ""; echo "FAILED — the host is unreachable; later checks skipped."
  echo "Start it with: docker compose up -d && dotnet run --project samples/Plenipo.Sample.Host"
  exit 1
fi

# 2. The three demo modules are discovered (drives the dashboard navigation).
modules="$(get /api/platform/modules || true)"
for m in finance nutrition legal; do
  if printf '%s' "$modules" | grep -q "\"$m\""; then pass "module '$m' is loaded"
  else fail "module '$m' missing from /api/platform/modules"; fi
done

# 3. Finance ships a seeded demo ledger (so its tabs and tools aren't hollow).
txns="$(get /api/finance/transactions | tr -d '[:space:]' || true)"
if [ -n "$txns" ] && [ "$txns" != "[]" ]; then pass "Finance ships a seeded ledger"
else fail "Finance ledger is empty (expected seeded demo transactions)"; fi

# 4. The agent makes a REAL, audited tool call over that ledger (no AI key).
sse="$(post /api/agui/finance '{"messages":[{"role":"user","content":"Summarize my spending using a tool."}]}' || true)"
printf '%s' "$sse" | grep -q 'TOOL_CALL_START' && pass "agent invoked a tool (TOOL_CALL_START)" || fail "no TOOL_CALL_START in the summarize response"
printf '%s' "$sse" | grep -q 'RUN_FINISHED'    && pass "run completed (RUN_FINISHED)"          || fail "no RUN_FINISHED in the summarize response"
printf '%s' "$sse" | grep -q 'RUN_ERROR'       && fail "summarize produced a RUN_ERROR"        || pass "no RUN_ERROR"
printf '%s' "$sse" | grep -q 'Groceries'       && pass "tool returned real ledger data (Groceries)" || fail "no real category data — did the tool run over the seed?"

# 5. A side-effecting tool is BLOCKED pending human approval (the HITL gate).
sse2="$(post /api/agui/finance '{"messages":[{"role":"user","content":"Record a transaction for me."}]}' || true)"
printf '%s' "$sse2" | grep -q 'approval_required' && pass "side-effecting tool held for approval (approval_required)" || fail "record_transaction was not gated for approval"

# 6. The tool call was written to the audit log (who / which tool / permission).
audit="$(get '/api/admin/audit/tool-calls?take=50' || true)"
printf '%s' "$audit" | grep -q 'summarize_spending' && pass "tool call was audited (summarize_spending)" || fail "summarize_spending not found in the audit log"

echo ""
if [ "$FAILED" -eq 0 ]; then
  echo "OK — your Plenipo instance loads all modules, serves seeded data, runs an audited tool call, and enforces the approval gate."
else
  echo "FAILED — see the checks above."
  exit 1
fi
