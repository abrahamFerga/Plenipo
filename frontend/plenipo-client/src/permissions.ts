/**
 * Client-side mirror of the server's PermissionMatcher: a permission is granted if the set holds the
 * global wildcard `*`, the exact string, or a dotted-prefix wildcard (e.g. `chat.*` grants
 * `chat.approvals.manage`). Used only to shape the UI — the API enforces the real check.
 */
export function hasPermission(granted: readonly string[], required: string): boolean {
  const set = new Set(granted);
  if (set.has("*") || set.has(required)) {
    return true;
  }

  let dot = required.lastIndexOf(".");
  while (dot > 0) {
    if (set.has(`${required.slice(0, dot + 1)}*`)) {
      return true;
    }
    dot = required.lastIndexOf(".", dot - 1);
  }
  return false;
}
