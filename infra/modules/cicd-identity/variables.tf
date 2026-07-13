variable "name_prefix" {
  description = "Naming prefix, e.g. plenipo-dev."
  type        = string
}

variable "resource_group_name" {
  description = "Resource group to deploy into."
  type        = string
}

variable "resource_group_id" {
  description = "Resource group ID (scope for the Contributor role assignment)."
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

variable "github_owner" {
  description = "GitHub org/user that owns the repo."
  type        = string
}

variable "github_repo" {
  description = "GitHub repository name."
  type        = string
}

variable "github_environments" {
  description = "GitHub deployment environments to trust (e.g. ['production'])."
  type        = list(string)
  default     = ["production"]
}

variable "acr_id" {
  description = "ACR resource ID (scope for AcrPush)."
  type        = string
}

variable "key_vault_id" {
  description = "Key Vault resource ID (scope for Key Vault Secrets Officer)."
  type        = string
}
