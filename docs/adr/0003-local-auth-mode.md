# 3. Local auth mode: the host as its own OpenID Connect issuer

Date: 2026-08-06

## Status

Accepted

## Context

Plenipo's production authentication is IdP-agnostic OIDC bearer-JWT: `Auth:Authority` + `Auth:Audience`
point at any compliant issuer (Entra External ID is the documented default; Keycloak and Authentik work
unchanged), and the platform deliberately holds **no credential store** — users are JIT-provisioned from
the token's `sub`, and everything below the token (tenant resolution, RBAC, audit) is already ours.

That leaves one deployment shape unserved: **on-premises and mini-PC installs** where the buyer will not
stand up Entra — or any external IdP at all. For them "set up an identity provider first" is the single
biggest install-time cliff, and bundling Keycloak/Authentik in Compose only relocates the cliff: a
0.5–1 GB JVM/Python sidecar, a second admin console, and a second user store duplicating the users,
invites, roles, and seat limits Plenipo's database and admin UI already own.

The alternatives considered:

1. **Bundle an OSS IdP in Compose** — zero platform code, but heavyweight on a mini PC, a duplicated
   user store, and exactly the setup friction the requirement exists to remove. Remains available to
   any customer anyway, because generic OIDC mode is unchanged.
2. **Duende IdentityServer** — the polished .NET OIDC server, but commercially licensed; redistributing
   it inside an on-prem product means per-customer licensing entanglement (COMMERCIALIZATION.md).
3. **A bespoke `/api/auth/login` that mints JWTs** — least code up front, but it forks the SPA (a second
   non-OIDC token flow), forks token issuance/refresh/revocation on the server, gives mobile nothing
   standard, and hand-rolls precisely the protocol surface (single-use codes, rotation, replay) that
   should not be hand-rolled.
4. **Embed OpenIddict and become the issuer** — the host itself serves discovery, `/connect/authorize`,
   `/connect/token`, and a small branded login page. Apache-2.0, actively maintained, and 7.6.0 ships a
   `net10.0` target built against the exact EF Core 10.0.10 this repo pins.

## Decision

Option 4. A new explicit **`Auth:Mode = Local`** makes the platform host its own OIDC issuer:

- **OpenIddict server** (authorization code + PKCE + refresh tokens, `offline_access`) with EF Core
  stores on `PlatformDbContext`. Access tokens are plain signed JWTs (access-token encryption disabled)
  so the **resource side stays byte-for-byte the existing `JwtBearer` path** — same SignalR
  query-token hookup, same `RequestEnricher`, same `Auth:RequireMfa` backstop (the local issuer emits
  `amr: ["pwd"|"otp"]`). Issuer validation is off in Local mode: the per-deployment signing key — never
  shared, generated on first run — is the trust anchor, and a mini PC is legitimately reached by
  hostname and by IP at once, so pinning one issuer string would break the other path. Audience is
  pinned (`plenipo`).
- **Credentials live on the existing platform `User`**: a new `LocalCredential` row (PBKDF2 hash via
  ASP.NET Core Identity's `PasswordHasher` — the hasher alone, not the Identity framework), with
  lockout counters, a security stamp (rotated on password change; checked on refresh so a reset ends
  stolen sessions), forced-change-on-first-login, and an optional TOTP secret (RFC 6238, implemented
  in-repo with test vectors — no new dependency — manual-entry key + `otpauth://` link, no QR
  library). Recovery is administrative: an admin resets the password or the TOTP enrollment; there are
  no recovery codes to manage in v1.
- **Login UI is server-rendered by the host** (no Razor: string-built, branded via
  `Branding:ProductName`), with a double-submit CSRF token, an IP-partitioned rate-limit policy, and
  multi-step stages (password → forced change → TOTP) carried in a Data-Protection-signed step token so
  the server stays stateless until the final cookie sign-in. The cookie session authenticates only the
  issuer surface; APIs still take bearer tokens only.
- **Redirect URI validation is same-host-by-path**: a custom application manager accepts exactly
  `/signin-callback` and `/admin/signin-callback` on the requesting host, because the SPA is served by
  the host itself and the host's name legitimately varies (LAN IP, mDNS name, port). Wildcards are not
  accepted; foreign hosts are not accepted.
- **Signing/encryption keys** are generated on first Local-mode startup and stored in the platform
  database protected by Data Protection — whose key ring the host already refuses to run without
  (Redis or `DataProtection:KeysPath`) outside Development.
- **Bootstrap extends, not forks**: the existing `Bootstrap` section gains `AdminInitialPassword`; in
  Local mode the platform mints the admin's subject itself (so the "operator roles need an explicit
  subject" guard is satisfied by construction) and, when no password is configured, generates a
  temporary one and prints it once to the startup log — forced change at first sign-in either way.
- **User management completes in the existing admin surface**: create-user-with-temporary-password
  (email invites keep working when SMTP is configured, but are never the only path — a mini PC often
  has no mail), reset password, unlock, reset TOTP — all behind the existing `platform.users.manage`
  / `platform.roles.manage` permissions, all answering 409 outside Local mode (the
  `RequiresDatabaseAuthorizationFilter` precedent).
- **The SPA is unchanged.** `/api/platform/auth-config` answers `mode: "oidc"` with the host's own
  origin as authority, the built-in public client id, and `offline_access` — the existing hand-rolled
  PKCE flow needs no new branch. `Auth:Mode` unset preserves today's behavior exactly (configured
  authority → JWT; Development → dev headers; otherwise startup throw); `Local` is explicit opt-in so
  the fail-fast default never silently weakens.

Local email addresses must be unique across the deployment's tenants (enforced at credential creation),
so the login form never needs tenant disambiguation; the issued token carries the user's tenant slug in
the `tenant` claim, and `Auth:PermissionSource` must be `Database` (Token mode contradicts an issuer
whose only role authority is that same database).

## Consequences

- On-prem installs need exactly two containers (host + Postgres, Redis optional) and zero external
  identity setup; `plenipo init` offers "Built-in sign-in" as a first-class choice.
- The platform takes on issuer-grade responsibilities: password hashing, lockout, key custody, token
  pruning (a daily background sweep), and a login page as an attack surface — bounded by rate
  limiting, audited through the existing `AuthEvents` trail, and exercised by integration tests that
  drive the full browser flow.
- OpenIddict tables ride along in every deployment's schema (empty outside Local mode) so migrations
  do not fork by mode.
- A future LDAP/Active Directory bind can slot in as an alternative credential check behind the same
  embedded issuer without touching the token surface; SaaS/Entra deployments are untouched.
