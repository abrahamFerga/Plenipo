// The API client and the manifest contract live in @plenipo/client, so the web shell and the
// mobile shell render the same descriptors and a change to the C# side lands in one place. This
// module stays as the shell's import path for them — every component imports from "../lib/api"
// exactly as before.
import "./devAuth"; // side effect: points the shared client at this app's API base

export * from "@plenipo/client";
