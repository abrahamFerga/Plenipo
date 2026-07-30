const path = require("node:path");
const { getDefaultConfig } = require("expo/metro-config");

const projectRoot = __dirname;
const workspaceRoot = path.resolve(projectRoot, "..");

/**
 * Metro in a pnpm workspace.
 *
 * Two things are not the default. Metro only watches the project folder, so edits to
 * `@plenipo/mobile` or `@plenipo/client` — which are symlinks into sibling folders — would not
 * trigger a reload; `watchFolders` fixes that. And pnpm's store means a dependency's own
 * dependencies live under `<root>/node_modules/.pnpm/...`, so resolution has to look at the
 * workspace root as well as here.
 *
 * A product that copies this app into its own repo and installs `@plenipo/mobile` from npm
 * doesn't need any of this — delete the file.
 */
const config = getDefaultConfig(projectRoot);

config.watchFolders = [workspaceRoot];
config.resolver.nodeModulesPaths = [
  path.resolve(projectRoot, "node_modules"),
  path.resolve(workspaceRoot, "node_modules"),
];
// The @plenipo/* packages ship TypeScript source; Metro already transforms dependencies through
// babel-preset-expo, so nothing extra is needed beyond letting it follow the symlinks.
config.resolver.unstable_enableSymlinks = true;

module.exports = config;
