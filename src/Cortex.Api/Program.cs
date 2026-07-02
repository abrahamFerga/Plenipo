using Cortex.AspNetCore.Connectors;
using Cortex.AspNetCore.Hosting;
using Cortex.Connectors.AzureBlob;
using Cortex.Connectors.LocalFolder;
using Cortex.Connectors.MsGraph;
using Cortex.Connectors.Peer;

// ─────────────────────────────────────────────────────────────────────────────
// Cortex base platform host — no domain modules installed here.
//
// To build a domain app on Cortex, create your own API project and:
//   builder.AddCortexPlatform();
//   builder.AddCortexModule<YourModule>();   // from your module NuGet package
//   ...
//   await app.RunCortexPlatformAsync();
//
// See samples/ for a complete example (Finance, Nutrition, Legal).
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddCortexPlatform();

// ── Add your domain modules here, e.g.:
// builder.AddCortexModule<FinanceModule>();

// The built-in data-source connectors ship even on the bare platform (default-off per tenant —
// an admin turns each one on under Integrations). Your own connectors register the same way.
builder.AddCortexConnector<LocalFolderConnector>();
builder.AddCortexConnector<AzureBlobConnector>();
builder.AddCortexConnector<CortexPeerConnector>();
builder.AddCortexConnector<MsGraphConnector>();

var app = builder.Build();

await app.RunCortexPlatformAsync();

/// <summary>Exposed so endpoint tests can host the bare platform via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
