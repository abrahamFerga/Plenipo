import { useEffect, useState } from "react";
import {
  BackHandler,
  KeyboardAvoidingView,
  Modal,
  Platform,
  Pressable,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from "react-native";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  apiAction,
  apiGet,
  apiSend,
  effectiveColumns,
  maskValue,
  resolveFieldDefault,
  resolveFieldDefaults,
  resolveRowUrl,
  type ModuleTab,
  type TabAction,
  type TabColumn,
  type TabDetailAction,
  type TabDetailDocument,
  type TabEditor,
  type TabEditorField,
  type TabRowAction,
} from "@plenipo/client";
import { radius, space, type, useResolvedTheme } from "../theme";
import { Button, Card, ConfirmDialog, EmptyState, ErrorNote, Loading, OutcomeNote } from "./ui";
import { FieldInput } from "./FieldInput";
import { TabChartView } from "./TabChart";

/**
 * The server-driven tab, rendered natively.
 *
 * This is the mobile counterpart of `@plenipo/ui`'s GenericTab and it makes the same decisions
 * from the same descriptor: a `chart` draws, a `singleton` becomes a labelled form, a
 * `dataEndpoint` becomes a list, and everything else is a placeholder the manifest words. Add a
 * tab to a C# `ModuleManifest` and it appears here — no React Native written, no release shipped.
 *
 * Where it differs from the web it is because the device differs, never because the contract
 * does: rows are always cards (a phone has no room for a wide table), forms open as sheets over
 * the list rather than inline, and every affordance is a 44pt target.
 *
 * The security posture is unchanged and unchangeable from here: the manifest arrives already
 * filtered by the caller's permissions, so an editor or action that isn't in the payload has no
 * button — and the endpoints stay authorization-gated regardless of what this renders.
 */

interface GenericTabProps {
  tab: ModuleTab;
}

export function GenericTab({ tab }: GenericTabProps) {
  const hasActions = (tab.actions?.length ?? 0) > 0;

  if (tab.dataEndpoint != null && tab.chart != null) {
    return (
      <ScrollView contentContainerStyle={styles.page}>
        {hasActions && <ActionBar actions={tab.actions!} />}
        <TabChartView endpoint={tab.dataEndpoint} spec={tab.chart} />
      </ScrollView>
    );
  }

  if (tab.dataEndpoint != null && tab.singleton === true) {
    return (
      <ScrollView contentContainerStyle={styles.page} keyboardShouldPersistTaps="handled">
        {hasActions && <ActionBar actions={tab.actions!} />}
        <SingletonForm endpoint={tab.dataEndpoint} columns={tab.columns ?? []} editor={tab.editor} />
      </ScrollView>
    );
  }

  if (tab.dataEndpoint != null) {
    return (
      <DataList
        endpoint={tab.dataEndpoint}
        columns={tab.columns ?? []}
        editor={tab.editor}
        detailEndpoint={tab.detailEndpoint}
        emptyText={tab.placeholder}
        rowActions={tab.rowActions}
        actions={tab.actions ?? []}
      />
    );
  }

  return (
    <ScrollView contentContainerStyle={styles.page}>
      {hasActions && <ActionBar actions={tab.actions!} />}
      <EmptyState text={tab.placeholder ?? "Nothing to show here yet."} />
    </ScrollView>
  );
}

/* ── cells ───────────────────────────────────────────────────────────────── */

/**
 * One value. A column declaring `masked` renders behind a reveal toggle — per cell, per mount,
 * never remembered. That is screen privacy, not access control: the caller was already authorized
 * to read this, it just shouldn't sit exposed on a phone someone else can see.
 */
function CellValue({ column, row }: { column: TabColumn; row: Record<string, unknown> }) {
  const t = useResolvedTheme();
  const [revealed, setRevealed] = useState(false);
  const raw = row[column.field];
  const text = raw == null ? "" : String(raw);

  if (column.masked !== true || text === "") {
    return <Text style={{ ...type.body, color: t.text }}>{text}</Text>;
  }

  return (
    <Pressable
      accessibilityRole="button"
      accessibilityState={{ selected: revealed }}
      accessibilityLabel={`${revealed ? "Hide" : "Reveal"} ${column.header}`}
      onPress={() => setRevealed((v) => !v)}
      hitSlop={8}
    >
      <Text style={{ ...type.body, color: t.text }}>{revealed ? text : maskValue(text)}</Text>
    </Pressable>
  );
}

/* ── list ────────────────────────────────────────────────────────────────── */

function DataList({
  endpoint,
  columns,
  editor,
  detailEndpoint,
  emptyText,
  rowActions,
  actions,
}: {
  endpoint: string;
  columns: TabColumn[];
  editor?: TabEditor | null;
  detailEndpoint?: string | null;
  emptyText?: string | null;
  rowActions?: TabRowAction[];
  actions: TabAction[];
}) {
  const qc = useQueryClient();
  const { data, isLoading, isError, error, refetch, isRefetching } = useQuery({
    queryKey: ["tab-data", endpoint],
    queryFn: () => apiGet<Record<string, unknown>[]>(endpoint),
  });

  // null = closed; "add" = a blank form; a row = editing that row.
  const [editing, setEditing] = useState<Record<string, unknown> | null | "add">(null);
  const [deleting, setDeleting] = useState<Record<string, unknown> | null>(null);
  const [detailUrl, setDetailUrl] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<{ action: TabRowAction; row: Record<string, unknown> } | null>(null);

  const remove = useMutation({
    mutationFn: (row: Record<string, unknown>) => apiSend(resolveRowUrl(editor!.deleteEndpoint!, row), "DELETE"),
    onSuccess: () => void qc.invalidateQueries({ queryKey: ["tab-data"] }),
    onError: (e) => setOutcome((e as Error).message),
  });

  const runRowAction = useMutation({
    mutationFn: ({ action, row }: { action: TabRowAction; row: Record<string, unknown> }) =>
      apiAction(resolveRowUrl(action.endpointTemplate, row)),
    onSuccess: (result) => {
      setOutcome(result ?? "Done.");
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
    },
    onError: (e) => setOutcome((e as Error).message),
  });

  // Android's hardware back must close the drill-down, not leave the tab. Without this the detail
  // view is a state the OS doesn't know about and back would pop the whole screen.
  useEffect(() => {
    if (detailUrl === null) return undefined;
    const sub = BackHandler.addEventListener("hardwareBackPress", () => {
      setDetailUrl(null);
      return true;
    });
    return () => sub.remove();
  }, [detailUrl]);

  if (isLoading) return <Loading />;
  if (isError) {
    return (
      <ScrollView contentContainerStyle={styles.page}>
        <ErrorNote error={error} />
      </ScrollView>
    );
  }
  if (detailUrl !== null) {
    return <DetailView endpoint={detailUrl} onBack={() => setDetailUrl(null)} />;
  }

  const rows = data ?? [];
  const cols = effectiveColumns(columns, rows);
  const canEdit = editor?.keyField != null;
  const canDelete = editor?.deleteEndpoint != null;
  const commands = rowActions ?? [];

  return (
    <>
      <ScrollView
        contentContainerStyle={styles.page}
        refreshControl={<RefreshControl refreshing={isRefetching} onRefresh={() => void refetch()} />}
      >
        {actions.length > 0 && <ActionBar actions={actions} />}

        {editor != null && (
          <Button label="Add" tone="primary" onPress={() => setEditing("add")} style={{ marginBottom: space.md }} />
        )}

        {outcome != null && (
          <View style={{ marginBottom: space.md }}>
            <OutcomeNote message={outcome} tone={runRowAction.isError || remove.isError ? "error" : "neutral"} />
          </View>
        )}

        {rows.length === 0 ? (
          <EmptyState text={emptyText ?? "No data yet."} />
        ) : (
          rows.map((row, i) => (
            <RowCard
              key={i}
              row={row}
              columns={cols}
              commands={commands}
              detailEndpoint={detailEndpoint}
              canEdit={canEdit}
              canDelete={canDelete}
              pending={runRowAction.isPending}
              onCommand={(action, r) =>
                action.confirm != null
                  ? setConfirming({ action, row: r })
                  : runRowAction.mutate({ action, row: r })
              }
              onView={setDetailUrl}
              onEdit={setEditing}
              onDelete={setDeleting}
            />
          ))
        )}
      </ScrollView>

      {editor != null && editing !== null && (
        <EditorForm
          editor={editor}
          initial={editing === "add" ? null : editing}
          onDone={() => setEditing(null)}
        />
      )}

      <ConfirmDialog
        open={confirming !== null}
        title={confirming?.action.label ?? ""}
        body={confirming?.action.confirm ?? ""}
        confirmLabel={confirming?.action.label ?? "Confirm"}
        onConfirm={() => {
          if (confirming) runRowAction.mutate(confirming);
          setConfirming(null);
        }}
        onCancel={() => setConfirming(null)}
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
    </>
  );
}

/**
 * One row as a card: the first column is the title, the next two are labelled pairs, and the rest
 * hide behind a "More" toggle. Same disclosure the web shell uses below its breakpoint — a wide
 * table must never make a phone scroll sideways.
 */
function RowCard({
  row,
  columns,
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
  columns: TabColumn[];
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
  const t = useResolvedTheme();
  const [expanded, setExpanded] = useState(false);
  const [title, ...rest] = columns;
  const visible = rest.slice(0, 2);
  const overflow = rest.slice(2);
  const hasRowActions = detailEndpoint != null || commands.length > 0 || canEdit || canDelete;

  return (
    <Card style={{ marginBottom: space.sm }}>
      {title != null && (
        <View style={{ marginBottom: space.xs }}>
          <Text style={{ ...type.heading, color: t.text }}>
            <CellValue column={title} row={row} />
          </Text>
        </View>
      )}

      {visible.map((c) => (
        <FieldRow key={c.field} column={c} row={row} />
      ))}

      {overflow.length > 0 && (
        <>
          <Pressable
            accessibilityRole="button"
            accessibilityState={{ expanded }}
            onPress={() => setExpanded((v) => !v)}
            hitSlop={8}
            style={{ paddingVertical: space.xs }}
          >
            <Text style={{ ...type.label, color: t.brandText }}>{expanded ? "Less" : "More"}</Text>
          </Pressable>
          {expanded && overflow.map((c) => <FieldRow key={c.field} column={c} row={row} />)}
        </>
      )}

      {hasRowActions && (
        <View style={styles.rowActions}>
          {commands.map((action) => (
            <Button
              key={action.id}
              label={action.label}
              tone="primary"
              disabled={pending}
              onPress={() => onCommand(action, row)}
            />
          ))}
          {detailEndpoint != null && (
            <Button label="View" onPress={() => onView(resolveRowUrl(detailEndpoint, row))} />
          )}
          {canEdit && <Button label="Edit" onPress={() => onEdit(row)} />}
          {canDelete && <Button label="Delete" tone="danger" onPress={() => onDelete(row)} />}
        </View>
      )}
    </Card>
  );
}

function FieldRow({ column, row }: { column: TabColumn; row: Record<string, unknown> }) {
  const t = useResolvedTheme();
  return (
    <View style={styles.fieldRow}>
      <Text style={{ ...type.caption, color: t.textMuted }}>{column.header}</Text>
      <View style={styles.fieldValue}>
        <CellValue column={column} row={row} />
      </View>
    </View>
  );
}

/* ── drill-down ──────────────────────────────────────────────────────────── */

/** The generic detail document: prose sections and sub-tables, composed by the endpoint. */
function DetailView({ endpoint, onBack }: { endpoint: string; onBack: () => void }) {
  const t = useResolvedTheme();
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["tab-detail", endpoint],
    queryFn: () => apiGet<TabDetailDocument>(endpoint),
  });

  // Back renders in every state: after an action discards the record the refetch legitimately
  // 404s, and an error screen with no way out would strand the user.
  const back = <Button label="← Back" onPress={onBack} style={{ alignSelf: "flex-start" }} />;

  if (isLoading) {
    return (
      <ScrollView contentContainerStyle={styles.page}>
        {back}
        <Loading />
      </ScrollView>
    );
  }
  if (isError) {
    return (
      <ScrollView contentContainerStyle={styles.page}>
        {back}
        <View style={{ marginTop: space.md }}>
          <ErrorNote error={error} />
        </View>
      </ScrollView>
    );
  }

  const doc = data!;
  return (
    <ScrollView contentContainerStyle={styles.page}>
      {back}
      <Text style={{ ...type.title, color: t.text, marginTop: space.md }}>{doc.title}</Text>
      {doc.subtitle != null && (
        <Text style={{ ...type.body, color: t.textMuted, marginTop: space.xs }}>{doc.subtitle}</Text>
      )}

      <DetailActions actions={doc.actions ?? []} />

      {doc.sections.map((section) => (
        <View key={section.heading} style={{ marginTop: space.lg }}>
          <Text style={{ ...type.label, color: t.textMuted, textTransform: "uppercase" }}>
            {section.heading}
          </Text>
          {section.text != null && (
            <Text style={{ ...type.body, color: t.text, marginTop: space.sm }}>{section.text}</Text>
          )}
          {section.rows != null &&
            (section.rows.length === 0 ? (
              <Text style={{ ...type.body, color: t.textMuted, marginTop: space.sm }}>None.</Text>
            ) : (
              <View style={{ marginTop: space.sm }}>
                {section.rows.map((row, i) => (
                  <Card key={i} style={{ marginBottom: space.xs }}>
                    {(section.columns ?? []).map((c) => (
                      <FieldRow key={c.field} column={c} row={row} />
                    ))}
                  </Card>
                ))}
              </View>
            ))}
        </View>
      ))}
    </ScrollView>
  );
}

/**
 * Commands on the record a detail document describes. The server sends only what is applicable
 * and permitted; running one refreshes the document and the list behind it, and the response is
 * shown — an action that refuses must never look like nothing happened.
 */
function DetailActions({ actions }: { actions: TabDetailAction[] }) {
  const qc = useQueryClient();
  const [message, setMessage] = useState<string | null>(null);
  const [confirming, setConfirming] = useState<TabDetailAction | null>(null);
  const [values, setValues] = useState<Record<string, string>>({});

  const run = useMutation({
    mutationFn: (action: TabDetailAction) =>
      apiAction(action.endpoint, action.field ? { [action.field.field]: values[action.id] ?? "" } : undefined),
    onSuccess: (result) => {
      setMessage(result ?? "Done.");
      void qc.invalidateQueries({ queryKey: ["tab-detail"] });
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
    },
    onError: (e) => setMessage((e as Error).message),
  });

  // Rendered even with zero actions so the LAST message survives the refetch that empties the
  // list — a terminal action (approve, close) succeeds into "nothing left to do", and its
  // success message vanishing with the buttons would un-say what just happened.
  if (actions.length === 0 && message === null) return null;

  return (
    <View style={{ marginTop: space.md, gap: space.sm }}>
      {actions.map((action) => (
        <View key={action.id} style={{ gap: space.sm }}>
          {action.field != null && (
            <FieldInput
              field={action.field}
              value={values[action.id] ?? ""}
              disabled={run.isPending}
              onChange={(value) => setValues((v) => ({ ...v, [action.id]: value }))}
            />
          )}
          <Button
            label={action.label}
            tone="primary"
            busy={run.isPending}
            disabled={action.field != null && (values[action.id] ?? "").trim() === ""}
            onPress={() => (action.confirm != null ? setConfirming(action) : run.mutate(action))}
          />
        </View>
      ))}

      {message != null && <OutcomeNote message={message} tone={run.isError ? "error" : "neutral"} />}

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
    </View>
  );
}

/* ── editors ─────────────────────────────────────────────────────────────── */

/**
 * The POST body for a server-declared form. Numeric fields post as JSON numbers so endpoints
 * binding decimal/int work unshimmed, and empty fields are OMITTED rather than sent as "" —
 * nullable value types reject "", and `Number("")` is 0, which would silently write a zero.
 */
function formBody(editor: TabEditor, values: Record<string, string>): Record<string, unknown> {
  return Object.fromEntries(
    editor.fields
      .filter((f) => (values[f.field] ?? "").trim() !== "")
      .map((f) => [f.field, f.numeric === true ? Number(values[f.field]) : values[f.field]]),
  );
}

/** A numeric field holding something that isn't a number — the one validation the shell can do. */
function invalidNumeric(fields: TabEditorField[], values: Record<string, string>): boolean {
  return fields.some(
    (f) => f.numeric === true && (values[f.field] ?? "").trim() !== "" && Number.isNaN(Number(values[f.field])),
  );
}

/** Add / edit, as a sheet over the list. The key field is locked while editing — it is identity. */
function EditorForm({
  editor,
  initial,
  onDone,
}: {
  editor: TabEditor;
  initial: Record<string, unknown> | null;
  onDone: () => void;
}) {
  const t = useResolvedTheme();
  const qc = useQueryClient();
  const [values, setValues] = useState<Record<string, string>>(() => {
    // Declared defaults are for a BLANK form only. Editing shows the record as it actually is —
    // quietly filling an empty field with a default would edit data the user never touched.
    const defaults = initial === null ? resolveFieldDefaults(editor.fields) : {};
    return Object.fromEntries(
      editor.fields.map((f) => [
        f.field,
        initial?.[f.field] == null ? (defaults[f.field] ?? "") : String(initial[f.field]),
      ]),
    );
  });

  const save = useMutation({
    mutationFn: () => apiSend(editor.upsertEndpoint, "POST", formBody(editor, values)),
    onSuccess: () => {
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
      onDone();
    },
  });

  const missing =
    editor.fields.some((f) => (f.required ?? true) && (values[f.field] ?? "").trim() === "") ||
    invalidNumeric(editor.fields, values);

  return (
    <Modal transparent animationType="slide" visible onRequestClose={onDone}>
      <KeyboardAvoidingView
        behavior={Platform.OS === "ios" ? "padding" : undefined}
        style={styles.formBackdrop}
      >
        <View style={[styles.formSheet, { backgroundColor: t.surface, borderColor: t.border }]}>
          <Text style={{ ...type.heading, color: t.text, marginBottom: space.md }}>
            {initial === null ? "Add" : "Edit"}
          </Text>

          <ScrollView keyboardShouldPersistTaps="handled">
            {editor.fields.map((f) => (
              <View key={f.field} style={{ marginBottom: space.md }}>
                <Text style={{ ...type.label, color: t.text, marginBottom: space.xs }}>{f.label}</Text>
                <FieldInput
                  field={f}
                  value={values[f.field] ?? ""}
                  disabled={initial !== null && editor.keyField === f.field}
                  onChange={(value) => setValues((v) => ({ ...v, [f.field]: value }))}
                />
              </View>
            ))}
            {save.isError && <OutcomeNote message={(save.error as Error).message} tone="error" />}
          </ScrollView>

          <View style={styles.formActions}>
            <Button label="Cancel" onPress={onDone} />
            <Button
              label="Save"
              tone="primary"
              busy={save.isPending}
              disabled={missing}
              onPress={() => save.mutate()}
            />
          </View>
        </View>
      </KeyboardAvoidingView>
    </Modal>
  );
}

/** Fields in declaration order, split into (heading, fields) sections by `group`; ungrouped first. */
function groupFields(fields: TabEditorField[]): { heading: string | null; fields: TabEditorField[] }[] {
  const sections: { heading: string | null; fields: TabEditorField[] }[] = [];
  for (const f of fields) {
    const heading = f.group ?? null;
    const last = sections[sections.length - 1];
    if (last && last.heading === heading) last.fields.push(f);
    else sections.push({ heading, fields: [f] });
  }
  return sections;
}

/**
 * A tab whose `dataEndpoint` is ONE config object — a labelled form, not a list with an Add
 * button that never applied to a single row. With an editor the config is editable and saved as a
 * whole; without one it reads read-only, labelled by `columns`.
 */
function SingletonForm({
  endpoint,
  columns,
  editor,
}: {
  endpoint: string;
  columns: TabColumn[];
  editor?: TabEditor | null;
}) {
  const t = useResolvedTheme();
  const { data, isLoading, isError, error } = useQuery({
    queryKey: ["tab-data", endpoint],
    queryFn: () => apiGet<Record<string, unknown>[]>(endpoint),
  });

  if (isLoading) return <Loading />;
  if (isError) return <ErrorNote error={error} />;

  const row = data?.[0] ?? {};

  if (editor != null) {
    // Remount when the fetched row changes so the form re-seeds from fresh data after a save.
    return <SingletonEditor key={JSON.stringify(row)} row={row} editor={editor} />;
  }

  return (
    <View>
      {columns.map((c) => (
        <View key={c.field} style={[styles.readOnlyRow, { borderColor: t.border }]}>
          <Text style={{ ...type.caption, color: t.textMuted }}>{c.header}</Text>
          <Text style={{ ...type.body, color: t.text, marginTop: 2 }}>
            {row[c.field] == null || row[c.field] === "" ? "—" : String(row[c.field])}
          </Text>
        </View>
      ))}
    </View>
  );
}

function SingletonEditor({ row, editor }: { row: Record<string, unknown>; editor: TabEditor }) {
  const t = useResolvedTheme();
  const qc = useQueryClient();
  const [values, setValues] = useState<Record<string, string>>(() =>
    Object.fromEntries(
      editor.fields.map((f) => {
        const stored = row[f.field];
        return [f.field, stored == null || stored === "" ? resolveFieldDefault(f) : String(stored)];
      }),
    ),
  );
  const [saved, setSaved] = useState(false);

  const save = useMutation({
    mutationFn: () => apiSend(editor.upsertEndpoint, "POST", formBody(editor, values)),
    onSuccess: () => {
      setSaved(true);
      void qc.invalidateQueries({ queryKey: ["tab-data"] });
    },
  });

  const invalid = invalidNumeric(editor.fields, values);

  return (
    <View>
      {groupFields(editor.fields).map((section, i) => (
        <View key={section.heading ?? `_${i}`} style={{ marginBottom: space.lg }}>
          {section.heading != null && (
            <Text
              style={{
                ...type.label,
                color: t.textMuted,
                borderBottomWidth: StyleSheet.hairlineWidth,
                borderColor: t.border,
                paddingBottom: space.xs,
                marginBottom: space.md,
              }}
            >
              {section.heading}
            </Text>
          )}
          {section.fields.map((f) => (
            <View key={f.field} style={{ marginBottom: space.md }}>
              <Text style={{ ...type.label, color: t.text, marginBottom: space.xs }}>{f.label}</Text>
              <FieldInput
                field={f}
                value={values[f.field] ?? ""}
                onChange={(value) => {
                  setValues((v) => ({ ...v, [f.field]: value }));
                  setSaved(false); // a fresh edit clears the "Saved" from the previous one
                }}
              />
            </View>
          ))}
        </View>
      ))}

      {save.isError && <OutcomeNote message={(save.error as Error).message} tone="error" />}

      <View style={{ flexDirection: "row", alignItems: "center", gap: space.md }}>
        <Button
          label="Save changes"
          tone="primary"
          busy={save.isPending}
          disabled={invalid}
          onPress={() => save.mutate()}
        />
        {saved && !save.isPending && (
          <Text style={{ ...type.body, color: t.success }}>Saved ✓</Text>
        )}
      </View>
    </View>
  );
}

/* ── tab-level actions ───────────────────────────────────────────────────── */

/**
 * Tab-level commands: POST, show what the endpoint said, refresh. Consequential ones declare a
 * `confirm` string and get a dialog first. The server only sends actions the caller may invoke.
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
    onError: (e) => setMessage((e as Error).message),
  });

  return (
    <View style={{ marginBottom: space.md, gap: space.sm }}>
      <View style={styles.rowActions}>
        {actions.map((action) => (
          <Button
            key={action.id}
            label={action.label}
            tone="primary"
            busy={run.isPending}
            onPress={() => (action.confirm != null ? setConfirming(action) : run.mutate(action))}
          />
        ))}
      </View>

      {message != null && <OutcomeNote message={message} tone={run.isError ? "error" : "neutral"} />}

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
    </View>
  );
}

const styles = StyleSheet.create({
  page: { padding: space.lg, paddingBottom: space.xl * 2 },
  fieldRow: {
    flexDirection: "row",
    justifyContent: "space-between",
    alignItems: "flex-start",
    gap: space.md,
    paddingVertical: 2,
  },
  fieldValue: { flexShrink: 1, alignItems: "flex-end" },
  rowActions: { flexDirection: "row", flexWrap: "wrap", gap: space.sm, marginTop: space.sm },
  readOnlyRow: { paddingVertical: space.md, borderBottomWidth: StyleSheet.hairlineWidth },
  formBackdrop: { flex: 1, backgroundColor: "rgba(15,23,42,0.55)", justifyContent: "flex-end" },
  formSheet: {
    borderTopLeftRadius: radius.lg,
    borderTopRightRadius: radius.lg,
    borderWidth: 1,
    padding: space.lg,
    maxHeight: "85%",
  },
  formActions: { flexDirection: "row", gap: space.sm, justifyContent: "flex-end", marginTop: space.md },
});
