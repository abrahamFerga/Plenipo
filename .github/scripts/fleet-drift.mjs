#!/usr/bin/env node
// =============================================================================
// fleet-drift.mjs — where every consumer's platform pin stands against the last release (#220).
// -----------------------------------------------------------------------------
// Casewell sat fifteen releases behind before anyone measured it, and the only signal a lagging
// product gets is the per-release upgrade issue announce-release opens. This reads consumers.json,
// fetches each consumer's pin from its default branch through the GitHub API (Directory.Build.props
// first; a csproj's Plenipo.*/Cortex.* PackageReference when there is no central pin), and reports
// it against the platform's releases:
//
//   * a table in the step summary (or stdout locally),
//   * a ::warning per consumer that is one release or more behind, or on pre-rename packages,
//   * one platform issue, upserted by a hidden marker so a re-run edits rather than duplicates,
//     naming each laggard's open "[Plenipo upgrade]" issue when it has one.
//
// Read-only against the products. GH_TOKEN with contents:read on the consumers and issues:write on
// the platform. DRY_RUN=1 prints everything and writes nothing.
//
//   GH_TOKEN=$(gh auth token) DRY_RUN=1 node .github/scripts/fleet-drift.mjs
// =============================================================================
import { readFileSync, appendFileSync } from "node:fs";

const token = process.env.GH_TOKEN ?? process.env.GITHUB_TOKEN;
if (!token) {
  console.error("GH_TOKEN is required.");
  process.exit(2);
}
const platformRepo = process.env.GITHUB_REPOSITORY ?? "abrahamFerga/Plenipo";
const dryRun = process.env.DRY_RUN === "1";
const marker = "<!-- plenipo-fleet-drift -->";

const api = async (path, init = {}) => {
  const res = await fetch(`https://api.github.com${path}`, {
    ...init,
    headers: {
      Accept: "application/vnd.github+json",
      Authorization: `Bearer ${token}`,
      "X-GitHub-Api-Version": "2022-11-28",
      ...(init.headers ?? {}),
    },
  });
  if (res.status === 404) return null;
  if (!res.ok) throw new Error(`${init.method ?? "GET"} ${path} → ${res.status} ${await res.text()}`);
  return res.status === 204 ? null : res.json();
};

const fileText = async (repo, ref, path) => {
  const r = await api(`/repos/${repo}/contents/${path}?ref=${encodeURIComponent(ref)}`);
  return r?.content ? Buffer.from(r.content, "base64").toString("utf8") : null;
};

// Releases newest-first, as the platform published them; the index is "how many releases behind".
const releases = (await api(`/repos/${platformRepo}/releases?per_page=100`)) ?? [];
const tags = releases.filter((r) => !r.draft).map((r) => r.tag_name.replace(/^v/, ""));
const latest = tags[0];
if (!latest) throw new Error("No releases found on the platform.");

const registry = JSON.parse(readFileSync("consumers.json", "utf8"));

async function pinOf(repo, ref) {
  const props = await fileText(repo, ref, "Directory.Build.props");
  const central = props?.match(/<PlenipoVersion>([^<]+)<\/PlenipoVersion>/)?.[1];
  if (central) return { pin: central, family: "Plenipo", source: "Directory.Build.props" };

  // No central pin: look at the csproj files the way a consumer that repeats the version does.
  const tree = await api(`/repos/${repo}/git/trees/${encodeURIComponent(ref)}?recursive=1`);
  const csprojs = (tree?.tree ?? []).filter((t) => t.path.endsWith(".csproj") && !t.path.includes("/tests/")).map((t) => t.path);
  for (const path of csprojs) {
    const text = await fileText(repo, ref, path);
    const m = text?.match(/Include="(Plenipo|Cortex)\.[A-Za-z.]+"\s+Version="([^"]+)"/);
    if (m) return { pin: m[2], family: m[1], source: path };
  }
  return { pin: null, family: null, source: csprojs.length ? `${csprojs.length} csproj, no platform reference` : "no src on this branch" };
}

async function upgradeIssue(repo) {
  const q = encodeURIComponent(`repo:${repo} is:issue is:open "[Plenipo upgrade]" in:title`);
  const r = await api(`/search/issues?q=${q}&per_page=1`);
  const hit = r?.items?.[0];
  return hit ? `${repo.split("/")[1]}#${hit.number}` : null;
}

const rows = [];
for (const c of registry.consumers) {
  const ref = c.ref || "main";
  const { pin, family, source } = await pinOf(c.repo, ref);
  const base = pin?.replace(/^(\d+\.\d+\.\d+-[a-z]+\.\d+)(\.\d+)?$/, "$1"); // 0.1.0-alpha.29.17 → 0.1.0-alpha.29
  const prerelease = pin && base !== pin ? pin.slice(base.length + 1) : null;
  const behind = base ? tags.indexOf(base) : null; // -1 = a version the platform never released
  const issue = await upgradeIssue(c.repo);
  let status;
  if (!pin) status = "no pin";
  else if (family === "Cortex") status = `pre-rename ${family}.* — ${behind >= 0 ? behind : "many"} releases behind`;
  else if (behind === 0) status = prerelease ? `current (+prerelease .${prerelease})` : "current";
  else if (behind > 0) status = `${behind} release${behind === 1 ? "" : "s"} behind`;
  else status = "unreleased version";
  rows.push({ repo: c.repo, ref, pin: pin ?? "—", source, behind, family, status, issue, conformance: c.conformance !== false, required: c.required === true });
}

const lagging = rows.filter((r) => r.behind === null || r.behind !== 0 || r.family === "Cortex");
const table = [
  "| Consumer | Pin | Source | Status | Upgrade issue | In matrix |",
  "|---|---|---|---|---|---|",
  ...rows.map((r) => `| \`${r.repo}\` | \`${r.pin}\` | ${r.source} | ${r.status} | ${r.issue ?? "—"} | ${r.conformance ? (r.required ? "required" : "reported") : "off"} |`),
].join("\n");
const heading = `### Fleet drift vs \`v${latest}\` (${new Date().toISOString().slice(0, 10)})`;
const report = `${heading}\n\n${table}\n\n${lagging.length ? `${lagging.length} of ${rows.length} consumers are not on the latest release.` : `All ${rows.length} registered consumers are on the latest release.`}`;

console.log(report);
for (const r of lagging) {
  console.log(`::warning title=Fleet drift — ${r.repo}::pins ${r.pin} (${r.status}); latest is ${latest}${r.issue ? `; upgrade issue ${r.issue}` : "; no open [Plenipo upgrade] issue — run announce-release"}`);
}
if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${report}\n`);

// One living issue on the platform, found by its marker; edited in place, never duplicated.
const title = "Fleet drift: consumer platform pins vs the last release";
const body = `${marker}\n${report}\n\nProduced by \`.github/workflows/fleet-drift.yml\` (weekly and on demand). The push channel for a lagging product is still \`announce-release\`; this is the pull-side dashboard so nobody has to remember to look.`;
if (dryRun) {
  console.log(`\n[dry run] would upsert issue "${title}" on ${platformRepo}`);
} else {
  const found = await api(`/search/issues?q=${encodeURIComponent(`repo:${platformRepo} is:issue is:open "${title}" in:title`)}&per_page=5`);
  const existing = found?.items?.find((i) => i.body?.includes(marker));
  if (existing) {
    await api(`/repos/${platformRepo}/issues/${existing.number}`, { method: "PATCH", body: JSON.stringify({ body }) });
    console.log(`updated ${platformRepo}#${existing.number}`);
  } else {
    const created = await api(`/repos/${platformRepo}/issues`, { method: "POST", body: JSON.stringify({ title, body }) });
    console.log(`opened ${platformRepo}#${created.number}`);
  }
}
