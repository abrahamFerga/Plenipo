using Plenipo.AspNetCore.Hosting;
using Plenipo.Connectors;

// ─────────────────────────────────────────────────────────────────────────────
// Plenipo base platform host — no domain modules installed here.
//
// To build a domain app on Plenipo, create your own API project and:
//   builder.AddPlenipoPlatform();
//   builder.AddPlenipoModule<YourModule>();   // from your module NuGet package
//   ...
//   await app.RunPlenipoPlatformAsync();
//
// See samples/ for a complete example (Finance, Nutrition, Legal).
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddPlenipoPlatform();

// ── Add your domain modules here, e.g.:
// builder.AddPlenipoModule<FinanceModule>();

// Every built-in data-source connector ships even on the bare platform (default-off per tenant —
// an admin turns each one on under Integrations). Suppress one by config with Connectors:Exclude;
// your own connectors register individually with builder.AddPlenipoConnector<T>().
builder.AddPlenipoConnectors();

var app = builder.Build();

await app.RunPlenipoPlatformAsync();

/// <summary>Exposed so endpoint tests can host the bare platform via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
