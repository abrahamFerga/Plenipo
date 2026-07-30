# @plenipo/client

The renderer-free half of a Plenipo frontend: the TypeScript mirror of the platform's manifest
contract, the API client, and the AG-UI chat transport. No React, no DOM, no bundler globals.

It exists so the web shell (`@plenipo/ui`) and the mobile shell (`@plenipo/mobile`) render the
**same** server-driven descriptors. When a C# `TabDescriptor` grows a field, it lands in one
TypeScript file and both shells see it — which is the only way two renderers stay honest about a
manifest-first platform.

## What's in it

| Area | Exports |
|---|---|
| Manifest contract | `Module`, `ModuleTab`, `TabColumn`, `TabEditor`, `TabEditorField`, `TabChart`, `TabAction`, `TabRowAction`, `TabDetailDocument`, … |
| REST | `api` (the whole surface), `apiGet`, `apiPost`, `apiSend`, `apiAction`, `uploadFile`, `ApiError` |
| Chat | `runAgui` (HTTP POST + SSE, the open AG-UI protocol), `parseAguiFrames` |
| Shared logic | `hasPermission` (mirrors the server's `PermissionMatcher`), `resolveFieldDefault(s)`, `withAttachmentRefs` / `parseAttachmentRefs` |
| Configuration | `configureClient`, `clientConfig`, `apiBase`, `normalizeApiBase` |

## Configuration

Everything has a working default — an unconfigured import talks to `http://localhost:8080` with
dev-auth headers over the global `fetch`, which is what a local dev host expects. A shell overrides
what it needs:

```ts
import { configureClient } from "@plenipo/client";
import { fetch as streamingFetch } from "expo/fetch";

configureClient({
  baseUrl: "https://api.example.com",
  // Called per request, so a refreshed token is picked up mid-session. May be async.
  authHeaders: async () => ({ Authorization: `Bearer ${await readToken()}` }),
  // React Native's built-in fetch cannot stream a response body, which AG-UI chat needs.
  fetch: streamingFetch,
  // How this platform answers a field's `defaultFrom` token.
  fieldDefaultSources: { "browser-currency": () => deviceCurrency() },
});
```

`fieldDefaultSources` merges over the built-ins, so a shell improves one source without restating
the rest.

## The invariant

`src/renderer-free.test.ts` reads every source file and fails on `document.`, `window.`,
`localStorage`, `import.meta.env`, `process.env`, or an import of `react` / `react-native`. The
failure mode it guards against is a crash on a phone that no web test would catch, so it's checked
by the build rather than by discipline.

## Scripts

```bash
pnpm test        # vitest
pnpm typecheck   # tsc --noEmit
pnpm build       # tsc → dist/ (ESM + declarations)
pnpm lint        # eslint
```
