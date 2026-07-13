# =============================================================================
# outputs.tf  (root)
# -----------------------------------------------------------------------------
# Useful values for operators and the CI/CD pipeline (e.g. smoke-test URL).
# =============================================================================

output "resource_group_name" {
  description = "Name of the resource group hosting all Cortex resources."
  value       = azurerm_resource_group.this.name
}

output "api_fqdn" {
  description = "Public FQDN of the Cortex.Api container app ingress."
  value       = module.container_app.fqdn
}

output "api_url" {
  description = "Base HTTPS URL of the Cortex.Api (use <url>/health for smoke tests)."
  value       = "https://${module.container_app.fqdn}"
}

output "acr_login_server" {
  description = "Login server of the Azure Container Registry (push target for images)."
  value       = azurerm_container_registry.this.login_server
}

output "acr_name" {
  description = "Name of the Azure Container Registry."
  value       = azurerm_container_registry.this.name
}

output "key_vault_uri" {
  description = "URI of the Key Vault (https://<name>.vault.azure.net/)."
  value       = module.keyvault.vault_uri
}

output "key_vault_name" {
  description = "Name of the Key Vault."
  value       = module.keyvault.name
}

output "app_identity_client_id" {
  description = "Client ID of the user-assigned managed identity used by Cortex.Api."
  value       = module.identity.client_id
}

output "app_identity_principal_id" {
  description = "Principal (object) ID of the app managed identity."
  value       = module.identity.principal_id
}

output "cicd_identity_client_id" {
  description = "Client ID of the GitHub Actions OIDC user-assigned identity (set as AZURE_CLIENT_ID in GitHub)."
  value       = module.cicd_identity.client_id
}

output "postgres_fqdn" {
  description = "Fully-qualified domain name of the PostgreSQL Flexible Server."
  value       = module.database.fqdn
}

output "postgres_platform_database" {
  description = "Name of the operational database."
  value       = module.database.platform_database_name
}

output "postgres_audit_database" {
  description = "Name of the append-only audit database."
  value       = module.database.audit_database_name
}

output "postgres_audit_fqdn" {
  description = "Fully-qualified domain name of the independent audit PostgreSQL server."
  value       = module.database.audit_fqdn
}

output "redis_hostname" {
  description = "Hostname of the Azure Cache for Redis instance."
  value       = module.cache.hostname
}

output "appinsights_connection_string" {
  description = "Application Insights connection string (also injected into the app)."
  value       = module.monitoring.appinsights_connection_string
  sensitive   = true
}

output "entra_api_client_id" {
  description = "Client ID of the Entra (CIAM) API app registration (the API token audience)."
  value       = module.entra_external_id.api_client_id
}

output "entra_api_scope" {
  description = "Delegated scope the SPA requests to call the API."
  value       = module.entra_external_id.api_scope_value
}

output "entra_spa_client_id" {
  description = "Client ID of the Entra (CIAM) SPA app registration."
  value       = module.entra_external_id.spa_client_id
}

output "entra_app_role_ids" {
  description = "Map of Cortex system role -> Entra app role id (for scripted role assignments)."
  value       = module.entra_external_id.app_role_ids
}
