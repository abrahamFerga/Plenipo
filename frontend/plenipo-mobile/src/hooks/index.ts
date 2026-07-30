import { useQuery, type UseQueryResult } from "@tanstack/react-query";
import { api, hasPermission, type Me, type Module, type PlatformInfo } from "@plenipo/client";

/**
 * The three reads the shell is built on, and the permission check that shapes what it offers.
 *
 * These mirror `@plenipo/ui`'s hooks by design — same query keys, same staleness, same semantics —
 * so behaviour a product learned on the web holds on mobile.
 */

/**
 * The permission-filtered module manifest. This single response is the whole navigation: which
 * modules exist, which tabs each has, which editors and actions the caller may use. Tabs the
 * caller can't open are absent, not hidden, so the client never has to make an access decision.
 */
export function useModules(): UseQueryResult<Module[], Error> {
  return useQuery({
    queryKey: ["modules"],
    queryFn: () => api.modules(),
    // Navigation shouldn't refetch on every screen focus, but it must pick up a newly granted
    // permission without a reinstall.
    staleTime: 60_000,
  });
}

/** The caller's identity and resolved permissions. */
export function useMe(): UseQueryResult<Me, Error> {
  return useQuery({ queryKey: ["me"], queryFn: () => api.me(), staleTime: 60_000 });
}

/** Deployment facts the shell uses to set expectations (demo mode, upload limit, model list). */
export function useInfo(): UseQueryResult<PlatformInfo, Error> {
  return useQuery({ queryKey: ["info"], queryFn: () => api.info(), staleTime: 300_000 });
}

/**
 * Whether the caller holds a permission — for shaping UI only. The server enforces the real check
 * on every endpoint, and the manifest already omits what the caller may not see; this is for the
 * cases where a screen has an affordance the manifest can't express.
 */
export function usePermission(permission: string): boolean {
  const { data } = useMe();
  return hasPermission(data?.permissions ?? [], permission);
}
