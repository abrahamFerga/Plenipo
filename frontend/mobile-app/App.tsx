import { PlenipoMobileApp } from "@plenipo/mobile";
import { expoAdapters } from "@plenipo/mobile/expo";

/**
 * The whole app.
 *
 * There is no screen, no route, and no domain vocabulary here — those all come from
 * `GET /api/platform/modules` at runtime. Install a module in the C# host and it shows up on
 * every phone that already has this build; add a tab to its manifest and the tab appears. That is
 * the point: a product's mobile app is a brand and a base URL, and shipping domain capability is
 * a backend deploy rather than an App Store review.
 *
 * To make this YOUR product's app: change `apiBase` and `branding`, drop in your icon, and set
 * the bundle identifiers in app.json. Everything below is optional from there.
 */
export default function App() {
  return (
    <PlenipoMobileApp
      config={{
        // Where your Plenipo host is. A device cannot reach "localhost" — use your machine's LAN
        // address (Expo prints it on start) when running against a local host.
        apiBase: process.env.EXPO_PUBLIC_API_BASE ?? "http://localhost:8080",

        // Secure storage, device identity, push, locale, and a streaming fetch, on the standard
        // Expo modules. Pass your own `auth` here to plug in a real IdP; without one the shell
        // uses the platform's Development-only dev auth, which is what makes this run with zero
        // configuration against a local host.
        ...expoAdapters(),
      }}
      // The name in the header. Omit it and the shell asks the host who it is
      // (`Branding:ProductName`), so one build can serve several deployments.
      branding={{ name: "Plenipo" }}
      // Rebrand the whole shell — nav, buttons, links, focus — by setting the brand token.
      // theme={{ both: { brand: "#2a78d6" } }}
      //
      // A tab that needs more than the generic renderer registers a native screen:
      // moduleUi={[defineModule("legal", { tabs: { matters: MattersBoard } })]}
    />
  );
}
