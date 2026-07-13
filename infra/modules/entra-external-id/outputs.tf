# =============================================================================
# modules/entra-external-id/outputs.tf
# =============================================================================

output "api_client_id" {
  description = "Client (application) ID of the API app registration — the API's token audience."
  value       = azuread_application.api.client_id
}

output "api_scope_value" {
  description = "The delegated scope value the SPA requests (api://<api_client_id>/access_as_user)."
  value       = "api://${azuread_application.api.client_id}/access_as_user"
}

output "spa_client_id" {
  description = "Client (application) ID of the SPA app registration."
  value       = azuread_application.spa.client_id
}

output "api_service_principal_object_id" {
  description = "Object ID of the API service principal (target for app-role assignments)."
  value       = azuread_service_principal.api.object_id
}

output "app_role_ids" {
  description = "Map of Plenipo system role -> Entra app role id."
  value       = { for k, v in random_uuid.app_role : k => v.result }
}
