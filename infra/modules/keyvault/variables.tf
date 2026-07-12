variable "name_prefix" {
  description = "Naming prefix, e.g. cortex-dev."
  type        = string
}

variable "name_compact" {
  description = "Hyphen-free compact name base, e.g. cortexdev."
  type        = string
}

variable "suffix" {
  description = "Random suffix for global uniqueness."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "location" {
  description = "Azure region."
  type        = string
}

variable "tags" {
  description = "Common tags."
  type        = map(string)
  default     = {}
}

variable "app_identity_principal_id" {
  description = "Principal ID of the app managed identity to grant Key Vault Secrets User."
  type        = string
}

variable "grant_app_secrets_officer" {
  description = "Grant the app identity write/delete on vault secrets. Required when the platform's secret vault runs in Key Vault mode (Secrets:Provider=AzureKeyVault): tenant AI keys, connector secrets, and per-user OAuth tokens are then CREATED and DELETED by the app at runtime, not just read."
  type        = bool
  default     = false
}

variable "db_admin_password" {
  description = "Generated PostgreSQL admin password to store as a secret."
  type        = string
  sensitive   = true
}

variable "platform_connection_string" {
  description = "Composed Npgsql connection string for the platform database (what the app reads as ConnectionStrings__cortex-platform)."
  type        = string
  sensitive   = true
}

variable "audit_connection_string" {
  description = "Composed Npgsql connection string for the audit database (what the app reads as ConnectionStrings__cortex-audit)."
  type        = string
  sensitive   = true
}

variable "redis_connection_string" {
  description = "Redis primary connection string to store as a secret."
  type        = string
  sensitive   = true
}

variable "channel_secret_names" {
  description = "Names of chat-channel secrets to seed as placeholders (values injected out-of-band)."
  type        = list(string)
  default     = ["whatsapp-app-secret", "whatsapp-access-token", "whatsapp-verify-token"]
}

variable "purge_protection_enabled" {
  description = "Enable purge protection (recommended for prod; blocks immediate delete)."
  type        = bool
  default     = false
}
