import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  apiGet,
  apiSend,
  type ModuleTab,
  type TabColumn,
  type TabDetailDocument,
  type TabEditor,
} from "../lib/api";
import { ConfirmDialog } from "./ConfirmDialog";

interface GenericTabProps {
  tab: ModuleTab;
  children?: React.ReactNode;
}

const inputClass =
  "w-full rounded border border-slate-300 bg-white px-2 py-1 text-sm focus:border-brand-500 focus:outline-none focus:ring-1 focus:ring-brand-500 disabled:opacity-60 dark:border-slate-600 dark:bg-slate-800";

/** Substitute the `{field}` placeholder in an endpoint template from the row's values. */
function resolveRowUrl(template: string, row: Record<string, unknown>): string {
  return template.replace(/\{(\w+)\}/, (_, field: string) => encodeURIComponent(String(row[field] ?? "")));
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
          {f.multiline ? (
            <textarea
              id={`editor-${f.field}`}
              rows={4}
              value={values[f.field]}
              onChange={(e) => setValues((v) => ({ ...v, [f.field]: e.target.value }))}
              className={inputClass}
            />
          ) : (
            <input
              id={`editor-${f.field}`}
              type={f.numeric ? "number" : "text"}
              inputMode={f.numeric ? "decimal" : undefined}
              step={f.numeric ? "any" : undefined}
              value={values[f.field]}
              disabled={initial !== null && editor.keyField === f.field}
              onChange={(e) => setValues((v) => ({ ...v, [f.field]: e.target.value }))}
              className={inputClass}
            />
          )}
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

/**
 * Renders a tab's `dataEndpoint` (a JSON array) as a generic table — no domain-specific UI needed.
 * When the tab declares an `editor` (and the server decided this caller may use it), the table
 * gains Add, per-row Edit (when a key field identifies records), and Delete with confirmation.
 */
function DataTable({
  endpoint,
  columns,
  editor,
  detailEndpoint,
}: {
  endpoint: string;
  columns: TabColumn[];
  editor?: TabEditor | null;
  detailEndpoint?: string | null;
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

  const remove = useMutation({
    mutationFn: (row: Record<string, unknown>) => apiSend(resolveRowUrl(editor!.deleteEndpoint!, row), "DELETE"),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["tab-data"] }),
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
  const hasRowActions = detailEndpoint != null || (editor != null && (canEdit || canDelete));

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
                  No data yet.
                </td>
              </tr>
            )}
            {rows.map((row, i) => (
              <tr key={i}>
                {cols.map((c) => (
                  <td key={c.field} className="px-4 py-2 text-slate-700 dark:text-slate-200">
                    {row[c.field] == null ? "" : String(row[c.field])}
                  </td>
                ))}
                {hasRowActions && (
                  <td className="px-4 py-2 text-right">
                    <span className="inline-flex gap-2">
                      {detailEndpoint && (
                        <button
                          type="button"
                          onClick={() => setDetailUrl(resolveRowUrl(detailEndpoint, row))}
                          className="focus-ring rounded border border-brand-300 px-2 py-0.5 text-xs font-medium text-brand-700 dark:border-brand-800 dark:text-brand-300"
                        >
                          View
                        </button>
                      )}
                      {canEdit && (
                        <button
                          type="button"
                          onClick={() => setEditing(row)}
                          className="focus-ring rounded border border-slate-300 px-2 py-0.5 text-xs font-medium text-slate-600 dark:border-slate-600 dark:text-slate-300"
                        >
                          Edit
                        </button>
                      )}
                      {canDelete && (
                        <button
                          type="button"
                          onClick={() => setDeleting(row)}
                          className="focus-ring rounded border border-red-300 px-2 py-0.5 text-xs font-medium text-red-600 hover:bg-red-50 dark:border-red-800 dark:hover:bg-red-900/20"
                        >
                          Delete
                        </button>
                      )}
                    </span>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

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
 * Server-driven tab content. If the tab declares a `dataEndpoint`, its data renders as a table; otherwise
 * the consuming app may supply content as children, or a placeholder is shown. The base library has no
 * knowledge of any particular vertical.
 */
export function GenericTab({ tab, children }: GenericTabProps) {
  return (
    <section>
      <h1 className="mb-4 text-xl font-semibold text-slate-900 dark:text-slate-100">{tab.label}</h1>

      {children ??
        (tab.dataEndpoint ? (
          <DataTable
            endpoint={tab.dataEndpoint}
            columns={tab.columns ?? []}
            editor={tab.editor}
            detailEndpoint={tab.detailEndpoint}
          />
        ) : (
          <div className="rounded-lg border border-dashed border-slate-300 p-8 text-center text-sm text-slate-500 dark:border-slate-700 dark:text-slate-400">
            {tab.placeholder ?? "Nothing to show here yet."}
          </div>
        ))}
    </section>
  );
}
