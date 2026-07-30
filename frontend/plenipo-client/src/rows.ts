import type { TabColumn } from "./api";

/**
 * Reading a tab's rows: the small pieces of behaviour that a `TabDescriptor` implies and that
 * every renderer must implement identically. A `{field}` placeholder has to resolve the same way
 * on a phone as in a browser, or the same manifest addresses two different records.
 */

/**
 * Substitute every `{field}` placeholder in an endpoint template from a row's values — the
 * contract behind `TabRowAction.endpointTemplate`, `TabEditor.deleteEndpoint`, and
 * `TabDescriptor.detailEndpoint`. Values are URL-encoded, and a field the row doesn't carry
 * resolves to empty rather than leaving a literal `{id}` in the request path.
 */
export function resolveRowUrl(template: string, row: Record<string, unknown>): string {
  return template.replace(/\{(\w+)\}/g, (_, field: string) => encodeURIComponent(String(row[field] ?? "")));
}

/**
 * The columns to show for a tab, falling back to the row's own fields when the manifest declared
 * none. `id` is dropped from that fallback: it is how the endpoints address a record, not
 * something a reader asked to see.
 */
export function effectiveColumns(
  declared: TabColumn[] | undefined,
  rows: Record<string, unknown>[],
): TabColumn[] {
  if (declared && declared.length > 0) return declared;
  return Object.keys(rows[0] ?? {})
    .filter((key) => key !== "id")
    .map((key) => ({ field: key, header: key }));
}

/**
 * Mask all but the last four characters — enough to recognise your own account, not enough to
 * read it off a screen. The display-side companion of a column's `masked` flag.
 */
export function maskValue(text: string): string {
  return text.length > 4 ? `••••${text.slice(-4)}` : "••••";
}
