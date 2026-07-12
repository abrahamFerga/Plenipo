output "id" {
  description = "Resource ID of the Key Vault."
  value       = azurerm_key_vault.this.id
}

output "name" {
  description = "Name of the Key Vault."
  value       = azurerm_key_vault.this.name
}

output "vault_uri" {
  description = "URI of the Key Vault."
  value       = azurerm_key_vault.this.vault_uri
}

# Versionless secret IDs the container app references. Using versionless IDs
# means the app automatically picks up rotated secret versions.
output "secret_ids" {
  description = "Map of logical secret name -> versionless Key Vault secret ID."
  value = merge(
    {
      "db-admin-password"          = azurerm_key_vault_secret.db_admin_password.versionless_id
      "platform-connection-string" = azurerm_key_vault_secret.platform_connection_string.versionless_id
      "audit-connection-string"    = azurerm_key_vault_secret.audit_connection_string.versionless_id
      "redis-connection-string"    = azurerm_key_vault_secret.redis_connection_string.versionless_id
    },
    { for k, s in azurerm_key_vault_secret.channel_keys : k => s.versionless_id }
  )
}
