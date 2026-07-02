using Cortex.AspNetCore.Connectors;
using Cortex.AspNetCore.Hosting;
using Cortex.AspNetCore.Modules;
using Cortex.Connectors.AzureBlob;
using Cortex.Connectors.LocalFolder;
using Cortex.Connectors.Peer;
using Cortex.Modules.Legal;

// ─────────────────────────────────────────────────────────────────────────────
// Cortex for Legal — a SINGLE-VERTICAL system, the real product shape.
//
// Verticals are separate systems: this host installs only the legal module. A
// business that also wants finance runs Cortex-for-finance as its own system
// (own repo, own deployment, own database) and connects the two by enabling the
// cortex-peer connector here, pointed at the finance system's URL — the legal
// agent can then ask the finance agent questions, with the finance system
// enforcing its own auth, permissions, and audit.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddCortexPlatform();

builder.AddCortexModule<LegalModule>();

builder.AddCortexConnector<LocalFolderConnector>();
builder.AddCortexConnector<AzureBlobConnector>();
builder.AddCortexConnector<CortexPeerConnector>();

var app = builder.Build();

await app.RunCortexPlatformAsync();

/// <summary>Exposed so integration tests can host this app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
