module.exports = function (api) {
  api.cache(true);
  // babel-preset-expo handles TypeScript, JSX, and the React Native runtime — including for the
  // workspace-linked @plenipo/* packages, which ship TypeScript source rather than a build.
  return { presets: ["babel-preset-expo"] };
};
