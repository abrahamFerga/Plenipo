# Cortex — Infrastructure (Terraform)

Infrastructure-as-Code for the **Cortex** AI-first platform on Azure. Everything
is prefixed `cortex` and scoped per environment (`dev`, `staging`, `prod`).

## Topology provisioned

| Concern        | Azure resource                                   |
| -------------- | ------------------------------------------------ |
| API compute    | Azure Container Apps (`Cortex.Api`, scale-to-0)  |
| Registry       | Azure Container Registry (managed-identity pull) |
| Database       | PostgreSQL Flexible Server v17 — `cortex_platform` + `cortex_audit` |
| Cache/backplane| Azure Cache for Redis                            |
| Secrets        | Azure Key Vault (RBAC authorization)             |
| App identity   | User-assigned Managed Identity (KV + ACR roles)  |
| Observability  | Log Analytics + Application Insights (OTEL)      |
| Auth           | Entra External ID (CIAM) — API + SPA app regs    |
| CI/CD identity | User-assigned MI + GitHub OIDC federated creds   |

## Layout

```
infra/
  versions.tf providers.tf main.tf variables.tf outputs.tf locals.tf
  modules/{monitoring,identity,keyvault,database,cache,container-app,cicd-identity,entra-external-id}/
  environments/{dev,staging,prod}.tfvars.example
```

## Prerequisites

- Terraform >= 1.9
- Azure CLI (`az login`) for local runs, or GitHub Actions OIDC in CI
- An existing **remote state backend**: a Storage Account + blob container.
  Terraform does not bootstrap its own backend — create it once (see below).
- An **Entra External ID (CIAM) tenant** provisioned manually (user flows,
  branding). Paste its tenant ID / authority host into the tfvars.

### One-time backend bootstrap

```bash
az group create -n rg-cortex-tfstate -l westeurope
az storage account create -n cortextfstate<unique> -g rg-cortex-tfstate -l westeurope --sku Standard_LRS
az storage container create -n tfstate --account-name cortextfstate<unique>
```

## Initialize (partial backend config)

Backend values are passed at init so nothing environment-specific is committed:

```bash
terraform init \
  -backend-config="resource_group_name=rg-cortex-tfstate" \
  -backend-config="storage_account_name=cortextfstate<unique>" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=cortex-dev.tfstate"
```

Use a distinct `key` per environment (e.g. `cortex-dev.tfstate`,
`cortex-prod.tfstate`).

## Plan / apply per environment

```bash
cp environments/dev.tfvars.example environments/dev.tfvars   # then edit
terraform plan  -var-file=environments/dev.tfvars
terraform apply -var-file=environments/dev.tfvars
```

The deploy pipeline overrides the image:

```bash
terraform apply -auto-approve \
  -var-file=environments/dev.tfvars \
  -var "api_image=<acr-login-server>/cortex-api:<git-sha>"
```

## Identity & RBAC (Entra External ID)

Authentication uses **Entra External ID** (CIAM — the successor to Azure AD B2C).
The `entra-external-id` module registers two applications in your CIAM tenant and
defines **app roles** that map 1:1 to Cortex's system roles.

### How role claims become Cortex permissions

```
Operator assigns user → app role        (e.g. "tenant_admin")  in Entra
        │
        ▼
Entra issues access token with          "roles": ["tenant_admin"]
        │
        ▼
Cortex API (PermissionResolver)         RolePermissions.ForRole("tenant_admin")
        │                               → ["platform.*", "chat.*"]
        ▼
Per-user tool grants (Admin dashboard)  add fine-grained tools.<module>.<tool>
```

The four default app roles are `system_admin`, `tenant_admin`, `user`, `guest`
(override via the module's `app_roles` variable). The API reads the `roles` claim
(`AuthOptions` → `RoleClaimType = "roles"`), and the **Admin → Users & Roles**
dashboard layers per-user permission grants on top.

### Manual CIAM steps (not Terraform-managed)

The `azuread` provider operates *inside* an existing tenant, so the tenant itself
and its user experience are created out-of-band:

1. Create an **Entra External ID** tenant (Azure portal → *Microsoft Entra External ID*).
2. Create a **sign-up/sign-in user flow** and associate both app registrations.
3. Put the tenant ID and authority host in your tfvars:
   ```hcl
   ciam_tenant_id      = "<your-ciam-tenant-id>"
   ciam_authority_host = "https://<tenant>.ciamlogin.com"
   spa_redirect_uris   = ["http://localhost:5173", "https://app.example.com"]
   ```
4. `terraform apply`, then assign users to app roles (portal → *Enterprise
   applications* → `cortex-<env>-api` → *Users and groups*, or scripted via the
   `entra_app_role_ids` output).

### Runtime auth wiring (the API's `Auth` config section)

The container app receives these from Terraform; locally set them in
`appsettings.Development.json` or user-secrets (or use the `X-Dev-*` dev-auth
fallback, which needs no IdP):

```jsonc
"Auth": {
  "Authority": "https://<tenant>.ciamlogin.com/<ciam-tenant-id>/v2.0",
  "Audience": "<entra_api_client_id output>",
  "TenantClaim": "tenant"
}
```

When `Authority` is empty and the environment is Development, the API falls back
to header-based dev auth (`X-Dev-Subject`, `X-Dev-Tenant`, `X-Dev-Roles`).

## Secrets

- No secrets are hardcoded. The DB admin password is generated
  (`random_password`) and stored in Key Vault.
- Tenant chat-provider keys are entered in Admin → AI Settings and stored through the configured
  secret vault; Terraform does not seed or inject chat-provider credentials.
- WhatsApp channel credentials (`whatsapp-app-secret`, `whatsapp-access-token`,
  `whatsapp-verify-token`) follow the same placeholder + out-of-band contract;
  the non-secret channel settings (`Channels__WhatsApp__Enabled`, `PhoneNumberId`,
  `ModuleId`, `TenantSlug`) go in `api_extra_env` — see
  [docs/WHATSAPP_CHANNEL.md](../docs/WHATSAPP_CHANNEL.md).
- Composed Npgsql connection strings (`platform-connection-string`,
  `audit-connection-string`) are stored whole and mapped 1:1 onto the
  `ConnectionStrings__cortex-*` env vars the app binds.
- The app reads all secrets at runtime via its **managed identity** (Key Vault
  Secrets User), never via stored connection strings in config.
- **Admin-entered secrets in Key Vault** (optional): set
  `enable_keyvault_secret_vault = true` to run the platform's secret vault in
  Key Vault mode — tenant AI keys, connector keys, and per-user OAuth tokens that admins enter
  in the UI are then stored as Key Vault secrets (the DB keeps `kv:` pointers).
  This grants the app identity **Secrets Officer** (it creates/deletes secrets
  at runtime) and sets `Secrets__Provider` / `Secrets__KeyVaultUri`. Flipping it
  on an existing deployment is safe: previously stored secrets keep resolving.

## CI/CD

The GitHub Actions workflows (`.github/workflows/`) run this with **OIDC** — no
stored cloud credentials. After the first apply, read these outputs and wire
them into GitHub:

```bash
terraform output cicd_identity_client_id   # -> AZURE_CLIENT_ID
terraform output acr_login_server          # -> ACR_LOGIN_SERVER
```

See `.github/workflows/README.md` for the full secret/variable list and the
federated-credential subject claims.

## TODOs / manual steps

- **CIAM tenant**: created manually; paste IDs into tfvars (see Identity & RBAC).
- **App-role assignment**: assign users to the `cortex-<env>-api` app roles.
- **Backend storage**: bootstrap once (above) before first `init`.
- **Private networking**: Postgres/Redis are public + firewalled in this
  scaffold. For production hardening, add VNet integration + private endpoints.
- **Tenant AI connections**: configure provider/model/key in Admin → AI Settings after deployment.
