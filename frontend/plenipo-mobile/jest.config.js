/**
 * jest-expo gives the React Native module registry, the Babel transform for TS/JSX, and the
 * platform mocks.
 */

/**
 * Packages that ship untranspiled ESM and therefore have to go through Babel.
 *
 * The usual Expo pattern anchors these right after `node_modules/`, which does not work under
 * pnpm: it stores a package at `node_modules/.pnpm/<name>@<version>_<hash>/node_modules/<name>`,
 * so the segment straight after the first `node_modules/` is `.pnpm`, never the package name.
 * Matching the name ANYWHERE in the path handles both layouts. Substrings do real work here —
 * "react-native" also covers react-native-svg and react-native-safe-area-context, and "expo"
 * covers every expo-* module.
 */
const NEEDS_TRANSFORM = [
  "react-native",
  "@react-native",
  "@react-navigation",
  "react-navigation",
  "expo",
  "@expo",
  "@plenipo",
].join("|");

export default {
  preset: "jest-expo",
  setupFilesAfterEach: undefined,
  setupFilesAfterEnv: ["<rootDir>/jest.setup.ts"],
  transformIgnorePatterns: [`node_modules[\\\\/](?!.*(${NEEDS_TRANSFORM}))`],
  moduleNameMapper: {
    // Resolve the workspace package to its TypeScript source, the same way Metro does.
    "^@plenipo/client$": "<rootDir>/../plenipo-client/src/index.ts",
  },
  testMatch: ["<rootDir>/src/**/*.test.ts", "<rootDir>/src/**/*.test.tsx"],
};
