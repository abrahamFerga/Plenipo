# =============================================================================
# modules/entra-external-id/main.tf
# -----------------------------------------------------------------------------
# App registrations for Entra External ID (CIAM) — the customer-identity
# successor to Azure AD B2C. Registers the Plenipo API and SPA applications and,
# crucially, defines App Roles on the API that map 1:1 to Plenipo's system roles.
#
# How this ties into Plenipo RBAC:
#   1. An operator assigns a user to an app role (e.g. "tenant_admin") in Entra.
#   2. Entra includes that role in the access token's `roles` claim.
#   3. Plenipo's PermissionResolver reads the `roles` claim and expands it to the
#      baseline permissions via RolePermissions — no code change per tenant.
#
# The CIAM tenant itself and its user flows (sign-up/sign-in) are provisioned
# out-of-band (see infra/README) because Terraform's azuread provider operates
# inside an existing tenant; this module manages the apps within it.
# =============================================================================

terraform {
  required_providers {
    azuread = {
      source  = "hashicorp/azuread"
      version = "~> 3.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

# Stable id for the delegated scope the SPA uses to call the API.
resource "random_uuid" "api_scope" {}

# Stable ids for each app role, keyed by the role value (system_admin, …).
resource "random_uuid" "app_role" {
  for_each = var.app_roles
}

# ---------------------------------------------------------------------------
# API application — exposes `access_as_user` and the Plenipo system app roles.
# ---------------------------------------------------------------------------
resource "azuread_application" "api" {
  display_name     = "${var.name_prefix}-api"
  sign_in_audience = var.sign_in_audience

  api {
    requested_access_token_version = 2

    oauth2_permission_scope {
      id                         = random_uuid.api_scope.result
      admin_consent_description  = "Allow the Plenipo SPA to call the Plenipo API on behalf of the signed-in user."
      admin_consent_display_name = "Access Plenipo API"
      user_consent_description   = "Allow the app to access the Plenipo API on your behalf."
      user_consent_display_name  = "Access Plenipo API"
      value                      = "access_as_user"
      type                       = "User"
      enabled                    = true
    }
  }

  # One app role per Plenipo system role. The `value` is what lands in the token's
  # `roles` claim and what RolePermissions.ForRole(...) matches on.
  dynamic "app_role" {
    for_each = var.app_roles
    content {
      id                   = random_uuid.app_role[app_role.key].result
      value                = app_role.key
      display_name         = app_role.value.display_name
      description          = app_role.value.description
      allowed_member_types = ["User"]
      enabled              = true
    }
  }

  tags = ["plenipo", var.environment, "terraform"]
}

# ---------------------------------------------------------------------------
# SPA application — public client; pre-authorized to call the API scope.
# ---------------------------------------------------------------------------
resource "azuread_application" "spa" {
  display_name     = "${var.name_prefix}-spa"
  sign_in_audience = var.sign_in_audience

  single_page_application {
    redirect_uris = var.spa_redirect_uris
  }

  required_resource_access {
    resource_app_id = azuread_application.api.client_id

    resource_access {
      id   = random_uuid.api_scope.result
      type = "Scope"
    }
  }

  tags = ["plenipo", var.environment, "terraform"]
}

# Service principal for the API so app-role assignments can target it.
resource "azuread_service_principal" "api" {
  client_id = azuread_application.api.client_id
}
