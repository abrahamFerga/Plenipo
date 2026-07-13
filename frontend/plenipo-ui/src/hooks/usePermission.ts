import { hasPermission } from "../lib/permissions";
import { useMe } from "./useMe";

/**
 * Whether the signed-in user holds `permission` (segment-boundary wildcards honoured, mirroring the
 * server's matcher). Returns `false` until `/me` has loaded and whenever denied.
 *
 * This shapes the UI only — the API enforces the real check, so gating on this never grants access,
 * it just avoids showing controls that would fail. For loading/error state, use {@link useMe} directly.
 */
export function usePermission(permission: string): boolean {
  const { data: me } = useMe();
  return hasPermission(me?.permissions ?? [], permission);
}
