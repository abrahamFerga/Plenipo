import { useEffect, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../lib/api";
import { NotificationPreferences } from "./NotificationPreferences";

/** How stale the badge may get before the next poll (background events push nothing yet). */
const POLL_MS = 30_000;

function timeAgo(iso: string): string {
  const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 60) return "just now";
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

/**
 * The in-app notification inbox: a bell with an unread badge, polling the self-scoped
 * notifications API; opening it lists the latest items (job completions, budget alerts, …) with
 * per-item and mark-all read actions.
 */
export function NotificationBell() {
  const [open, setOpen] = useState(false);
  const [showPrefs, setShowPrefs] = useState(false);
  const panelRef = useRef<HTMLDivElement>(null);
  const queryClient = useQueryClient();

  const inbox = useQuery({
    queryKey: ["notifications"],
    queryFn: () => api.notifications.list(),
    refetchInterval: POLL_MS,
  });

  const invalidate = () => queryClient.invalidateQueries({ queryKey: ["notifications"] });
  const markRead = useMutation({ mutationFn: api.notifications.markRead, onSuccess: invalidate });
  const markAllRead = useMutation({ mutationFn: api.notifications.markAllRead, onSuccess: invalidate });

  // Close on outside click / Escape — standard popover behavior without a positioning library.
  useEffect(() => {
    if (!open) return;
    const onPointerDown = (e: PointerEvent) => {
      if (!panelRef.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("pointerdown", onPointerDown);
    document.addEventListener("keydown", onKeyDown);
    return () => {
      document.removeEventListener("pointerdown", onPointerDown);
      document.removeEventListener("keydown", onKeyDown);
    };
  }, [open]);

  const items = inbox.data ?? [];
  const unread = items.filter((n) => !n.readAt).length;

  return (
    <div className="relative" ref={panelRef}>
      <button
        type="button"
        onClick={() => setOpen((v) => !v)}
        aria-label={unread > 0 ? `Notifications (${unread} unread)` : "Notifications"}
        aria-expanded={open}
        className="focus-ring relative rounded-md p-1.5 text-slate-600 hover:bg-slate-100 dark:text-slate-300 dark:hover:bg-slate-800"
      >
        <svg
          viewBox="0 0 20 20"
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          strokeLinecap="round"
          strokeLinejoin="round"
          className="h-5 w-5"
          aria-hidden="true"
        >
          <path d="M10 2.5a4.5 4.5 0 0 0-4.5 4.5c0 3.2-1 4.5-2 5.5h13c-1-1-2-2.3-2-5.5A4.5 4.5 0 0 0 10 2.5Z" />
          <path d="M8.2 15.5a2 2 0 0 0 3.6 0" />
        </svg>
        {unread > 0 && (
          <span
            aria-hidden="true"
            className="absolute -right-0.5 -top-0.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-brand-600 px-1 text-[10px] font-bold text-white"
          >
            {unread > 9 ? "9+" : unread}
          </span>
        )}
      </button>

      {open && (
        <div
          role="dialog"
          aria-label="Notifications"
          className="absolute right-0 top-10 z-30 w-80 rounded-lg border border-slate-200 bg-white shadow-lg dark:border-slate-700 dark:bg-slate-900"
        >
          <div className="flex items-center justify-between border-b border-slate-200 px-3 py-2 dark:border-slate-700">
            <span className="text-sm font-semibold text-slate-900 dark:text-slate-100">Notifications</span>
            <span className="flex items-center gap-1">
              {unread > 0 && !showPrefs && (
                <button
                  type="button"
                  onClick={() => markAllRead.mutate()}
                  className="focus-ring rounded px-2 py-0.5 text-xs font-medium text-brand-600 hover:bg-slate-100 dark:hover:bg-slate-800"
                >
                  Mark all read
                </button>
              )}
              <button
                type="button"
                onClick={() => setShowPrefs((v) => !v)}
                aria-pressed={showPrefs}
                className="focus-ring rounded px-2 py-0.5 text-xs font-medium text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800"
              >
                {showPrefs ? "Back" : "Preferences"}
              </button>
            </span>
          </div>
          {showPrefs && <NotificationPreferences />}
          {!showPrefs && (
          <ul className="max-h-96 overflow-y-auto">
            {items.length === 0 && (
              <li className="px-3 py-6 text-center text-sm text-slate-500 dark:text-slate-400">
                Nothing yet — job completions and alerts land here.
              </li>
            )}
            {items.map((n) => (
              <li
                key={n.id}
                className="border-b border-slate-100 px-3 py-2 last:border-b-0 dark:border-slate-800"
              >
                <div className="flex items-start gap-2">
                  {!n.readAt && (
                    <span
                      className="mt-1.5 h-2 w-2 shrink-0 rounded-full bg-brand-600"
                      role="status"
                      aria-label="Unread"
                    />
                  )}
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-slate-900 dark:text-slate-100">
                      {n.title}
                    </p>
                    <p className="line-clamp-2 text-xs text-slate-500 dark:text-slate-400">{n.body}</p>
                    <p className="mt-0.5 text-[11px] text-slate-400 dark:text-slate-500">
                      {n.category} · {timeAgo(n.createdAt)}
                    </p>
                  </div>
                  {!n.readAt && (
                    <button
                      type="button"
                      onClick={() => markRead.mutate(n.id)}
                      aria-label={`Mark "${n.title}" read`}
                      className="focus-ring shrink-0 rounded px-1.5 py-0.5 text-xs text-slate-500 hover:bg-slate-100 dark:hover:bg-slate-800"
                    >
                      ✓
                    </button>
                  )}
                </div>
              </li>
            ))}
          </ul>
          )}
        </div>
      )}
    </div>
  );
}
