import js from "@eslint/js";
import globals from "globals";
import reactHooks from "eslint-plugin-react-hooks";
import tseslint from "typescript-eslint";

export default tseslint.config(
  { ignores: ["dist", "node_modules", ".expo"] },
  {
    extends: [js.configs.recommended, ...tseslint.configs.recommended],
    files: ["**/*.{ts,tsx}"],
    languageOptions: {
      ecmaVersion: 2022,
      globals: {
        ...globals.es2022,
        // React Native's runtime globals. Deliberately not globals.browser: reaching for
        // `document` or `localStorage` here is a bug the linter should catch, not a runtime crash
        // on someone's phone.
        fetch: "readonly",
        Response: "readonly",
        RequestInit: "readonly",
        FormData: "readonly",
        AbortController: "readonly",
        console: "readonly",
        setTimeout: "readonly",
        clearTimeout: "readonly",
        setInterval: "readonly",
        clearInterval: "readonly",
        __DEV__: "readonly",
      },
    },
    plugins: { "react-hooks": reactHooks },
    rules: { ...reactHooks.configs.recommended.rules },
  },
  {
    // Tests run under jest-expo, and mock modules freely.
    files: ["**/*.test.{ts,tsx}", "jest.setup.ts"],
    languageOptions: { globals: { ...globals.jest } },
    rules: {
      // jest.mock factories are hoisted above imports, so a module they need can only be pulled
      // in with require(). This is the one place that's correct rather than lazy.
      "@typescript-eslint/no-require-imports": "off",
    },
  },
);
