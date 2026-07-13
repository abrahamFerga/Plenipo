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

variable "sku_name" {
  description = "Redis SKU tier (Basic, Standard, Premium)."
  type        = string
  default     = "Basic"
}

variable "family" {
  description = "Redis SKU family: C (Basic/Standard) or P (Premium)."
  type        = string
  default     = "C"
}

variable "capacity" {
  description = "Capacity unit within the family (e.g. 0 = C0)."
  type        = number
  default     = 0
}
