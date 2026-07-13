using Aspire.Hosting.ApplicationModel;

// Cortex sample-app orchestration — the one-command way to run the FULL stack locally:
//   • Postgres (platform + audit databases) and Redis (SignalR backplane / cache)
//   • the sample API (Cortex.Sample.Host with the Finance + Nutrition + Legal modules)
//   • both front-ends as Vite dev servers: the end-user workspace (@abrahamferga/cortex-ui) and the
//     admin console (@cortex/admin-ui)
//
// The chat assistant runs on the dependency-free "Mock" provider, so everything works with
// zero configuration. Supply a real provider + key without editing this file:
//   dotnet user-secrets --project samples/Cortex.Sample.AppHost set "Parameters:ai-provider" "OpenAI"
//   Then configure each tenant's provider, model, and vaulted key under Admin → AI Settings.
//
// Prerequisites: a container runtime (Docker/Podman) for the DB + Redis, and the front-end deps
// installed once — `corepack enable && pnpm --dir frontend install`.
//
// Run with: dotnet run --project samples/Cortex.Sample.AppHost   (or `aspire run`)

var builder = DistributedApplication.CreateBuilder(args);

// ── Backing services (run as containers locally) ─────────────────────────────
// STABLE dev password (overridable via Parameters:cortex-pg-password in user-secrets). Postgres
// bakes the password into the data volume at first init and never re-reads it — with Aspire's
// default *generated* password, losing/regenerating user-secrets left the volume unopenable
// ("28P01 password authentication failed", API waits forever). A fixed dev default can't drift.
// This is a local demo container on a random localhost port — not a production credential.
var pgPassword = builder.AddParameter("cortex-pg-password", "cortex-dev-only", secret: true);

var postgres = builder.AddPostgres("cortex-pg", password: pgPassword)
    // pgvector-enabled Postgres — the platform's opt-in RAG pipeline needs the vector extension
    // at migration time. pg17 pairs with Aspire's data-volume mount (/var/lib/postgresql/data is
    // exactly its PGDATA; pg18+ images use a versioned layout and refuse that mount). A volume
    // created by a DIFFERENT Postgres image won't attach — `docker volume rm <apphost>-cortex-pg-data`
    // resets the disposable dev data (see GETTING_STARTED troubleshooting, incl. the password-drift row).
    .WithImage("pgvector/pgvector")
    .WithImageTag("pg17")
    .WithDataVolume()
    .WithPgAdmin();

var platformDb = postgres.AddDatabase("cortex-platform");
var auditDb = postgres.AddDatabase("cortex-audit");

var redis = builder.AddRedis("cortex-redis");

// ── Parameters — everything Cortex needs to run, overridable per environment ──
// Defaults keep the stack zero-config (Mock chat provider); override any of these via
// `Parameters:<name>` in user-secrets/env. Commercial API keys are tenant-vaulted in Admin → AI Settings.
var aiProvider = builder.AddParameter("ai-provider", "Mock", publishValueAsDefault: true);
var aiModel = builder.AddParameter("ai-model", "gpt-4o-mini", publishValueAsDefault: true);
var aiEndpoint = builder.AddParameter("ai-endpoint", "", publishValueAsDefault: true);
var ragApiKey = builder.AddParameter("rag-api-key", "", secret: true);

// ── API ──────────────────────────────────────────────────────────────────────
var api = builder.AddProject<Projects.Cortex_Sample_Host>("cortex-sample")
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

// ── Front-ends (Vite dev servers, launched via pnpm) ─────────────────────────
// A missing pnpm otherwise surfaces as an opaque resource failure deep in the dashboard, so check
// it up front and say exactly how to fix it.
if (builder.ExecutionContext.IsRunMode && !ToolExistsOnPath("pnpm"))
{
    throw new DistributedApplicationException(
        "pnpm was not found on PATH, so the cortex-ui / cortex-admin-ui resources cannot start. " +
        "Run `corepack enable` (needs admin rights on Windows — or use `npm install -g pnpm`), " +
        "then start the AppHost again.");
}

// install: true (the default) runs `pnpm install` as an <name>-installer resource before each UI
// starts — a fast no-op when deps are current, and a fresh clone self-installs. In the dashboard
// the installer resources run to completion ("Finished"); they are helpers, not long-running
// services (same for the Aspire-internal "cortex-sample-rebuilder").
var workspace = builder.AddViteApp("cortex-ui", "../../frontend/cortex-ui")
    .WithPnpm()
    .WaitFor(api)
    .WithEnvironment("VITE_API_BASE", api.GetEndpoint("http"))
    .WithExternalHttpEndpoints();

var admin = builder.AddViteApp("cortex-admin-ui", "../../frontend/admin-ui")
    .WithPnpm()
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

// True when `tool` resolves on PATH (Windows launchers included — pnpm installs as pnpm.cmd).
static bool ToolExistsOnPath(string tool)
{
    var extensions = OperatingSystem.IsWindows() ? new[] { ".cmd", ".exe", ".bat", "" } : new[] { "" };
    return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .SelectMany(_ => extensions, (dir, ext) => Path.Combine(dir.Trim('"'), tool + ext))
        .Any(File.Exists);
}
