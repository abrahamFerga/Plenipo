# =============================================================================
# module: database
# -----------------------------------------------------------------------------
# Two independently credentialed PostgreSQL Flexible Servers (v17):
#   - plenipo_platform : operational data
#   - plenipo_audit    : append-only audit log
# Both admin passwords are generated (random_password) and surfaced as sensitive
# outputs so the keyvault module can store the connection strings. Optional AAD admin + firewall.
# =============================================================================

# Strong, generated admin password. Stored only in state + Key Vault, never in
# source. Excludes characters Postgres/Azure dislike in passwords.
resource "random_password" "admin" {
  length           = 24
  special          = true
  override_special = "!#$%*-_=+"
  min_lower        = 2
  min_upper        = 2
  min_numeric      = 2
  min_special      = 2
}

# The audit store has an independent server and credential. Compromise of the operational
# connection string therefore does not confer any access to the security trail.
resource "random_password" "audit_admin" {
  length           = 24
  special          = true
  override_special = "!#$%*-_=+"
  min_lower        = 2
  min_upper        = 2
  min_numeric      = 2
  min_special      = 2
}

resource "azurerm_postgresql_flexible_server" "this" {
  # Postgres server names: lowercase, 3-63 chars, globally unique.
  name                = "${var.name_compact}pg${var.suffix}"
  resource_group_name = var.resource_group_name
  location            = var.location

  # pg17 to match the verified local pairing (AppHost + compose run pgvector/pgvector:pg17).
  version = "17"

  administrator_login    = var.admin_username
  administrator_password = random_password.admin.result

  sku_name   = var.sku_name
  storage_mb = var.storage_mb

  # Public access enabled but locked by firewall rules. For private networking,
  # add delegated_subnet_id / private_dns_zone_id (out of scope for scaffold).
  public_network_access_enabled = true

  zone = "1"

  # Authentication: password always; AAD optionally enabled if an admin object
  # id is supplied.
  authentication {
    password_auth_enabled         = true
    active_directory_auth_enabled = var.aad_admin_object_id != ""
    tenant_id                     = var.aad_admin_object_id != "" ? data.azurerm_client_config.current.tenant_id : null
  }

  tags = var.tags

  lifecycle {
    # Zone is sometimes reassigned by Azure; don't fight it on subsequent plans.
    ignore_changes = [zone]
  }
}

resource "azurerm_postgresql_flexible_server" "audit" {
  name                = "${var.name_compact}pgaudit${var.suffix}"
  resource_group_name = var.resource_group_name
  location            = var.location
  version             = "17"

  administrator_login           = "${var.admin_username}_audit"
  administrator_password        = random_password.audit_admin.result
  sku_name                      = var.sku_name
  storage_mb                    = var.storage_mb
  public_network_access_enabled = true
  zone                          = "1"

  authentication {
    password_auth_enabled         = true
    active_directory_auth_enabled = var.aad_admin_object_id != ""
    tenant_id                     = var.aad_admin_object_id != "" ? data.azurerm_client_config.current.tenant_id : null
  }

  tags = var.tags

  lifecycle {
    ignore_changes = [zone]
  }
}

data "azurerm_client_config" "current" {}

# Allowlist the pgvector extension — Azure Flexible Server refuses CREATE EXTENSION for
# anything not listed in azure.extensions. The app's migrations run CREATE EXTENSION vector
# when Rag:Enabled is true; without this allowlist that migration fails at startup.
resource "azurerm_postgresql_flexible_server_configuration" "extensions" {
  name      = "azure.extensions"
  server_id = azurerm_postgresql_flexible_server.this.id
  value     = "VECTOR"
}

# ---------------------------------------------------------------------------
# Databases
# ---------------------------------------------------------------------------
resource "azurerm_postgresql_flexible_server_database" "platform" {
  name      = "plenipo_platform"
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"

  lifecycle {
    prevent_destroy = false
  }
}

resource "azurerm_postgresql_flexible_server_database" "audit" {
  name      = "plenipo_audit"
  server_id = azurerm_postgresql_flexible_server.audit.id
  charset   = "UTF8"
  collation = "en_US.utf8"

  lifecycle {
    prevent_destroy = false
  }
}

# ---------------------------------------------------------------------------
# Firewall
# ---------------------------------------------------------------------------

# Allow access from other Azure services (Container Apps egress). This is the
# "0.0.0.0" special rule meaning "Azure services".
resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  name             = "allow-azure-services"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "audit_azure_services" {
  name             = "allow-azure-services"
  server_id        = azurerm_postgresql_flexible_server.audit.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}

# Optional single-CIDR allow rule (e.g. office / operator IP).
resource "azurerm_postgresql_flexible_server_firewall_rule" "allowed_cidr" {
  count            = var.allowed_cidr != "" ? 1 : 0
  name             = "allow-operator"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = cidrhost(var.allowed_cidr, 0)
  end_ip_address   = cidrhost(var.allowed_cidr, -1)
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "audit_allowed_cidr" {
  count            = var.allowed_cidr != "" ? 1 : 0
  name             = "allow-operator"
  server_id        = azurerm_postgresql_flexible_server.audit.id
  start_ip_address = cidrhost(var.allowed_cidr, 0)
  end_ip_address   = cidrhost(var.allowed_cidr, -1)
}

# ---------------------------------------------------------------------------
# AAD administrator (optional)
# ---------------------------------------------------------------------------
resource "azurerm_postgresql_flexible_server_active_directory_administrator" "aad" {
  count               = var.aad_admin_object_id != "" ? 1 : 0
  server_name         = azurerm_postgresql_flexible_server.this.name
  resource_group_name = var.resource_group_name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  object_id           = var.aad_admin_object_id
  principal_name      = var.aad_admin_principal_name
  principal_type      = "User"
}

resource "azurerm_postgresql_flexible_server_active_directory_administrator" "audit_aad" {
  count               = var.aad_admin_object_id != "" ? 1 : 0
  server_name         = azurerm_postgresql_flexible_server.audit.name
  resource_group_name = var.resource_group_name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  object_id           = var.aad_admin_object_id
  principal_name      = var.aad_admin_principal_name
  principal_type      = "User"
}
