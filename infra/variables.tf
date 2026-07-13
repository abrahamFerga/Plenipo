# =============================================================================
# variables.tf  (root)
# -----------------------------------------------------------------------------
# All root input variables. Per-environment values live in
# environments/<env>.tfvars (see the .example files).
# =============================================================================

# ----------------------------------------------------------------------------
# Core naming / placement
# ----------------------------------------------------------------------------
variable "project" {
  description = "Project name; used as the lowercase naming prefix for all resources."
  type        = string
  default     = "cortex"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{1,10}$", var.project))
    error_message = "project must be lowercase alphanumeric, 2-11 chars, starting with a letter."
  }
}

variable "environment" {
  description = <<-EOT
    Deployment environment — part of every resource name. dev/staging/prod for the shared
    SaaS rings, or a CUSTOMER SLUG (lowercase kebab) for a dedicated per-customer environment
    (the deploy-customer workflow passes the slug here, giving cortex-<slug>-* resources and
    a tenants/<slug>.tfstate state of their own).
  EOT
  type        = string

  validation {
    condition     = can(regex("^[a-z][a-z0-9-]{1,20}$", var.environment)) && !endswith(var.environment, "-")
    error_message = "environment must be lowercase kebab (2-21 chars, starts with a letter, no trailing hyphen): dev, staging, prod, or a customer slug."
  }
}

variable "location" {
  description = "Azure region for all resources (e.g. westeurope)."
  type        = string
  default     = "westeurope"
}

variable "extra_tags" {
  description = "Additional tags merged on top of the standard cortex tag set."
  type        = map(string)
  default     = {}
}

# ----------------------------------------------------------------------------
# Database (PostgreSQL Flexible Server)
# ----------------------------------------------------------------------------
variable "postgres_sku_name" {
  description = "SKU for the PostgreSQL Flexible Server (e.g. B_Standard_B1ms, GP_Standard_D2s_v3)."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "postgres_storage_mb" {
  description = "Storage allocated to the PostgreSQL server, in MB."
  type        = number
  default     = 32768
}

variable "postgres_admin_username" {
  description = "Administrator login for the PostgreSQL server."
  type        = string
  default     = "cortexadmin"
}

variable "postgres_aad_admin_object_id" {
  description = "Object ID of the Entra ID principal to set as PostgreSQL AAD administrator. Empty disables AAD admin assignment."
  type        = string
  default     = ""
}

variable "postgres_aad_admin_principal_name" {
  description = "Display/principal name of the Entra ID PostgreSQL AAD administrator (required if object_id is set)."
  type        = string
  default     = ""
}

variable "postgres_allowed_cidr" {
  description = "Optional public CIDR allowed through the PostgreSQL firewall (e.g. an office IP). Empty means no public rule."
  type        = string
  default     = ""
}

# ----------------------------------------------------------------------------
# Redis cache
# ----------------------------------------------------------------------------
variable "redis_sku_name" {
  description = "Azure Cache for Redis SKU tier (Basic, Standard, Premium)."
  type        = string
  default     = "Basic"
}

variable "redis_family" {
  description = "Redis SKU family: C (Basic/Standard) or P (Premium)."
  type        = string
  default     = "C"
}

variable "redis_capacity" {
  description = "Redis capacity / size unit within the family (e.g. 0 = C0 250MB, 1 = C1 1GB)."
  type        = number
  default     = 0
}

# ----------------------------------------------------------------------------
# Container App (Cortex.Api)
# ----------------------------------------------------------------------------
variable "api_image" {
  description = "Fully-qualified container image for Cortex.Api. The deploy pipeline overrides this with the freshly-pushed ACR image:tag."
  type        = string
  default     = "mcr.microsoft.com/dotnet/aspnet:10.0"
}

variable "api_target_port" {
  description = "Container port the API listens on (ASPNETCORE binds here)."
  type        = number
  default     = 8080
}

variable "api_cpu" {
  description = "vCPU allocated to each API container replica."
  type        = number
  default     = 0.5
}

variable "api_memory" {
  description = "Memory allocated to each API container replica (e.g. '1Gi')."
  type        = string
  default     = "1Gi"
}

variable "api_min_replicas" {
  description = "Minimum replicas. 0 enables scale-to-zero (cheap, cold starts)."
  type        = number
  default     = 0
}

variable "api_max_replicas" {
  description = "Maximum replicas for HTTP autoscaling."
  type        = number
  default     = 1
}

variable "api_extra_env" {
  description = "Additional plain (non-secret) environment variables for the API container (e.g. Ai__Provider, Channels__WhatsApp__Enabled)."
  type        = map(string)
  default     = {}
}

variable "enable_keyvault_secret_vault" {
  description = "Run the platform's secret vault in Key Vault mode: tenant AI keys, connector secrets, and per-user OAuth tokens are stored as Key Vault secrets (the DB keeps kv: pointers) instead of DataProtection ciphertext in the database. Grants the app identity secret write/delete and sets Secrets__Provider/Secrets__KeyVaultUri."
  type        = bool
  default     = true
}

# ----------------------------------------------------------------------------
# Entra External ID (CIAM) / auth
# NOTE: The CIAM tenant itself is provisioned manually. These variables wire the
# app registrations and runtime config to that tenant.
# ----------------------------------------------------------------------------
variable "ciam_tenant_id" {
  description = "Entra External ID (CIAM) tenant ID the API/SPA authenticate against."
  type        = string
  default     = ""
}

variable "ciam_authority_host" {
  description = "CIAM authority host, e.g. https://<tenant>.ciamlogin.com."
  type        = string
  default     = ""
}

variable "spa_redirect_uris" {
  description = "Redirect URIs for the SPA app registration (SPA platform)."
  type        = list(string)
  default     = ["http://localhost:5173"]
}

# ----------------------------------------------------------------------------
# CI/CD (GitHub Actions OIDC federation)
# ----------------------------------------------------------------------------
variable "github_owner" {
  description = "GitHub org/user that owns the repo (for OIDC federated credential subjects)."
  type        = string
  default     = ""
}

variable "github_repo" {
  description = "GitHub repository name (for OIDC federated credential subjects)."
  type        = string
  default     = "Cortex"
}

variable "github_environments" {
  description = "GitHub deployment environments to grant OIDC trust (e.g. ['production'])."
  type        = list(string)
  default     = ["production"]
}
