import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  apiAction,
  apiGet,
  apiSend,
  type ModuleTab,
  type TabAction,
  type TabColumn,
  type TabDetailDocument,
  type TabEditor,
  type TabRowAction,
} from "../lib/api";
import { ConfirmDialog } from "./ConfirmDialog";
import { FieldInput } from "./FieldInput";
import { TabChartView } from "./TabChart";
import { NARROW_QUERY, useMediaQuery } from "../hooks/useMediaQuery";

interface GenericTabProps {
  tab: ModuleTab;
  children?: React.ReactNode;
}

/** Substitute every `{field}` placeholder in an endpoint template from the row's values. */
function resolveRowUrl(template: string, row: Record<string, unknown>): string {
  return template.replace(/\{(\w+)\}/g, (_, field: string) => encodeURIComponent(String(row[field] ?? "")));
}

/** Mask all but the last four characters — enough to recognize your own account, not enough to read it off a screen. */
const maskValue = (text: string) => (text.length > 4 ? `••••${text.slice(-4)}` : "••••");

/**
 * One cell's value. Columns declaring `masked` (the display-side companion of `[Pii]`) render
 * masked behind an explicit reveal toggle — per cell, per mount, never persisted. Masking is a
 * screen-privacy affordance, not access control: the value already reached this authorized
 * caller; it just shouldn't sit exposed on a shared screen.
 */
function CellValue({ column, row }: { column: TabColumn; row: Record<string, unknown> }) {
  const [revealed, setRevealed] = useState(false);
  const raw = row[column.field];
  const text = raw == null ? "" : String(raw);
  if (!column.masked || text === "") return <>{text}</>;
  return (
    <button
      type="button"
      onClick={() => setRevealed((v) => !v)}
      aria-pressed={revealed}
      aria-label={`${revealed ? "Hide" : "Reveal"} ${column.header}`}
      className="focus-ring rounded tabular-nums"
    >
      {revealed ? text : maskValue(text)}
    </button>
  );
}

/** The generic drill-down: a detail document rendered as prose and sub-tables, with a way back. */
function DetailView({ endpoint, onBack }: { endpoint: string; onBack: () => void }) {
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["tab-detail", endpoint],
    queryFn: () => apiGet<TabDetailDocument>(endpoint),
  });

  if (isLoading) {
    return <p className="text-sm text-slate-500">Loading…</p>;
  }
  if (isError) {
    return <p className="text-sm text-red-600">{(error as Error).message}</p>;
  }

  const doc = data!;
  return (
    <div className="space-y-5">
      <div>
        <button
          type="button"
          onClick={onBack}
          className="focus-ring mb-2 rounded border border-slate-300 px-2 py-1 text-xs font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
        >
          ← Back
        </button>
        <h2 className="text-lg font-semibold text-slate-900 dark:text-slate-100">{doc.title}</h2>
        {doc.subtitle && <p className="text-sm text-slate-500 dark:text-slate-400">{doc.subtitle}</p>}
      </div>

      {doc.sections.map((section) => (
        <section key={section.heading} className="space-y-2">
          <h3 className="text-sm font-semibold uppercase tracking-wide text-slate-400">{section.heading}</h3>
          {section.text != null && <p className="text-sm text-slate-700 dark:text-slate-200">{section.text}</p>}
          {section.rows != null &&
            ((section.rows.length ?? 0) === 0 ? (
              <p className="text-sm text-slate-400">None.</p>
            ) : (
              <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
                <table className="w-full text-left text-sm">
                  <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
                    <tr>
                      {(section.columns ?? []).map((c) => (
                        <th key={c.field} className="px-4 py-2 font-medium">
                          {c.header}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
                    {section.rows.map((row, i) => (
                      <tr key={i}>
                        {(section.columns ?? []).map((c) => (
                          <td key={c.field} className="px-4 py-2 text-slate-700 dark:text-slate-200">
                            {row[c.field] == null ? "" : String(row[c.field])}
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ))}
        </section>
      ))}
    </div>
  );
}

/**
 * The generic editor form: fields come from the tab's declared editor, values from the row being
 * edited (or empty for add). The key field is read-only while editing — it's the record identity.
 */
function EditorForm({
  editor,
  initial,
  onDone,
}: {
  editor: TabEditor;
  initial: Record<string, unknown> | null;
  onDone: () => void;
}) {
  const qc = useQueryClient();
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(editor.fields.map((f) => [f.field, initial?.[f.field] == null ? "" : String(initial[f.field])])),
  );

  const save = useMutation({
    // Numeric fields post as JSON numbers so endpoints binding decimal/int work as-is. Fields left
    // empty are OMITTED (not sent as "" — which nullable value types reject, and Number("") is 0):
    // only optional fields can be empty here, and absent binds server-side as null.
    mutationFn: () =>
      apiSend(
        editor.upsertEndpoint,
        "POST",
        Object.fromEntries(
          editor.fields
            .filter((f) => values[f.field].trim() !== "")
            .map((f) => [f.field, f.numeric ? Number(values[f.field]) : values[f.field]]),
        ),
      ),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
      onDone();
    },
  });

  const missing = editor.fields.some(
    (f) =>
      ((f.required ?? true) && values[f.field].trim() === "") ||
      (f.numeric && values[f.field].trim() !== "" && Number.isNaN(Number(values[f.field]))),
  );

  return (
    <form
      className="space-y-3 rounded-lg border border-brand-200 bg-white p-4 dark:border-brand-900/60 dark:bg-slate-900"
      onSubmit={(e) => {
        e.preventDefault();
        if (!missing) save.mutate();
      }}
    >
      {editor.fields.map((f) => (
        <div key={f.field} className="space-y-1">
          <label htmlFor={`editor-${f.field}`} className="block text-sm font-medium text-slate-700 dark:text-slate-200">
            {f.label}
          </label>
          <FieldInput
            id={`editor-${f.field}`}
            field={f}
            value={values[f.field]}
            disabled={initial !== null && editor.keyField === f.field}
            onChange={(value) => setValues((v) => ({ ...v, [f.field]: value }))}
          />
        </div>
      ))}

      {save.isError && <p className="text-xs text-red-600">{(save.error as Error).message}</p>}

      <div className="flex gap-2">
        <button
          type="submit"
          disabled={save.isPending || missing}
          className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
        >
          {save.isPending ? "Saving…" : "Save"}
        </button>
        <button
          type="button"
          onClick={onDone}
          className="focus-ring rounded border border-slate-300 px-3 py-1.5 text-sm font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
        >
          Cancel
        </button>
      </div>
    </form>
  );
}

/** The per-row affordances (commands, View, Edit, Delete) — one markup, used by table cells and cards alike. */
function RowButtons({
  row,
  commands,
  detailEndpoint,
  canEdit,
  canDelete,
  pending,
  onCommand,
  onView,
  onEdit,
  onDelete,
}: {
  row: Record<string, unknown>;
  commands: TabRowAction[];
  detailEndpoint?: string | null;
  canEdit: boolean;
  canDelete: boolean;
  pending: boolean;
  onCommand: (action: TabRowAction, row: Record<string, unknown>) => void;
  onView: (url: string) => void;
  onEdit: (row: Record<string, unknown>) => void;
  onDelete: (row: Record<string, unknown>) => void;
}) {
  return (
    <span className="inline-flex flex-wrap gap-2">
      {commands.map((action) => (
        <button
          key={action.id}
          type="button"
          disabled={pending}
          onClick={() => onCommand(action, row)}
          className="focus-ring rounded bg-brand-600 px-2 py-0.5 text-xs font-medium text-white hover:bg-brand-500 disabled:opacity-40"
        >
          {action.label}
        </button>
      ))}
      {detailEndpoint && (
        <button
          type="button"
          onClick={() => onView(resolveRowUrl(detailEndpoint, row))}
          className="focus-ring rounded border border-brand-300 px-2 py-0.5 text-xs font-medium text-brand-700 dark:border-brand-800 dark:text-brand-300"
        >
          View
        </button>
      )}
      {canEdit && (
        <button
          type="button"
          onClick={() => onEdit(row)}
          className="focus-ring rounded border border-slate-300 px-2 py-0.5 text-xs font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
        >
          Edit
        </button>
      )}
      {canDelete && (
        <button
          type="button"
          onClick={() => onDelete(row)}
          className="focus-ring rounded border border-red-300 px-2 py-0.5 text-xs font-medium text-red-600 hover:bg-red-50 dark:border-red-800 dark:hover:bg-red-900/20"
        >
          Delete
        </button>
      )}
    </span>
  );
}

/**
 * Renders a tab's `dataEndpoint` (a JSON array) as a generic table — no domain-specific UI needed.
 * When the tab declares an `editor` (and the server decided this caller may use it), the table
 * gains Add, per-row Edit (when a key field identifies records), and Delete with confirmation.
 * Below the sidebar's drawer breakpoint the same rows render as cards instead — the first column
 * as the card title, the next two visible, the rest behind a native details disclosure — so a
 * wide table never forces a phone to scroll sideways.
 */
function DataTable({
  endpoint,
  columns,
  editor,
  detailEndpoint,
  emptyText,
  rowActions,
}: {
  endpoint: string;
  columns: TabColumn[];
  editor?: TabEditor | null;
  detailEndpoint?: string | null;
  emptyText?: string | null;
  rowActions?: TabRowAction[];
}) {
  const qc = useQueryClient();
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["tab-data", endpoint],
    queryFn: () => apiGet<Record<string, unknown>[]>(endpoint),
  });
  // null = closed; {} = adding; a row = editing that row.
  const [editing, setEditing] = useState<Record<string, unknown> | null | "add">(null);
  const [deleting, setDeleting] = useState<Record<string, unknown> | null>(null);
  const [detailUrl, setDetailUrl] = useState<string | null>(null);
  const [actionMessage, setActionMessage] = useState<string | null>(null);
  const [confirmingRow, setConfirmingRow] = useState<{ action: TabRowAction; row: Record<string, unknown> } | null>(
    null,
  );
  const narrow = useMediaQuery(NARROW_QUERY);

  const remove = useMutation({
    mutationFn: (row: Record<string, unknown>) => apiSend(resolveRowUrl(editor!.deleteEndpoint!, row), "DELETE"),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["tab-data"] }),
  });

  // Per-row commands: POST to the {field}-resolved URL, surface the endpoint's message, refresh.
  const runRowAction = useMutation({
    mutationFn: ({ action, row }: { action: TabRowAction; row: Record<string, unknown> }) =>
      apiAction(resolveRowUrl(action.endpointTemplate, row)),
    onSuccess: (result) => {
      setActionMessage(result ?? "Done.");
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
    },
    onError: (error) => setActionMessage((error as Error).message),
  });

  if (isLoading) {
    return <p className="text-sm text-slate-500">Loading…</p>;
  }
  if (isError) {
    return <p className="text-sm text-red-600">{(error as Error).message}</p>;
  }

  if (detailUrl) {
    return <DetailView endpoint={detailUrl} onBack={() => setDetailUrl(null)} />;
  }

  const rows = data ?? [];
  // Fall back to the row's own fields (minus id) if the tab declared no columns.
  const cols: TabColumn[] =
    columns.length > 0
      ? columns
      : Object.keys(rows[0] ?? {})
          .filter((k) => k !== "id")
          .map((k) => ({ field: k, header: k }));

  const canEdit = editor?.keyField != null;
  const canDelete = editor?.deleteEndpoint != null;
  const commands = rowActions ?? [];
  const hasRowActions = detailEndpoint != null || commands.length > 0 || (editor != null && (canEdit || canDelete));

  return (
    <div className="space-y-3">
      {editor && editing === null && (
        <button
          type="button"
          onClick={() => setEditing("add")}
          className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500"
        >
          Add
        </button>
      )}
      {editor && editing !== null && (
        <EditorForm editor={editor} initial={editing === "add" ? null : editing} onDone={() => setEditing(null)} />
      )}
      {remove.isError && <p className="text-xs text-red-600">{(remove.error as Error).message}</p>}
      {actionMessage && (
        <p
          className={`text-sm ${runRowAction.isError ? "text-red-600" : "text-slate-600 dark:text-slate-300"}`}
          data-testid="row-action-message"
        >
          {actionMessage}
        </p>
      )}

      {narrow ? (
        <ul className="space-y-2" data-testid="card-list">
          {rows.length === 0 && (
            <li className="rounded-lg border border-dashed border-slate-300 px-4 py-6 text-center text-sm text-slate-400 dark:border-slate-700">
              {emptyText ?? "No data yet."}
            </li>
          )}
          {rows.map((row, i) => {
            const [title, ...rest] = cols;
            const visible = rest.slice(0, 2);
            const overflow = rest.slice(2);
            return (
              <li
                key={i}
                data-testid="row-card"
                className="space-y-2 rounded-lg border border-slate-200 bg-white p-3 dark:border-slate-700 dark:bg-slate-900"
              >
                {title && (
                  <p className="text-sm font-medium text-slate-900 dark:text-slate-100">
                    <CellValue column={title} row={row} />
                  </p>
                )}
                {visible.map((c) => (
                  <p key={c.field} className="flex justify-between gap-4 text-sm">
                    <span className="text-slate-400 dark:text-slate-500">{c.header}</span>
                    <span className="min-w-0 truncate text-right text-slate-700 dark:text-slate-200">
                      <CellValue column={c} row={row} />
                    </span>
                  </p>
                ))}
                {overflow.length > 0 && (
                  <details className="text-sm">
                    <summary className="focus-ring cursor-pointer rounded text-xs font-medium text-slate-500 dark:text-slate-400">
                      More
                    </summary>
                    <div className="mt-1 space-y-1">
                      {overflow.map((c) => (
                        <p key={c.field} className="flex justify-between gap-4">
                          <span className="text-slate-400 dark:text-slate-500">{c.header}</span>
                          <span className="min-w-0 truncate text-right text-slate-700 dark:text-slate-200">
                            <CellValue column={c} row={row} />
                          </span>
                        </p>
                      ))}
                    </div>
                  </details>
                )}
                {hasRowActions && (
                  <RowButtons
                    row={row}
                    commands={commands}
                    detailEndpoint={detailEndpoint}
                    canEdit={canEdit}
                    canDelete={canDelete}
                    pending={runRowAction.isPending}
                    onCommand={(action, r) =>
                      action.confirm ? setConfirmingRow({ action, row: r }) : runRowAction.mutate({ action, row: r })
                    }
                    onView={setDetailUrl}
                    onEdit={setEditing}
                    onDelete={setDeleting}
                  />
                )}
              </li>
            );
          })}
        </ul>
      ) : (
        <div className="overflow-hidden rounded-lg border border-slate-200 dark:border-slate-700">
          <table className="w-full text-left text-sm">
            <thead className="bg-slate-50 text-slate-500 dark:bg-slate-800 dark:text-slate-400">
              <tr>
                {cols.map((c) => (
                  <th key={c.field} className="px-4 py-2 font-medium">
                    {c.header}
                  </th>
                ))}
                {hasRowActions && <th className="px-4 py-2" />}
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100 dark:divide-slate-800">
              {rows.length === 0 && (
                <tr>
                  <td colSpan={cols.length + (hasRowActions ? 1 : 0)} className="px-4 py-6 text-center text-slate-400">
                    {emptyText ?? "No data yet."}
                  </td>
                </tr>
              )}
              {rows.map((row, i) => (
                <tr key={i}>
                  {cols.map((c) => (
                    <td key={c.field} className="px-4 py-2 text-slate-700 dark:text-slate-200">
                      <CellValue column={c} row={row} />
                    </td>
                  ))}
                  {hasRowActions && (
                    <td className="px-4 py-2 text-right">
                      <RowButtons
                        row={row}
                        commands={commands}
                        detailEndpoint={detailEndpoint}
                        canEdit={canEdit}
                        canDelete={canDelete}
                        pending={runRowAction.isPending}
                        onCommand={(action, r) =>
                          action.confirm ? setConfirmingRow({ action, row: r }) : runRowAction.mutate({ action, row: r })
                        }
                        onView={setDetailUrl}
                        onEdit={setEditing}
                        onDelete={setDeleting}
                      />
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <ConfirmDialog
        open={confirmingRow !== null}
        title={confirmingRow?.action.label ?? ""}
        body={confirmingRow?.action.confirm ?? ""}
        confirmLabel={confirmingRow?.action.label ?? "Confirm"}
        onConfirm={() => {
          if (confirmingRow) runRowAction.mutate(confirmingRow);
          setConfirmingRow(null);
        }}
        onCancel={() => setConfirmingRow(null)}
      />

      <ConfirmDialog
        open={deleting !== null}
        title="Delete row"
        body="Delete this entry? This cannot be undone."
        confirmLabel="Delete"
        tone="danger"
        onConfirm={() => {
          if (deleting) remove.mutate(deleting);
          setDeleting(null);
        }}
        onCancel={() => setDeleting(null)}
      />
    </div>
  );
}

/**
 * Tab-level command buttons: POST (empty body), surface the endpoint's message, refresh the tab
 * data. Consequential actions declare `confirm` and get a dialog first. The server only sends
 * actions the caller may invoke; the endpoints stay authorization-gated regardless.
 */
function ActionBar({ actions }: { actions: TabAction[] }) {
  const qc = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<TabAction | null>(null);

  const run = useMutation({
    mutationFn: (action: TabAction) => apiAction(action.endpoint),
    onSuccess: (result) => {
      setMessage(result ?? "Done.");
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
    },
    onError: (error) => setMessage((error as Error).message),
  });

  return (
    <div className="mb-3 space-y-2">
      <div className="flex flex-wrap gap-2">
        {actions.map((action) => (
          <button
            key={action.id}
            type="button"
            disabled={run.isPending}
            onClick={() => (action.confirm ? setConfirming(action) : run.mutate(action))}
            className="focus-ring rounded bg-brand-600 px-3 py-1.5 text-sm font-medium text-white hover:bg-brand-500 disabled:opacity-40"
          >
            {run.isPending ? "Working…" : action.label}
          </button>
        ))}
      </div>
      {message && (
        <p className={`text-sm ${run.isError ? "text-red-600" : "text-slate-600 dark:text-slate-300"}`} data-testid="action-message">
          {message}
        </p>
      )}

      <ConfirmDialog
        open={confirming !== null}
        title={confirming?.label ?? ""}
        body={confirming?.confirm ?? ""}
        confirmLabel={confirming?.label ?? "Confirm"}
        onConfirm={() => {
          if (confirming) run.mutate(confirming);
          setConfirming(null);
        }}
        onCancel={() => setConfirming(null)}
      />
    </div>
  );
}

/**
 * Server-driven tab content. If the tab declares a `dataEndpoint`, its data renders as a table —
 * or as a chart (time-series line, donut, or grouped bars per `chart.kind`) when the tab
 * declares `chart`. Otherwise the consuming app may supply content as children, or a placeholder
 * is shown. The base library has no knowledge of any particular vertical.
 */
export function GenericTab({ tab, children }: GenericTabProps) {
  return (
    <section>
      <h1 className="mb-4 text-xl font-semibold text-slate-900 dark:text-slate-100">{tab.label}</h1>

      {(tab.actions?.length ?? 0) > 0 && <ActionBar actions={tab.actions!} />}

      {children ??
        (tab.dataEndpoint && tab.chart ? (
          <TabChartView endpoint={tab.dataEndpoint} spec={tab.chart} />
        ) : tab.dataEndpoint ? (
          <DataTable
            endpoint={tab.dataEndpoint}
            columns={tab.columns ?? []}
            editor={tab.editor}
            detailEndpoint={tab.detailEndpoint}
            emptyText={tab.placeholder}
            rowActions={tab.rowActions}
          />
        ) : (
          <div className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
            {tab.placeholder ?? "Nothing to show here yet."}
          </div>
        ))}
    </section>
  );
}
