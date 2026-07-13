import type { ReactNode } from "react";
import { usePermission } from "../hooks/usePermission";

interface PermissionGateProps {
  /** Permission the user must hold for `children` to render (wildcards honoured). */
  permission: string;
  children: ReactNode;
  /** Rendered when the user lacks the permission. Defaults to nothing. */
  fallback?: ReactNode;
}

/**
 * Renders `children` only when the signed-in user holds `permission`. A UI convenience mirroring the
 * server's RBAC — the API still enforces access, so this shapes what's shown, it never grants anything.
 *
 * @example
 * <PermissionGate permission="tools.finance.record_transaction">
 *   <button onClick={record}>Record transaction</button>
 * </PermissionGate>
 */
export function PermissionGate({ permission, children, fallback = null }: PermissionGateProps) {
  return usePermission(permission) ? <>{children}</> : <>{fallback}</>;
}
