import { useEffect, useState } from "react";
import { BrowserRouter } from "react-router-dom";
import { AppShell } from "./routes/AppShell";
import { API_BASE } from "./lib/devAuth";
import { resolveBrandName } from "./lib/branding";

// The dev-server entry doubles as a product host's shell. The product name resolves at RUNTIME
// from the host (/api/platform/branding <- Branding:ProductName), so one prebuilt bundle serves
// every product; VITE_BRAND_NAME remains the build-time fallback for hosts that bake their brand
// (and paints the first frame without waiting on the fetch). Library consumers pass `branding`
// to CortexApp/AppShell directly instead.
const buildTimeBrand = import.meta.env.VITE_BRAND_NAME as string | undefined;

export default function App() {
  const [brandName, setBrandName] = useState(buildTimeBrand);

  useEffect(() => {
    fetch(`${API_BASE}/api/platform/branding`)
      .then((res) => (res.ok ? (res.json() as Promise<{ name?: string }>) : null))
      .then((body) => {
        const resolved = resolveBrandName(buildTimeBrand, body?.name);
        if (resolved) {
          setBrandName(resolved);
        }
      })
      .catch(() => {
        // Offline dev server or API not up yet — the build-time brand (or default) stands.
      });
  }, []);

  useEffect(() => {
    if (brandName) {
      document.title = brandName;
    }
  }, [brandName]);

  return (
    // Opt into the React Router v7 behaviors now — silences the v6 future-flag console warnings and keeps
    // routing forward-compatible. Safe here: the app uses absolute links (no relative splat-path resolution).
    <BrowserRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
      <AppShell branding={brandName ? { name: brandName } : undefined} />
    </BrowserRouter>
  );
}
