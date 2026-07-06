using Cortex.Application.Authorization;
using Cortex.Core.Multitenancy;
using Cortex.Modules.Finance.Persistence;
using Cortex.Modules.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Modules.Finance;

/// <summary>
/// The Finance vertical — the first domain module, ported from the-ledger. Demonstrates the full
/// module contract: a manifest declaring tools and tabs, a tool source, and its own endpoints.
/// </summary>
public sealed class FinanceModule : IModule
{
    public const string Id = "finance";

    public const string ViewTransactions = "finance.transactions.view";
    public const string ViewBudgets = "finance.budgets.view";
    public const string EditBudgets = "finance.budgets.edit";
    public const string ManageRules = "finance.rules.manage";
    public const string EditTransactions = "finance.transactions.edit";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Finance",
        Version = "1.0.0",
        Description = "Personal and business finance assistant. Categorize transactions, track budgets, and ask money questions.",
        Icon = "wallet",
        AgentInstructions =
            "You are Cortex's finance assistant. Help the user categorize transactions, summarize spending, " +
            "and reason about budgets. Use the available tools for categorization and summaries. " +
            "Do not provide regulated investment, tax, or legal advice; suggest consulting a professional instead.",
        SuggestedPrompts =
        [
            "Summarize my spending",
            "Am I over budget on anything?",
            "Record a 250 MXN dinner expense",
        ],
        Roles = ["finance:user", "finance:admin"],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "categorize_transaction",
                Description = "Categorize a transaction into a spending category from its description and amount.",
                Permission = Permissions.ForTool(Id, "categorize_transaction"),
            },
            new ToolDescriptor
            {
                Name = "summarize_spending",
                Description = "Summarize spending from category totals.",
                Permission = Permissions.ForTool(Id, "summarize_spending"),
            },
            new ToolDescriptor
            {
                Name = "check_budget",
                Description = "Compare recent spending to the monthly budget for a category (or all categories).",
                Permission = Permissions.ForTool(Id, "check_budget"),
            },
            new ToolDescriptor
            {
                Name = "record_transaction",
                Description = "Record a new transaction in the ledger. Side-effecting: writes data and requires human approval.",
                Permission = Permissions.ForTool(Id, "record_transaction"),
                RequiresApproval = true,
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/finance/chat", Icon = "message-circle", Order = 0 },
            new TabDescriptor
            {
                Id = "transactions", Label = "Transactions", Route = "/finance/transactions", Icon = "list", Order = 1,
                Permission = ViewTransactions,
                DataEndpoint = "/api/finance/transactions",
                Columns =
                [
                    new("date", "Date"), new("description", "Description"), new("category", "Category"),
                    new("direction", "Type"), new("amount", "Amount"), new("currency", "Ccy"),
                ],
            },
            new TabDescriptor
            {
                Id = "budgets", Label = "Budgets", Route = "/finance/budgets", Icon = "pie-chart", Order = 2,
                Permission = ViewBudgets,
                DataEndpoint = "/api/finance/budgets",
                Columns = [new("category", "Category"), new("monthlyLimit", "Monthly limit"), new("currency", "Ccy")],
                // Budgets are simple per-category limits - a natural fit for the generic table
                // editor (POST /budgets upserts by category). Numeric field posts a JSON number.
                Editor = new TabEditor
                {
                    UpsertEndpoint = "/api/finance/budgets",
                    Permission = EditBudgets,
                    KeyField = "category",
                    Fields =
                    [
                        new("category", "Category"),
                        new("monthlyLimit", "Monthly limit", Numeric: true),
                        new("currency", "Currency (blank keeps current)", Required: false),
                    ],
                },
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<FinanceCategorizer>();
        services.AddScoped<FinanceLedgerTools>();
        services.AddSingleton<IModuleToolSource, FinanceToolSource>();

        // The module owns its data under the 'finance' schema of the platform database.
        services.AddDbContext<FinanceDbContext>(options =>
            options.UseNpgsql(configuration.GetConnectionString(FinanceDbContext.ConnectionName)));
    }

    public async Task MigrateAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var db = services.GetRequiredService<FinanceDbContext>();
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>
    /// Seeds a small, realistic demo ledger so the Transactions / Budgets tabs and the
    /// <c>summarize_spending</c> / <c>check_budget</c> tools have something to show out of the box.
    /// The data is tenant-owned, so it only seeds when an ambient tenant is present — which the host
    /// establishes (the dev tenant) in Development. In production this no-ops.
    /// </summary>
    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var tenant = services.GetRequiredService<ITenantContext>();
        if (!tenant.HasTenant)
        {
            return;
        }

        var db = services.GetRequiredService<FinanceDbContext>();

        // Idempotent: only populate a fresh, empty ledger so we never duplicate or fight real data.
        if (await db.Transactions.AnyAsync(cancellationToken) || await db.Budgets.AnyAsync(cancellationToken))
        {
            return;
        }

        SeedDemoLedger(db, tenant.RequireTenantId());
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Builds the demo ledger: monthly budgets plus a month of categorized activity where
    /// Groceries/Transport/Utilities stay under budget but Dining and Entertainment run over — so the
    /// budget and summary tools have a concrete story to report.</summary>
    private static void SeedDemoLedger(FinanceDbContext db, Guid tenantId)
    {
        const string ccy = "MXN";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        db.Budgets.AddRange(
            NewBudget(tenantId, "Groceries", 8000m, ccy),
            NewBudget(tenantId, "Dining", 3000m, ccy),
            NewBudget(tenantId, "Transport", 2000m, ccy),
            NewBudget(tenantId, "Utilities", 2500m, ccy),
            NewBudget(tenantId, "Entertainment", 1500m, ccy));

        var debits = new (string Category, string Description, decimal Amount, int DaysAgo)[]
        {
            ("Groceries", "Soriana weekly shop", 1240.50m, 26),
            ("Groceries", "La Comer produce", 865.00m, 19),
            ("Groceries", "Costco run", 1980.75m, 12),
            ("Groceries", "OXXO snacks", 142.00m, 4),
            ("Dining", "Tacos El Califa", 320.00m, 24),
            ("Dining", "Sushi night", 1180.00m, 17),
            ("Dining", "Cafe con Leche", 145.00m, 9),
            ("Dining", "Birthday dinner", 1620.00m, 3),
            ("Transport", "Uber to the airport", 410.00m, 21),
            ("Transport", "Metro card top-up", 200.00m, 8),
            ("Utilities", "CFE electricity", 1340.00m, 14),
            ("Utilities", "Internet (Totalplay)", 699.00m, 14),
            ("Entertainment", "Cinepolis VIP", 540.00m, 15),
            ("Entertainment", "Concert tickets", 1300.00m, 6),
        };

        foreach (var (category, description, amount, daysAgo) in debits)
        {
            db.Transactions.Add(new FinanceTransaction
            {
                TenantId = tenantId,
                Date = today.AddDays(-daysAgo),
                Description = description,
                Amount = amount,
                Currency = ccy,
                Direction = TransactionDirection.Debit,
                Category = category,
                CategorizationSource = CategorizationSource.Manual,
                Confidence = 1.0d,
            });
        }

        // One income credit so the ledger isn't pure outflow.
        db.Transactions.Add(new FinanceTransaction
        {
            TenantId = tenantId,
            Date = today.AddDays(-28),
            Description = "Payroll deposit",
            Amount = 28000m,
            Currency = ccy,
            Direction = TransactionDirection.Credit,
            Category = "Income",
            CategorizationSource = CategorizationSource.Manual,
            Confidence = 1.0d,
        });
    }

    private static Budget NewBudget(Guid tenantId, string category, decimal limit, string currency) => new()
    {
        TenantId = tenantId,
        Category = category,
        MonthlyLimit = limit,
        Currency = currency,
    };

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/finance").WithTags("Finance").RequireAuthorization();

        // The tenant's stored transactions (most recent first).
        group.MapGet("/transactions", async (FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var items = await db.Transactions
                    .OrderByDescending(t => t.Date)
                    .Take(200)
                    .Select(t => new TransactionDto(t.Id, t.Date, t.Description, t.Amount, t.Currency, t.Direction.ToString(), t.Category))
                    .ToListAsync(cancellationToken);
                return Results.Ok(items);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewTransactions))
            .WithName("Finance_GetTransactions");

        // Ingest a transaction: auto-categorize via the deterministic chain, then store it.
        group.MapPost("/transactions", async (
                AddTransactionRequest body,
                FinanceDbContext db,
                FinanceCategorizer categorizer,
                ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                var outcome = await categorizer.ResolveAsync(body.Description, cancellationToken);
                var transaction = new FinanceTransaction
                {
                    TenantId = tenant.RequireTenantId(),
                    Date = body.Date ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    Description = body.Description,
                    Amount = Math.Abs(body.Amount),
                    Currency = string.IsNullOrWhiteSpace(body.Currency) ? "MXN" : body.Currency!,
                    Direction = body.Amount < 0 ? TransactionDirection.Credit : TransactionDirection.Debit,
                    Category = outcome.Category,
                    CategorizationSource = outcome.Source,
                    Confidence = outcome.Source == CategorizationSource.None ? null : outcome.Confidence,
                };
                db.Transactions.Add(transaction);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Created($"/api/finance/transactions/{transaction.Id}", new { transaction.Id, transaction.Category });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewTransactions))
            .WithName("Finance_AddTransaction");

        // Correct a transaction's category and (optionally) learn from it — the feedback loop that
        // makes future categorization smarter, ported from the-ledger.
        group.MapPost("/transactions/{id:guid}/recategorize", async (
                Guid id,
                RecategorizeRequest body,
                FinanceDbContext db,
                ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                var transaction = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
                if (transaction is null)
                {
                    return Results.NotFound();
                }

                transaction.Category = body.Category;
                transaction.CategorizationSource = CategorizationSource.Manual;
                transaction.Confidence = 1.0d;

                string? learnedPattern = null;
                if (body.Learn)
                {
                    var pattern = string.IsNullOrWhiteSpace(body.MatchPattern)
                        ? FinanceCategorization.DeriveMatchPattern(transaction.Description)
                        : body.MatchPattern!.Trim().ToUpperInvariant();

                    if (pattern is not null)
                    {
                        var alreadyKnown = await db.CategorizationRules
                            .AnyAsync(r => r.MatchPattern == pattern && r.Category == body.Category, cancellationToken);
                        if (!alreadyKnown)
                        {
                            db.CategorizationRules.Add(new LearnedCategorizationRule
                            {
                                TenantId = tenant.RequireTenantId(),
                                MatchPattern = pattern,
                                Category = body.Category,
                                Priority = 100,
                            });
                            learnedPattern = pattern;
                        }
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new { transaction.Id, transaction.Category, LearnedPattern = learnedPattern });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(EditTransactions))
            .WithName("Finance_Recategorize");

        // Budgets: the tenant's monthly category caps.
        group.MapGet("/budgets", async (FinanceDbContext db, CancellationToken cancellationToken) =>
            {
                var budgets = await db.Budgets
                    .OrderBy(b => b.Category)
                    .Select(b => new BudgetDto(b.Id, b.Category, b.MonthlyLimit, b.Currency))
                    .ToListAsync(cancellationToken);
                return Results.Ok(budgets);
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ViewBudgets))
            .WithName("Finance_GetBudgets");

        // Create or update the monthly limit for a category.
        group.MapPost("/budgets", async (
                SetBudgetRequest body,
                FinanceDbContext db,
                ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                var existing = await db.Budgets.FirstOrDefaultAsync(b => b.Category == body.Category, cancellationToken);
                if (existing is null)
                {
                    existing = new Budget
                    {
                        TenantId = tenant.RequireTenantId(),
                        Category = body.Category,
                        MonthlyLimit = body.MonthlyLimit,
                        Currency = string.IsNullOrWhiteSpace(body.Currency) ? "MXN" : body.Currency!,
                    };
                    db.Budgets.Add(existing);
                }
                else
                {
                    existing.MonthlyLimit = body.MonthlyLimit;
                    if (!string.IsNullOrWhiteSpace(body.Currency))
                    {
                        existing.Currency = body.Currency!;
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                return Results.Ok(new BudgetDto(existing.Id, existing.Category, existing.MonthlyLimit, existing.Currency));
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(EditBudgets))
            .WithName("Finance_SetBudget");

        // Teach the categorizer a tenant-specific rule (the-ledger's "learn from corrections").
        group.MapPost("/categorization-rules", async (
                LearnRuleRequest body,
                FinanceDbContext db,
                ITenantContext tenant,
                CancellationToken cancellationToken) =>
            {
                var rule = new LearnedCategorizationRule
                {
                    TenantId = tenant.RequireTenantId(),
                    MatchPattern = body.MatchPattern,
                    Category = body.Category,
                    Priority = body.Priority,
                };
                db.CategorizationRules.Add(rule);
                await db.SaveChangesAsync(cancellationToken);
                return Results.Created($"/api/finance/categorization-rules/{rule.Id}", new { rule.Id });
            })
            .RequireAuthorization(PermissionRequirement.PolicyName(ManageRules))
            .WithName("Finance_LearnRule");
    }

    /// <summary>Request to teach the categorizer that descriptions containing a pattern map to a category.</summary>
    public sealed record LearnRuleRequest(string MatchPattern, string Category, int Priority = 100);

    /// <summary>Request to set the monthly spending limit for a category.</summary>
    public sealed record SetBudgetRequest(string Category, decimal MonthlyLimit, string? Currency = null);

    private sealed record BudgetDto(Guid Id, string Category, decimal MonthlyLimit, string Currency);

    /// <summary>Request to ingest a transaction. Negative <paramref name="Amount"/> is treated as a credit/deposit.</summary>
    public sealed record AddTransactionRequest(string Description, decimal Amount, DateOnly? Date = null, string? Currency = null);

    /// <summary>
    /// Request to correct a transaction's category. When <paramref name="Learn"/> is true a learned rule
    /// is created (from <paramref name="MatchPattern"/>, or derived from the description) so future
    /// matching transactions categorize automatically.
    /// </summary>
    public sealed record RecategorizeRequest(string Category, bool Learn = true, string? MatchPattern = null);

    private sealed record TransactionDto(Guid Id, DateOnly Date, string Description, decimal Amount, string Currency, string Direction, string? Category);
}
