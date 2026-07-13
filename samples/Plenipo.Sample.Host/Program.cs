using Plenipo.Application.Authorization;
using Plenipo.Application.Commerce;
using Plenipo.Sample.Host;
using Plenipo.AspNetCore.Connectors;
using Plenipo.AspNetCore.Hosting;
using Plenipo.AspNetCore.Modules;
using Plenipo.Connectors;
using Plenipo.Modules.Finance;
using Plenipo.Modules.Legal;
using Plenipo.Modules.Nutrition;

// ─────────────────────────────────────────────────────────────────────────────
// Sample application built on the Plenipo base platform.
//
// This is what a real product's host looks like: one AddPlenipoPlatform() call brings
// the whole platform — RBAC, auditing, token usage, the admin dashboard API, AG-UI
// chat — then you install the domain modules you want. Nothing else is required.
// ─────────────────────────────────────────────────────────────────────────────

var builder = WebApplication.CreateBuilder(args);

builder.AddPlenipoPlatform();

// Install the domain modules this product ships with.
builder.AddPlenipoModule<FinanceModule>();
builder.AddPlenipoModule<NutritionModule>();
builder.AddPlenipoModule<LegalModule>();

// Install every built-in data-source connector in one call. Installation only makes them
// available — a tenant admin enables and configures each one under Integrations (default-off) —
// and a deployment can still suppress any of them by config alone (Connectors:Exclude).
builder.AddPlenipoConnectors();
// A connector DEFINED BY THIS HOST (Plenipo.Sample.Host assembly), not shipped in Plenipo.Connectors —
// proves a domain system can add its own connector. Networthy owns its Plaid connector the same way.
builder.AddPlenipoConnector<HostDefinedCrmConnector>();

// What this host SELLS (docs/COMMERCIALIZATION.md): the plan — not checkout metadata — decides
// what a purchase grants. The sample sells the Legal vertical in the three standard tiers.
// A product role: paralegals chat and work the docket/tasks but never file, bill, or
// close. Seeded into every tenant's editable baseline alongside the built-ins.
builder.Services.AddPlenipoRole("paralegal",
[
    "chat.use", "chat.conversations.view", "files.upload", "files.read",
    "tools.documents.read_document", "tools.documents.list_documents",
    "tools.legal.list_matters", "tools.legal.list_deadlines", "tools.legal.add_deadline",
    "tools.legal.complete_deadline", "tools.legal.list_tasks", "tools.legal.add_task",
    "tools.legal.complete_task", "legal.matters.view",
]);

// Both wave-1 host seams together: after any tenant is provisioned (operator call or a
// billing webhook), email the new admin their sign-in details. Best-effort by design.
builder.Services.AddPlenipoTenantProvisionedHook<WelcomeEmailHook>();

builder.Services.AddPlenipoProduct(new ProductOffering
{
    ProductId = "the-lawyer",
    Plans =
    [
        new ProductPlan { Id = "solo", Modules = ["legal"], DefaultSeats = 1, MonthlyTokenBudget = 200_000 },
        new ProductPlan { Id = "team", Modules = ["legal"], DefaultSeats = 5, MonthlyTokenBudget = 500_000 },
        new ProductPlan { Id = "dedicated", Dedicated = true },
    ],
});

var app = builder.Build();

await app.RunPlenipoPlatformAsync();

/// <summary>Exposed so integration tests can host this app via WebApplicationFactory&lt;Program&gt;.</summary>
public partial class Program;
