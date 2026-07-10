import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "react-router-dom";
import { apiGet, type Module } from "../lib/api";

const dismissKey = (moduleId: string) => `cortex.onboarding.dismissed.${moduleId}`;

/**
 * The respectful first-run prompt: when a module declares onboarding and its probe endpoint says
 * "no data yet", offer the guided setup as a banner — never hijack navigation. Dismissal sticks
 * per module (localStorage); the wizard stays reachable at /setup regardless.
 */
export function OnboardingOffer({ module }: { module: Module | undefined }) {
  const navigate = useNavigate();
  const setup = module?.onboarding;
  const [dismissed, setDismissed] = useState(
    () => setup !== null && module !== undefined && localStorage.getItem(dismissKey(module.id)) === "1",
  );

  const probe = useQuery({
    queryKey: ["onboarding-probe", module?.id],
    queryFn: () => apiGet<unknown[]>(setup!.probeEndpoint),
    enabled: Boolean(setup) && !dismissed,
    staleTime: 60_000,
  });

  if (!module || !setup || dismissed || probe.data === undefined || probe.data.length > 0) {
    return null;
  }

  return (
    <div
      role="region"
      aria-label="First-time setup"
      className="flex flex-wrap items-center gap-3 border-b border-brand-200 bg-brand-50 px-4 py-2.5 text-sm text-brand-900 dark:border-brand-900/60 dark:bg-brand-950/60 dark:text-brand-100"
    >
      <span aria-hidden>👋</span>
      <span className="font-medium">{setup.title}</span>
      <span className="hidden text-brand-700 sm:inline dark:text-brand-300">
        A few guided minutes — everything is skippable, everything can wait.
      </span>
      <span className="flex-1" />
      <button
        type="button"
        onClick={() => navigate("/setup")}
        className="focus-ring rounded bg-brand-600 px-3 py-1 text-sm font-medium text-white hover:bg-brand-500"
      >
        Start setup
      </button>
      <button
        type="button"
        onClick={() => {
          localStorage.setItem(dismissKey(module.id), "1");
          setDismissed(true);
        }}
        className="focus-ring rounded px-2 py-1 text-sm text-brand-700 hover:text-brand-900 dark:text-brand-300 dark:hover:text-brand-100"
      >
        Not now
      </button>
    </div>
  );
}
