import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api } from "../lib/api";
import { ConfirmDialog } from "./ConfirmDialog";

interface ConversationListProps {
  moduleId: string;
  selectedId?: string;
  onSelect: (id: string) => void;
  onNew: () => void;
  /** Disables selection/new/delete while a turn is streaming, to avoid switching mid-response. */
  disabled?: boolean;
}

function formatWhen(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { month: "short", day: "numeric" });
}

/** The chat history sidebar: the user's conversations for a module, newest first, with new + delete. */
export function ConversationList({ moduleId, selectedId, onSelect, onNew, disabled }: ConversationListProps) {
  const queryClient = useQueryClient();
  const [pendingDeleteId, setPendingDeleteId] = useState<string | null>(null);
  const conversations = useQuery({
    queryKey: ["conversations", moduleId],
    queryFn: () => api.conversations(moduleId),
  });

  const remove = useMutation({
    mutationFn: (id: string) => api.deleteConversation(id),
    onSuccess: (_void, id) => {
      void queryClient.invalidateQueries({ queryKey: ["conversations", moduleId] });
      // If we just deleted the conversation being viewed, fall back to a fresh chat.
      if (id === selectedId) {
        onNew();
      }
    },
  });

  const rename = useMutation({
    mutationFn: ({ id, title }: { id: string; title: string }) => api.renameConversation(id, title),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["conversations", moduleId] }),
  });

  return (
    <aside className="flex w-56 shrink-0 flex-col border-r border-slate-200 dark:border-slate-700">
      <button
        type="button"
        onClick={onNew}
        disabled={disabled}
        className="focus-ring m-2 rounded-md bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:cursor-not-allowed disabled:opacity-50"
      >
        + New chat
      </button>

      <div className="min-h-0 flex-1 overflow-y-auto px-2 pb-2">
        {conversations.isLoading && <p className="px-2 text-xs text-slate-400">Loading…</p>}
        {conversations.isError && (
          <p className="px-2 text-xs text-red-500">Could not load conversations.</p>
        )}
        {conversations.data?.length === 0 && (
          <p className="px-2 text-xs text-slate-400">No conversations yet.</p>
        )}
        <ul className="space-y-1">
          {conversations.data?.map((c) => (
            <li
              key={c.id}
              className={`group flex items-center rounded-md ${
                selectedId === c.id ? "bg-slate-100 dark:bg-slate-800" : "hover:bg-slate-50 dark:hover:bg-slate-800/60"
              }`}
            >
              <button
                type="button"
                onClick={() => onSelect(c.id)}
                disabled={disabled}
                title={c.title || "Untitled chat"}
                className="focus-ring min-w-0 flex-1 rounded-md px-2 py-1.5 text-left disabled:cursor-not-allowed disabled:opacity-50"
              >
                <span
                  className={`block truncate text-sm ${
                    selectedId === c.id
                      ? "font-medium text-slate-900 dark:text-slate-100"
                      : "text-slate-600 dark:text-slate-300"
                  }`}
                >
                  {c.title || "Untitled chat"}
                </span>
                <span className="block text-[11px] text-slate-400">{formatWhen(c.updatedAt)}</span>
              </button>
              <button
                type="button"
                aria-label="Rename conversation"
                disabled={disabled || rename.isPending}
                onClick={() => {
                  const next = window.prompt("Rename conversation", c.title || "")?.trim();
                  if (next) {
                    rename.mutate({ id: c.id, title: next });
                  }
                }}
                className="focus-ring rounded px-1 text-slate-400 opacity-0 hover:text-brand-600 focus:opacity-100 group-hover:opacity-100 disabled:opacity-30"
              >
                ✎
              </button>
              <button
                type="button"
                aria-label="Delete conversation"
                disabled={disabled || remove.isPending}
                onClick={() => setPendingDeleteId(c.id)}
                className="focus-ring rounded px-2 text-slate-400 opacity-0 hover:text-red-600 focus:opacity-100 group-hover:opacity-100 disabled:opacity-30"
              >
                ×
              </button>
            </li>
          ))}
        </ul>
      </div>

      <ConfirmDialog
        open={pendingDeleteId !== null}
        title="Delete conversation"
        body="Delete this conversation? This cannot be undone."
        confirmLabel="Delete"
        tone="danger"
        onConfirm={() => {
          if (pendingDeleteId) {
            remove.mutate(pendingDeleteId);
          }
          setPendingDeleteId(null);
        }}
        onCancel={() => setPendingDeleteId(null)}
      />
    </aside>
  );
}
