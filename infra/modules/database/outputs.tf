output "fqdn" {
  description = "Fully-qualified domain name of the PostgreSQL server."
  value       = azurerm_postgresql_flexible_server.this.fqdn
}

output "server_name" {
  description = "Name of the PostgreSQL server."
  value       = azurerm_postgresql_flexible_server.this.name
}

output "admin_username" {
  description = "Administrator login name."
  value       = azurerm_postgresql_flexible_server.this.administrator_login
}

output "admin_password" {
  description = "Generated administrator password (consumed by the keyvault module)."
  value       = random_password.admin.result
  sensitive   = true
}

output "platform_database_name" {
  description = "Name of the operational database."
  value       = azurerm_postgresql_flexible_server_database.platform.name
}

output "audit_database_name" {
  description = "Name of the append-only audit database."
  value       = azurerm_postgresql_flexible_server_database.audit.name
}

output "audit_fqdn" {
  description = "Fully-qualified domain name of the independent audit PostgreSQL server."
  value       = azurerm_postgresql_flexible_server.audit.fqdn
}

output "audit_admin_username" {
  description = "Administrator login for the independent audit server."
  value       = azurerm_postgresql_flexible_server.audit.administrator_login
}

output "audit_admin_password" {
  description = "Generated password for the independent audit server."
  value       = random_password.audit_admin.result
  sensitive   = true
}
