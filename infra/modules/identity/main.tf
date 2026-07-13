# =============================================================================
# module: identity
# -----------------------------------------------------------------------------
# User-assigned managed identity for Plenipo.Api. This identity is attached to
# the container app and used for:
#   - Pulling images from ACR (AcrPull, assigned in root main.tf)
#   - Reading secrets from Key Vault (Key Vault Secrets User, assigned in the
#     keyvault module)
# Keeping the identity in its own module lets multiple resources reference it
# without creating dependency cycles.
# =============================================================================

resource "azurerm_user_assigned_identity" "app" {
  name                = "${var.name_prefix}-api-id"
  resource_group_name = var.resource_group_name
  location            = var.location
  tags                = var.tags
}
