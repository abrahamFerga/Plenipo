import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist"] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ["**/*.ts"],
    languageOptions: {
      ecmaVersion: 2022,
      // Deliberately NOT globals.browser: this package runs under Hermes too. Only the handful of
      // web-standard globals React Native also implements are in scope, so reaching for `document`
      // or `localStorage` is a lint error at the moment it's typed rather than a crash on a phone.
      globals: {
        ...globals.es2022,
        fetch: "readonly",
        Response: "readonly",
        Request: "readonly",
        Headers: "readonly",
        FormData: "readonly",
        Blob: "readonly",
        File: "readonly",
        AbortSignal: "readonly",
        AbortController: "readonly",
        TextDecoder: "readonly",
        TextEncoder: "readonly",
        URL: "readonly",
        URLSearchParams: "readonly",
        crypto: "readonly",
        navigator: "readonly",
        console: "readonly",
      },
    },
  },
);
