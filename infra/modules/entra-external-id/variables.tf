# =============================================================================
# modules/entra-external-id/variables.tf
# =============================================================================

variable "name_prefix" {
  description = "Naming prefix, e.g. plenipo-dev."
  type        = string
}

variable "environment" {
  description = "Deployment environment (dev/staging/prod); applied as an app tag."
  type        = string
}

variable "spa_redirect_uris" {
  description = "Redirect URIs registered on the SPA (single-page application) platform."
  type        = list(string)
  default     = ["http://localhost:5173"]
}

variable "sign_in_audience" {
  description = <<-EOT
    Token audience for the app registrations. For an Entra External ID (CIAM)
    tenant the apps live in that tenant and use "AzureADMyOrg" (the CIAM tenant
    is itself the directory). Use "AzureADandPersonalMicrosoftAccount" only for
    multi-tenant workforce scenarios.
  EOT
  type        = string
  default     = "AzureADMyOrg"
}

variable "app_roles" {
  description = <<-EOT
    System roles to expose as Entra app roles on the API application. Assigning a
    user to one of these roles makes Entra emit it in the token's `roles` claim,
    which Plenipo's PermissionResolver expands into permissions. Defaults match
    Plenipo's built-in roles.
  EOT
  type = map(object({
    display_name = string
    description  = string
  }))
  default = {
    system_admin = { display_name = "System Administrator", description = "Full platform access across every tenant." }
    tenant_admin = { display_name = "Tenant Administrator", description = "Administers a single tenant: users, roles, modules." }
    user         = { display_name = "User", description = "Standard authenticated user; can chat and use granted tools." }
    guest        = { display_name = "Guest", description = "Read-only access; may not execute tools." }
  }
}
