/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the Plenipo API the console reads from. Mirrors @plenipo/ui's VITE_API_BASE. */
  readonly VITE_API_BASE?: string;
  /** Where the "Back to workspace" link points (the domain UI). Defaults to "/". */
  readonly VITE_WORKSPACE_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
