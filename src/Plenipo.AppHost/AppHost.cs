using Aspire.Hosting.ApplicationModel;

// Plenipo local orchestration — the full platform stack in one command:
//   • a Postgres server hosting the platform + audit databases
//   • a Redis instance backing the SignalR backplane (the API picks it up via AddPlenipoRealtime)
//   • the Plenipo API wired to all of them
//   • both front-ends as Vite dev servers: the end-user workspace (@plenipo/ui) and the
//     admin console (@plenipo/admin-ui)
//
// The chat assistant defaults to the dependency-free "Mock" provider, so the stack runs with zero
// configuration. Supply a real provider + key without editing this file:
//   dotnet user-secrets --project src/Plenipo.AppHost set "Parameters:ai-provider" "OpenAI"
//   Then configure each tenant's provider, model, and vaulted key under Admin → AI Settings.
//
// Prerequisites: a container runtime (Docker/Podman) for the DB + Redis, and the front-end deps
// installed once — `corepack enable && pnpm --dir frontend install`.
//
// Run with: dotnet run --project src/Plenipo.AppHost   (or `aspire run`)

var builder = DistributedApplication.CreateBuilder(args);

// ── Backing services (run as containers locally) ─────────────────────────────
var postgres = builder.AddPostgres("plenipo-pg")
    // pgvector-enabled Postgres — the platform's opt-in RAG pipeline needs the vector extension
    // at migration time. pg17 pairs with Aspire's data-volume mount (/var/lib/postgresql/data is
    // exactly its PGDATA; pg18+ images use a versioned layout and refuse that mount). A volume
    // created by a DIFFERENT Postgres image won't attach — `docker volume rm <apphost>-plenipo-pg-data`
    // resets the disposable dev data (see GETTING_STARTED gotchas).
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume()
    .WithPgAdmin();

var platformDb = postgres.AddDatabase("plenipo-platform");
var auditDb = postgres.AddDatabase("plenipo-audit");

var redis = builder.AddRedis("plenipo-redis");

// ── Parameters — everything Plenipo needs to run, overridable per environment ──
// Defaults keep the stack zero-config (Mock chat provider); override any of these via
// `Parameters:<name>` in user-secrets/env. Commercial API keys are tenant-vaulted in Admin → AI Settings.
var aiProvider = builder.AddParameter("ai-provider", "Mock", publishValueAsDefault: true);
var aiModel = builder.AddParameter("ai-model", "gpt-4o-mini", publishValueAsDefault: true);
var aiEndpoint = builder.AddParameter("ai-endpoint", "", publishValueAsDefault: true);
var ragApiKey = builder.AddParameter("rag-api-key", "", secret: true);

// ── API ──────────────────────────────────────────────────────────────────────
var api = builder.AddProject<Projects.Plenipo_Api>("plenipo-api")
    .WithReference(platformDb)
    .WithReference(auditDb)
    .WithReference(redis)
    .WaitFor(platformDb)
    .WaitFor(auditDb)
    .WithEnvironment("Ai__Provider", aiProvider)
    .WithEnvironment("Ai__Model", aiModel)
    .WithEnvironment("Ai__Endpoint", aiEndpoint)
    .WithEnvironment("Rag__ApiKey", ragApiKey)
    .WithExternalHttpEndpoints();

// ── Front-ends (Vite dev servers; pnpm workspace deps installed once, so no per-app install) ──
var workspace = builder.AddViteApp("plenipo-ui", "../../frontend/plenipo-ui")
    .WithPnpm(install: false)
    .WaitFor(api)
    .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

var admin = builder.AddViteApp("plenipo-admin-ui", "../../frontend/admin-ui")
    .WithPnpm(install: false)
    .WaitFor(api)
    .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
    .WithEnvironment("VITE_WORKSPACE_URL", workspace.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

// The workspace's "Admin" link targets the admin console (Vite serves it under its /admin base).
workspace.WithEnvironment(
    "VITE_ADMIN_URL",
    ReferenceExpression.Create($"{admin.GetEndpoint("http")}/admin"));

// Teach the API's CORS policy the front-end origins. Aspire assigns the UI ports dynamically, so
// reference their endpoints; the fixed localhost ports cover running `pnpm dev` outside Aspire.
api.WithEnvironment("Cors__Origins__0", workspace.GetEndpoint("http"))
   .WithEnvironment("Cors__Origins__1", admin.GetEndpoint("http"))
   .WithEnvironment("Cors__Origins__2", "http://localhost:5173")
   .WithEnvironment("Cors__Origins__3", "http://localhost:5174");

builder.Build().Run();
