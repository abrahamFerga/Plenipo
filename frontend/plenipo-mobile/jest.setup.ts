// @testing-library/react-native v13 registers its Jest matchers (toBeOnTheScreen, …) on import
// of the library itself, so there is nothing to extend here.

/**
 * The shell's tests run against the real renderer and a fake API — never a real device, never a
 * real backend. Anything that would reach for a native module the test environment doesn't have
 * is stubbed here, so a test failure always means the shell got the manifest wrong.
 */

// react-native-svg renders through native views; the mock keeps chart tests about the geometry
// the shaping produced rather than about pixel output.
jest.mock("react-native-svg", () => {
  const React = require("react");
  const { View } = require("react-native");
  const stub = (name: string) => {
    const C = (props: Record<string, unknown>) => React.createElement(View, { ...props, testID: props.testID ?? name });
    C.displayName = name;
    return C;
  };
  return {
    __esModule: true,
    default: stub("Svg"),
    Svg: stub("Svg"),
    G: stub("G"),
    Path: stub("Path"),
    Circle: stub("Circle"),
    Line: stub("Line"),
    Rect: stub("Rect"),
    Text: stub("SvgText"),
  };
});

// Silence the act() noise React Native's animation helpers produce in a test renderer; it says
// nothing about whether the shell rendered the right thing.
jest.spyOn(console, "warn").mockImplementation((message?: unknown, ...rest: unknown[]) => {
  if (typeof message === "string" && message.includes("useNativeDriver")) return;
  console.info(message, ...rest);
});
