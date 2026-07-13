import { useQuery } from "@tanstack/react-query";
import { api } from "@abrahamferga/cortex-ui";

/**
 * Module-contributed admin pages (ModuleManifest.AdminTabs): the server declares them, the admin
 * console renders them with the same generic tab machinery the domain shell uses, so a product
 * adds an admin page without forking this app. The list is permission-filtered server-side; a
 * caller who can't see a page never receives it.
 */
export function useAdminExtensions() {
  return useQuery({ queryKey: ["admin", "extensions"], queryFn: api.admin.extensions });
}
