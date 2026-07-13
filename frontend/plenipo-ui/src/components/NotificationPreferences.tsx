import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../lib/api";

/**
 * The per-category mute switchboard inside the notification bell: every category an installed
 * module declares, each with its own toggle. Muting one suppresses that category entirely
 * (in-app and channels) without touching anything else.
 */
export function NotificationPreferences() {
  const qc = useQueryClient();
  const prefs = useQuery({ queryKey: ["notification-prefs"], queryFn: api.notifications.preferences });
  const toggle = useMutation({
    mutationFn: ({ category, enabled }: { category: string; enabled: boolean }) =>
      api.notifications.setPreference(category, enabled),
    onSuccess: () => qc.invalidateQueries({ queryKey: ["notification-prefs"] }),
  });

  const categories = prefs.data ?? [];
  if (prefs.isLoading) {
    return <p className="px-3 py-2 text-xs text-slate-500">Loading preferences…</p>;
  }
  if (categories.length === 0) {
    return (
      <p className="px-3 py-2 text-xs text-slate-500 dark:text-slate-400">
        No mutable categories — the installed modules declare none.
      </p>
    );
  }

  return (
    <ul className="max-h-60 overflow-y-auto">
      {categories.map((c) => (
        <li key={c.id} className="border-b border-slate-100 px-3 py-2 last:border-b-0 dark:border-slate-800">
          <label className="flex cursor-pointer items-start justify-between gap-2">
            <span className="min-w-0">
              <span className="block text-sm text-slate-900 dark:text-slate-100">{c.label}</span>
              {c.description && (
                <span className="block text-xs text-slate-500 dark:text-slate-400">{c.description}</span>
              )}
            </span>
            <input
              type="checkbox"
              checked={c.enabled}
              disabled={toggle.isPending}
              onChange={(e) => toggle.mutate({ category: c.id, enabled: e.target.checked })}
              aria-label={`${c.label} notifications`}
              className="mt-0.5 shrink-0"
            />
          </label>
        </li>
      ))}
    </ul>
  );
}
