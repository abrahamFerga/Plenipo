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

  /**
   * Jest's 5 s default is a per-TEST budget, and under `jest-expo` the FIRST test of a suite also
   * pays for building the React Native module registry and Babel-transforming everything in
   * NEEDS_TRANSFORM. These suites take 16–20 s end to end on a CI runner, so that first test loses
   * the race on a cold or contended machine while every assertion in it is fine — the same commit
   * passed one CI run and failed the next with no frontend change between them.
   *
   * The budget is raised, not the work reduced: 20 s is comfortably above the slowest observed
   * suite total, so a test that genuinely hangs still fails rather than stalling the job.
   */
  testTimeout: 20_000,
};
