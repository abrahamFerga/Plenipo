variable "name_prefix" {
  description = "Naming prefix, e.g. plenipo-dev."
  type        = string
}

variable "name_compact" {
  description = "Hyphen-free compact name base, e.g. plenipodev."
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

variable "sku_name" {
  description = "PostgreSQL Flexible Server SKU."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "storage_mb" {
  description = "Storage in MB."
  type        = number
  default     = 32768
}

variable "admin_username" {
  description = "Administrator login name."
  type        = string
  default     = "plenipoadmin"
}

variable "allowed_cidr" {
  description = "Optional public CIDR allowed through the firewall. Empty disables the rule."
  type        = string
  default     = ""
}

variable "aad_admin_object_id" {
  description = "Entra ID object ID of the PostgreSQL AAD administrator. Empty disables AAD admin."
  type        = string
  default     = ""
}

variable "aad_admin_principal_name" {
  description = "Principal/display name of the PostgreSQL AAD administrator."
  type        = string
  default     = ""
}
