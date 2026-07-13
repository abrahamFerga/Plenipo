/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the Plenipo API the shell reads from. Defaults to http://localhost:8080. */
  readonly VITE_API_BASE?: string;
  /** Where the "Admin" link points (the separately-served admin console). Defaults to "/admin". */
  readonly VITE_ADMIN_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
