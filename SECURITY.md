# Security Policy

Plenipo is a **security-first platform** — tool-level authorization, full audit, human-in-the-loop approval,
and multi-tenant isolation are core to the design, not add-ons. This document explains how to report a
vulnerability and summarizes the security model so you can evaluate it before building on Plenipo.

## Reporting a vulnerability

Please report security issues **privately** — do **not** open a public issue containing exploit details.

- **Preferred:** GitHub's private vulnerability reporting — the repository's **Security** tab →
  **Report a vulnerability**. This keeps the report confidential until a fix is available.
- If private reporting isn't enabled, open a minimal public issue asking a maintainer to open a private
  channel — **without** any reproduction or exploit detail.

Please include: the affected component and version, a description, reproduction steps, and the impact. We
aim to acknowledge within a few days and will coordinate a fix and a disclosure timeline with you.

## Supported versions

Plenipo is pre-1.0 (`0.1.0-alpha`). Security fixes land on the latest `main`; there is no long-term-support
branch yet. **Alpha software is not yet production-hardened** — review the model and hardening notes below
before deploying.

| Version | Supported |
|---------|-----------|
| `0.1.0-alpha` (latest `main`) | ✅ |
| older pre-releases | ❌ |

## Security model (what Plenipo enforces)

- **Tool authorization _before_ the model call.** For each turn the agent runner resolves a module's tools
  and removes any the caller may not invoke **before** building the LLM request — the model never receives
  the schema of a tool the user is not permitted to call (`AuthorizedAgentRunner`).
- **Layered RBAC.** System roles → hierarchical dotted permissions (with wildcards) → per-resource ACLs.
  Endpoints gate on permissions; authorization policies are materialized on demand.
- **Human-in-the-loop for side-effecting tools.** A tool whose manifest marks it `RequiresApproval` is never
  auto-executed: it is blocked, recorded as a pending approval, and only runs after a user with
  approval-management permission approves it.
- **Append-only audit.** Every tool invocation, data change, and token spend is written to a **separate**
  audit database (distinct schema) so the security trail cannot be edited through the application.
- **System-identity execution is scoped and attributed.** Module-declared recurring jobs run with no
  user behind them: the processor executes them **tenant-scoped** under a well-known system principal
  whose authority is only the owning module's tool wildcard (`tools.{moduleId}.*`) — never a platform
  wildcard — and audit rows attribute the run to the scheduler (tenant recorded, user id null), so a
  scheduled run can neither cross a tenant boundary nor masquerade as a person.
- **Consumer-facing ADMT disclosure.** Any authenticated user can read their own tenant's AI-decision
  history in plain language (`GET /api/platform/ai-decisions`): what the agent did or proposed, when, on
  what basis, and the human-oversight outcome — approved by whom, rejected, or executed automatically
  because the tool is not approval-gated — and download it as JSON from `/account/ai-decisions`. This is
  the account a CPPA-style automated-decision-making (ADMT) disclosure request expects. It reads the same
  append-only stores as the audit trail, and every entry carries its stable record id so an export remains
  verifiable against the trail.
- **Multi-tenant isolation.** Row-level isolation via EF Core global query filters on `TenantId` — a query
  cannot cross a tenant boundary by omission.
- **Authentication.** Two production shapes, both OIDC bearer-JWT on the API surface: an external
  IdP (Entra External ID, Keycloak, …) via `Auth:Authority`/`Auth:Audience`, or **built-in sign-in**
  (`Auth:Mode=Local`, ADR 0003) where the host is its own OpenID Connect issuer — embedded OpenIddict,
  PBKDF2-hashed credentials in the platform database, per-credential lockout, optional TOTP, a
  security stamp that ends outstanding sessions on any password reset, and every sign-in event in the
  append-only audit trail. The dev-only header fallback is registered **only** in the Development
  environment. Tokens are always audience-validated.
- **Controlled outbound traffic.** Tenant-configured HTTP endpoints are restricted to HTTPS and public
  network destinations by default; redirects are disabled on sensitive clients. Operators can explicitly
  permit HTTP/private destinations only for an isolated self-hosted deployment.
- **Untrusted inbound identities are denied by default.** WhatsApp and email adapters process only
  explicitly allowlisted senders unless an operator deliberately enables unknown-sender provisioning.
- **Secrets.** Never committed — user-secrets in development, Key Vault / managed identity in production
  (see `infra/`). Logs record metadata (tool name, permission, duration), never message contents.
- **Dependency & image hygiene.** Dependabot keeps NuGet, npm, GitHub Actions, and Docker base images
  patched; CI runs a Trivy scan on the API image and fails the build on CRITICAL/HIGH findings.

## Hardening notes for deployment

- Configure authentication explicitly. The dev-auth fallback is inert outside Development, so you
  **must** either set both `Auth:Authority` and `Auth:Audience` (external IdP) or set
  `Auth:Mode=Local` (built-in sign-in — the on-prem/mini-PC path with no external dependency).
  A partial or contradictory configuration fails startup; built-in sign-in is never a silent default.
- **Built-in sign-in specifics** (`Auth:Mode=Local`): the first admin comes from the `Bootstrap`
  section — with no `Bootstrap:AdminInitialPassword` a temporary password is printed ONCE to the
  startup log and must be changed at first sign-in. Login and token endpoints are IP-rate-limited and
  credentials lock after repeated failures. Signing keys are generated per deployment and stored
  Data-Protection-protected in the platform database. On plain-HTTP LAN deployments set
  `Auth:RequireHttpsMetadata=false` deliberately; anywhere TLS exists, leave it on.
- **Multi-factor authentication** is enrolled and enforced at your IdP (Entra External ID user
  flows, Keycloak/Authentik for self-hosters) — or, under built-in sign-in, as TOTP enrolled from
  `/api/auth/totp/*` and verified on the login page. Set `Auth:RequireMfa` to make the platform
  additionally **reject any token that was not issued after MFA** (judged by the `amr` claim;
  accepted markers configurable via `Auth:MfaAmrValues`) — the local issuer emits `amr: ["otp"]`
  after a verified TOTP code, so the same backstop covers both shapes.
- **Browser sign-in.** Set `Auth:ClientId` to the PUBLIC client id of your SPA registration, and register
  `https://<your-host>/signin-callback` and `https://<your-host>/admin/signin-callback` as its redirect
  URIs. The shipped shell then runs Authorization Code + PKCE with no client secret — a browser cannot
  keep one. `Auth:Scopes` adds provider-specific scopes beyond `openid profile email`; there is no
  default, because Entra's `{audience}/.default` is rejected by Keycloak and Authentik.
  *Trade-off to know:* the access token is held in memory only, and the refresh token in
  `sessionStorage` — readable by script on your own origin, which is the honest limit of a browser SPA.
  Shorten refresh-token lifetime at the IdP if that matters to your threat model.
- **Anonymous endpoints** are exactly: `/alive` and `/health` (liveness/readiness),
  `/api/platform/branding` (the product name, which must render before any sign-in),
  `/api/platform/auth-config` (public OIDC client metadata — a client cannot present a token before it
  knows where to get one), the signature-verified Stripe and WhatsApp webhooks, and — **only under
  `Auth:Mode=Local`** — the issuer surface itself: `/.well-known/*` (discovery + JWKS),
  `/connect/authorize|token|logout`, and the `/auth/login*` pages (anonymous by definition: they are
  what creates sessions; rate-limited and lockout-protected). Nothing else.
  If you add health checks that surface sensitive dependency detail, restrict `/health` (auth, or an
  internal-only port).
- Run behind HTTPS; terminate TLS at the ingress (the Container App / reverse proxy).
- Keep the audit connection on an independently credentialed database server. The audit context rejects
  updates/deletes, but database-level retention and restricted administrator access remain operator duties.
- Persist the ASP.NET Data Protection key ring in Redis or set `DataProtection:KeysPath` to shared durable
  storage. Losing the key ring makes OAuth state and DataProtection-vaulted secrets unreadable.

See [ARCHITECTURE.md](ARCHITECTURE.md) for how these pieces fit together.
