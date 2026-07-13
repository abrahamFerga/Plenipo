variable "name_prefix" {
  description = "Naming prefix, e.g. plenipo-dev."
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

# --- Image / sizing ----------------------------------------------------------
variable "image" {
  description = "Container image for Plenipo.Api (ACR repo:tag or placeholder)."
  type        = string
}

variable "target_port" {
  description = "Container ingress port."
  type        = number
  default     = 8080
}

variable "cpu" {
  description = "vCPU per replica."
  type        = number
  default     = 0.5
}

variable "memory" {
  description = "Memory per replica (e.g. 1Gi)."
  type        = string
  default     = "1Gi"
}

variable "min_replicas" {
  description = "Minimum replicas (0 = scale to zero)."
  type        = number
  default     = 0
}

variable "max_replicas" {
  description = "Maximum replicas."
  type        = number
  default     = 1
}

variable "scale_concurrent_requests" {
  description = "Concurrent requests per replica before scaling out."
  type        = number
  default     = 50
}

# --- Identity / registry -----------------------------------------------------
variable "identity_id" {
  description = "Resource ID of the user-assigned managed identity."
  type        = string
}

variable "identity_client_id" {
  description = "Client ID of the managed identity (set as AZURE_CLIENT_ID env)."
  type        = string
}

variable "acr_login_server" {
  description = "ACR login server to pull images from."
  type        = string
}

# --- Observability -----------------------------------------------------------
variable "log_analytics_workspace_id" {
  description = "Log Analytics workspace ID for the Container App Environment."
  type        = string
}

variable "appinsights_connection_string" {
  description = "Application Insights connection string for OTEL export."
  type        = string
  sensitive   = true
}

# --- Secrets -----------------------------------------------------------------
variable "key_vault_secret_ids" {
  description = "Map of logical secret name -> versionless Key Vault secret ID."
  type        = map(string)
  default     = {}
}

# --- Auth / CIAM -------------------------------------------------------------
variable "ciam_tenant_id" {
  description = "CIAM tenant ID."
  type        = string
  default     = ""
}

variable "ciam_authority_host" {
  description = "CIAM authority host URL."
  type        = string
  default     = ""
}

variable "api_client_id" {
  description = "Entra API app registration client ID (token audience)."
  type        = string
  default     = ""
}

# --- App config --------------------------------------------------------------
variable "aspnetcore_environment" {
  description = "ASPNETCORE_ENVIRONMENT value (Development/Staging/Production)."
  type        = string
  default     = "Production"
}

variable "extra_env" {
  description = "Additional plain environment variables."
  type        = map(string)
  default     = {}
}
