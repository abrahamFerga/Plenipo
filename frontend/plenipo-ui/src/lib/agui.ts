// The AG-UI chat transport lives in @plenipo/client (see lib/api.ts).
import "./devAuth"; // side effect: points the shared client at this app's API base

export { runAgui, messageId, parseAguiFrames } from "@plenipo/client";
export type { AguiEvent } from "@plenipo/client";
