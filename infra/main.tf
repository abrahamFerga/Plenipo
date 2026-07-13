# =============================================================================
# main.tf  (composition root)
# -----------------------------------------------------------------------------
# Creates the resource group and wires the focused modules together. Module
# outputs flow downstream here (e.g. Key Vault id -> container-app for secret
# references, identity client id -> container app, monitoring connection string
# -> container app env var).
# =============================================================================

# Random suffix for globally-unique resource names (ACR, Key Vault, Postgres,
# Storage). Stable across applies because it lives in state.
resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

# -----------------------------------------------------------------------------
# Resource group: rg-plenipo-<env>
# -----------------------------------------------------------------------------
resource "azurerm_resource_group" "this" {
  name     = "rg-${local.name_prefix}"
  location = var.location
  tags     = local.common_tags
}

# -----------------------------------------------------------------------------
# Monitoring: Log Analytics workspace + Application Insights
# -----------------------------------------------------------------------------
module "monitoring" {
  source = "./modules/monitoring"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags
}

# -----------------------------------------------------------------------------
# App identity: user-assigned managed identity for Plenipo.Api
# (role assignments to Key Vault / ACR are made in their respective modules so
#  the scope ids are available there).
# -----------------------------------------------------------------------------
module "identity" {
  source = "./modules/identity"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags
}

# -----------------------------------------------------------------------------
# Container registry + Key Vault live in keyvault module's sibling? -> ACR is
# created in the container-app module's dependency chain. We create ACR here so
# both keyvault role-scoping and container-app can reference it.
# -----------------------------------------------------------------------------
resource "azurerm_container_registry" "this" {
  # ACR names: 5-50 chars, alphanumeric only, globally unique.
  name                = "${local.name_compact}acr${random_string.suffix.result}"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  sku                 = "Standard"
  admin_enabled       = false # Pull via managed identity, never admin creds.
  tags                = local.common_tags
}

# Allow the app identity to pull images from ACR.
resource "azurerm_role_assignment" "app_acr_pull" {
  scope                = azurerm_container_registry.this.id
  role_definition_name = "AcrPull"
  principal_id         = module.identity.principal_id
}

# -----------------------------------------------------------------------------
# Key Vault (RBAC authorization) + secret placeholders
# -----------------------------------------------------------------------------
module "keyvault" {
  source = "./modules/keyvault"

  name_prefix         = local.name_prefix
  name_compact        = local.name_compact
  suffix              = random_string.suffix.result
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags

  # The app identity gets "Key Vault Secrets User" so the container app can read
  # secret references at runtime via managed identity. In Key Vault secret-vault
  # mode the app also writes/deletes secrets, so it gets Officer.
  app_identity_principal_id = module.identity.principal_id
  grant_app_secrets_officer = var.enable_keyvault_secret_vault

  # The database admin password (generated) is stored as a Key Vault secret.
  db_admin_password = module.database.admin_password
  # Redis primary connection string -> secret for the app to consume.
  redis_connection_string = module.cache.primary_connection_string

  # Composed Npgsql connection strings — stored whole so the container app maps
  # them 1:1 onto ConnectionStrings__plenipo-platform / __plenipo-audit.
  platform_connection_string = "Host=${module.database.fqdn};Port=5432;Database=${module.database.platform_database_name};Username=${module.database.admin_username};Password=${module.database.admin_password};Ssl Mode=Require"
  audit_connection_string    = "Host=${module.database.audit_fqdn};Port=5432;Database=${module.database.audit_database_name};Username=${module.database.audit_admin_username};Password=${module.database.audit_admin_password};Ssl Mode=Require"
}

# -----------------------------------------------------------------------------
# Database: PostgreSQL Flexible Server + two databases
# -----------------------------------------------------------------------------
module "database" {
  source = "./modules/database"

  name_prefix         = local.name_prefix
  name_compact        = local.name_compact
  suffix              = random_string.suffix.result
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags

  sku_name                 = var.postgres_sku_name
  storage_mb               = var.postgres_storage_mb
  admin_username           = var.postgres_admin_username
  allowed_cidr             = var.postgres_allowed_cidr
  aad_admin_object_id      = var.postgres_aad_admin_object_id
  aad_admin_principal_name = var.postgres_aad_admin_principal_name
}

# -----------------------------------------------------------------------------
# Cache: Azure Cache for Redis
# -----------------------------------------------------------------------------
module "cache" {
  source = "./modules/cache"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags

  sku_name = var.redis_sku_name
  family   = var.redis_family
  capacity = var.redis_capacity
}

# -----------------------------------------------------------------------------
# Container App environment + Plenipo.Api container app
# -----------------------------------------------------------------------------
module "container_app" {
  source = "./modules/container-app"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags

  # Image + sizing
  image        = var.api_image
  target_port  = var.api_target_port
  cpu          = var.api_cpu
  memory       = var.api_memory
  min_replicas = var.api_min_replicas
  max_replicas = var.api_max_replicas

  # Identity used for ACR pull + Key Vault secret references
  identity_id        = module.identity.id
  identity_client_id = module.identity.client_id

  # Registry
  acr_login_server = azurerm_container_registry.this.login_server

  # Observability
  log_analytics_workspace_id    = module.monitoring.workspace_id
  appinsights_connection_string = module.monitoring.appinsights_connection_string

  # Key Vault secret references (versionless ids resolved by the app identity)
  key_vault_secret_ids = module.keyvault.secret_ids

  # CIAM / auth runtime config. api_client_id is the registered API app, not the
  # managed identity — the API validates tokens issued for this audience.
  ciam_tenant_id      = var.ciam_tenant_id
  ciam_authority_host = var.ciam_authority_host
  api_client_id       = module.entra_external_id.api_client_id

  # Plain extra env. Key Vault secret-vault mode rides on top: the app then stores
  # tenant AI keys / connector secrets / OAuth tokens as KV secrets via its identity.
  extra_env = merge(
    var.api_extra_env,
    var.enable_keyvault_secret_vault ? {
      "Secrets__Provider"    = "AzureKeyVault"
      "Secrets__KeyVaultUri" = module.keyvault.vault_uri
    } : {}
  )

  # Ensure ACR pull role exists before the app tries to pull.
  depends_on = [azurerm_role_assignment.app_acr_pull]
}

# -----------------------------------------------------------------------------
# Entra External ID (CIAM): API + SPA app registrations and Plenipo app roles
# -----------------------------------------------------------------------------
# The CIAM tenant + user flows are provisioned manually (see infra/README). This
# module registers the API and SPA apps and defines App Roles matching Plenipo's
# system roles, so Entra emits a `roles` claim that the API maps to permissions.
module "entra_external_id" {
  source = "./modules/entra-external-id"

  name_prefix       = local.name_prefix
  environment       = var.environment
  spa_redirect_uris = var.spa_redirect_uris
}

# -----------------------------------------------------------------------------
# CI/CD identity: GitHub Actions OIDC federated identity + role assignments
# -----------------------------------------------------------------------------
module "cicd_identity" {
  source = "./modules/cicd-identity"

  name_prefix         = local.name_prefix
  resource_group_name = azurerm_resource_group.this.name
  resource_group_id   = azurerm_resource_group.this.id
  location            = azurerm_resource_group.this.location
  tags                = local.common_tags

  github_owner        = var.github_owner
  github_repo         = var.github_repo
  github_environments = var.github_environments

  acr_id       = azurerm_container_registry.this.id
  key_vault_id = module.keyvault.id
}
