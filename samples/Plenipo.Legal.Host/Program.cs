using Plenipo.AspNetCore.Connectors;
using Plenipo.AspNetCore.Hosting;
using Plenipo.AspNetCore.Modules;
using Plenipo.Connectors.AzureBlob;
using Plenipo.Connectors.LocalFolder;
using Plenipo.Connectors.MsGraph;
using Plenipo.Connectors.Peer;
using Plenipo.Modules.Legal;

// ─────────────────────────────────────────────────────────────────────────────
// Plenipo for Legal — a SINGLE-VERTICAL system, the real product shape.
//
// Verticals are separate systems: this host installs only the legal module. A
// business that also wants finance runs Plenipo-for-finance as its own system
// (own repo, own deployment, own database) and connects the two by enabling the
// plenipo-peer connector here, pointed at the finance system's URL — the legal
// agent can then ask the finance agent questions, with the finance system
// enforcing its own auth, permissions, and audit.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddPlenipoPlatform();

builder.AddPlenipoModule<LegalModule>();

builder.AddPlenipoConnector<LocalFolderConnector>();
builder.AddPlenipoConnector<AzureBlobConnector>();
builder.AddPlenipoConnector<PlenipoPeerConnector>();
builder.AddPlenipoConnector<MsGraphConnector>();

var app = builder.Build();

await app.RunPlenipoPlatformAsync();

/// <summary>Exposed so integration tests can host this app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
