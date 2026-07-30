// Server-declared form defaults live in @plenipo/client (see lib/api.ts). The browser sources for
// `defaultFrom` — the viewer's time zone and locale currency — are the client's defaults, so this
// shell configures nothing extra.
export { resolveFieldDefault, resolveFieldDefaults } from "@plenipo/client";
