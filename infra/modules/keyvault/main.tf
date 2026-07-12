# =============================================================================
# module: keyvault
# -----------------------------------------------------------------------------
# Key Vault in RBAC authorization mode. Holds all platform secrets. The app
# managed identity is granted "Key Vault Secrets User" so the container app can
# resolve secret references at runtime.
#
# Secret placeholders are created for values the platform needs. Some (DB
# password, Redis connection string) are wired from other modules; LLM provider
# keys are created as placeholders with a sentinel value and are expected to be
# overwritten out-of-band (manually or by a secrets-sync pipeline) — Terraform
# ignores subsequent value drift via lifecycle.ignore_changes.
# =============================================================================

data "azurerm_client_config" "current" {}

resource "azurerm_key_vault" "this" {
  # Key Vault names: 3-24 chars, alphanumeric + hyphens, globally unique.
  name                = substr("${var.name_compact}kv${var.suffix}", 0, 24)
  resource_group_name = var.resource_group_name
  location            = var.location
  tenant_id           = data.azurerm_client_config.current.tenant_id
  sku_name            = "standard"

  # RBAC instead of legacy access policies.
  rbac_authorization_enabled = true

  # Hardening
  purge_protection_enabled   = var.purge_protection_enabled
  soft_delete_retention_days = 7

  tags = var.tags
}

# ---------------------------------------------------------------------------
# Role assignments
# ---------------------------------------------------------------------------

# The app managed identity may READ secrets at runtime.
resource "azurerm_role_assignment" "app_secrets_user" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets User"
  principal_id         = var.app_identity_principal_id
}

# When the platform stores tenant AI keys / connector secrets / OAuth tokens in Key Vault
# (Secrets:Provider=AzureKeyVault), the app writes and deletes secrets at runtime and
# needs Officer, not just User.
resource "azurerm_role_assignment" "app_secrets_officer" {
  count                = var.grant_app_secrets_officer ? 1 : 0
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = var.app_identity_principal_id
}

# The deployer (whoever runs terraform apply) needs Secrets Officer to create
# the secrets below. In CI this is the OIDC identity; locally it's your user.
resource "azurerm_role_assignment" "deployer_secrets_officer" {
  scope                = azurerm_key_vault.this.id
  role_definition_name = "Key Vault Secrets Officer"
  principal_id         = data.azurerm_client_config.current.object_id
}

# ---------------------------------------------------------------------------
# Secrets
# ---------------------------------------------------------------------------

# Database admin password (generated in the database module).
resource "azurerm_key_vault_secret" "db_admin_password" {
  name         = "db-admin-password"
  value        = var.db_admin_password
  key_vault_id = azurerm_key_vault.this.id
  tags         = var.tags

  depends_on = [azurerm_role_assignment.deployer_secrets_officer]
}

# Composed Npgsql connection strings — exactly what the app binds as
# ConnectionStrings__cortex-platform / ConnectionStrings__cortex-audit.
resource "azurerm_key_vault_secret" "platform_connection_string" {
  name         = "platform-connection-string"
  value        = var.platform_connection_string
  key_vault_id = azurerm_key_vault.this.id
  tags         = var.tags

  depends_on = [azurerm_role_assignment.deployer_secrets_officer]
}

resource "azurerm_key_vault_secret" "audit_connection_string" {
  name         = "audit-connection-string"
  value        = var.audit_connection_string
  key_vault_id = azurerm_key_vault.this.id
  tags         = var.tags

  depends_on = [azurerm_role_assignment.deployer_secrets_officer]
}

# Redis connection string (from the cache module).
resource "azurerm_key_vault_secret" "redis_connection_string" {
  name         = "redis-connection-string"
  value        = var.redis_connection_string
  key_vault_id = azurerm_key_vault.this.id
  tags         = var.tags

  depends_on = [azurerm_role_assignment.deployer_secrets_officer]
}

# Chat-channel secret placeholders (WhatsApp app secret / access token / verify token).
resource "azurerm_key_vault_secret" "channel_keys" {
  for_each = toset(var.channel_secret_names)

  name         = each.value
  value        = "REPLACE_ME"
  key_vault_id = azurerm_key_vault.this.id
  tags         = var.tags

  lifecycle {
    ignore_changes = [value]
  }

  depends_on = [azurerm_role_assignment.deployer_secrets_officer]
}
