import { readdirSync, readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";

/**
 * The invariant that makes this package worth extracting: it must run unchanged in a browser, in
 * Hermes, and in Node. Anything that only exists in one of those is a fork waiting to happen — a
 * `document` reference here means the mobile shell silently can't use the file, and the two
 * renderers start drifting apart on the manifest contract.
 *
 * Enforced by reading the sources rather than by discipline, because the failure mode is a crash
 * on a phone that no web test would ever catch.
 */

const src = dirname(fileURLToPath(import.meta.url));

const RULES: { pattern: RegExp; why: string }[] = [
  { pattern: /\bdocument\s*\./, why: "there is no DOM in React Native" },
  { pattern: /\bwindow\s*\./, why: "there is no `window` in React Native" },
  {
    pattern: /\b(?:localStorage|sessionStorage)\b/,
    why: "web-only storage — each shell supplies its own (React Native uses SecureStore)",
  },
  {
    pattern: /import\.meta\.env/,
    why: "a bundler-specific global — take configuration through configureClient() instead",
  },
  {
    pattern: /\bprocess\.env\b/,
    why: "Node-specific — take configuration through configureClient() instead",
  },
  { pattern: /from\s+["']react["']/, why: "this package is renderer-free by definition" },
  { pattern: /from\s+["']react-native["']/, why: "this package is renderer-free by definition" },
];

/**
 * Strips comments before matching. These rules are about what the code DOES, and this package's
 * doc comments are prose — "refresh the document", "the browser's time zone" — which would
 * otherwise trip every rule and train everyone to ignore the guard.
 */
function stripComments(code: string): string {
  return code
    .replace(/\/\*[\s\S]*?\*\//g, " ")
    // Not after ":" so a URL in a string ("https://…") survives.
    .replace(/(^|[^:"'`\\])\/\/[^\n]*/gm, "$1");
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((entry) => {
    const path = join(dir, entry.name);
    if (entry.isDirectory()) return sourceFiles(path);
    return entry.isFile() && entry.name.endsWith(".ts") && !entry.name.endsWith(".test.ts") ? [path] : [];
  });
}

describe("the client stays renderer-free", () => {
  const files = sourceFiles(src);

  it("finds sources to check (a passing scan of zero files proves nothing)", () => {
    expect(files.length).toBeGreaterThan(4);
  });

  it.each(files.map((f) => [f.slice(src.length + 1), f] as const))(
    "%s uses no platform-only global",
    (_name, path) => {
      const code = stripComments(readFileSync(path, "utf8"));
      for (const { pattern, why } of RULES) {
        expect(pattern.test(code), `${pattern} is not allowed here — ${why}`).toBe(false);
      }
    },
  );
});
