import { useInfo } from "../hooks/useInfo";

/**
 * A thin banner shown when the chat assistant is running on the dependency-free Mock provider, so a
 * newcomer following the quickstart understands the canned answers are expected — not a broken AI.
 * Renders nothing once a real provider is configured.
 */
export function DemoModeBanner() {
  const { data: info } = useInfo();
  if (!info?.demoMode) {
    return null;
  }

  return (
    <div className="border-b border-amber-300 bg-amber-50 px-4 py-1.5 text-center text-xs text-amber-800 dark:border-amber-700/60 dark:bg-amber-900/20 dark:text-amber-200">
      <span className="font-medium">Demo mode</span> — the assistant uses the built-in Mock provider, so
      answers are canned. Configure an AI provider (see GETTING_STARTED) for real answers.
    </div>
  );
}
