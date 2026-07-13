# =============================================================================
# module: container-app
# -----------------------------------------------------------------------------
# Container App Environment (wired to Log Analytics) + the Plenipo.Api container
# app. The app:
#   - Runs under the supplied user-assigned managed identity.
#   - Pulls its image from ACR via that identity (registry block, no creds).
#   - Resolves secrets from Key Vault via that identity (secret blocks reference
#     key_vault_secret_id; identity = the user-assigned identity).
#   - Exposes external ingress on the target port (default 8080).
# =============================================================================

resource "azurerm_container_app_environment" "this" {
  name                       = "${var.name_prefix}-cae"
  resource_group_name        = var.resource_group_name
  location                   = var.location
  log_analytics_workspace_id = var.log_analytics_workspace_id
  tags                       = var.tags
}

resource "azurerm_container_app" "api" {
  name                         = "${var.name_prefix}-api"
  resource_group_name          = var.resource_group_name
  container_app_environment_id = azurerm_container_app_environment.this.id
  revision_mode                = "Single"
  tags                         = var.tags

  # Attach the user-assigned managed identity (used for ACR + Key Vault).
  identity {
    type         = "UserAssigned"
    identity_ids = [var.identity_id]
  }

  # Pull from ACR using the managed identity (no admin creds).
  registry {
    server   = var.acr_login_server
    identity = var.identity_id
  }

  # ---------------------------------------------------------------------------
  # Secrets: one per Key Vault secret reference. Resolved at runtime by the
  # managed identity. The container references these by `secret_name` in env.
  # ---------------------------------------------------------------------------
  dynamic "secret" {
    for_each = var.key_vault_secret_ids
    content {
      # Container App secret names must be lowercase alphanumeric or '-'.
      name                = secret.key
      key_vault_secret_id = secret.value
      identity            = var.identity_id
    }
  }

  ingress {
    external_enabled = true
    target_port      = var.target_port
    transport        = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = var.min_replicas
    max_replicas = var.max_replicas

    container {
      name   = "plenipo-api"
      image  = var.image
      cpu    = var.cpu
      memory = var.memory

      # -- Plain (non-secret) environment ------------------------------------
      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.aspnetcore_environment
      }
      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:${var.target_port}"
      }
      env {
        name  = "ASPNETCORE_HTTP_PORTS"
        value = tostring(var.target_port)
      }

      # Observability (OpenTelemetry -> Application Insights)
      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = var.appinsights_connection_string
      }
      env {
        name  = "OTEL_SERVICE_NAME"
        value = "plenipo-api"
      }

      # Auth (Entra External ID) — the keys AuthSetup binds: Auth:Authority and
      # Auth:Audience. Left empty when CIAM isn't wired yet, which the app
      # treats as "auth not configured" (and refuses to start outside Development).
      env {
        name  = "Auth__Authority"
        value = var.ciam_tenant_id != "" && var.ciam_authority_host != "" ? "${trimsuffix(var.ciam_authority_host, "/")}/${var.ciam_tenant_id}/v2.0" : ""
      }
      env {
        name  = "Auth__Audience"
        value = var.api_client_id
      }

      # Tells the app which managed identity to use (e.g. DefaultAzureCredential
      # ManagedIdentityClientId) for Key Vault / other Azure SDK calls.
      env {
        name  = "AZURE_CLIENT_ID"
        value = var.identity_client_id
      }

      # -- Secret-backed environment -----------------------------------------
      # Map each Key Vault secret to an env var the app reads. The secret_name
      # points at the `secret` block above (same key).
      dynamic "env" {
        for_each = local.secret_env_map
        content {
          name        = env.value.env_name
          secret_name = env.value.secret_name
        }
      }

      # -- Caller-supplied extra plain env -----------------------------------
      dynamic "env" {
        for_each = var.extra_env
        content {
          name  = env.key
          value = env.value
        }
      }

      # Liveness uses the lightweight /alive probe (process up); readiness runs the full health checks.
      liveness_probe {
        transport = "HTTP"
        port      = var.target_port
        path      = "/alive"
      }
      readiness_probe {
        transport = "HTTP"
        port      = var.target_port
        path      = "/health"
      }
    }

    # HTTP-concurrency autoscaling rule.
    http_scale_rule {
      name                = "http-concurrency"
      concurrent_requests = var.scale_concurrent_requests
    }
  }
}
