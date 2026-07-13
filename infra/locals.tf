# =============================================================================
# locals.tf
# -----------------------------------------------------------------------------
# Naming convention + common tags. Everything is lowercase and prefixed with the
# project name ("plenipo"). The standard shape is:
#   "${var.project}-${var.environment}-<resource-short-name>"
# Some Azure resources disallow hyphens and must be globally unique (ACR, Key
# Vault, Storage, Postgres) — for those we use a compact, hyphen-free name plus a
# random suffix (see random_string.suffix in main.tf).
# =============================================================================

locals {
  # e.g. "plenipo-dev"
  name_prefix = "${var.project}-${var.environment}"

  # Compact, hyphen-free base for resources with tight naming rules.
  # e.g. "plenipodev"
  name_compact = "${var.project}${var.environment}"

  # Tags applied to every resource via the provider-agnostic merge below.
  common_tags = merge(
    {
      project     = var.project
      environment = var.environment
      managed_by  = "terraform"
    },
    var.extra_tags
  )
}
