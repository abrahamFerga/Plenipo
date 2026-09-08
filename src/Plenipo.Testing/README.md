# Plenipo.Testing

The conformance kit for products built on Plenipo. The platform publishes its tests; a product
executes them against its own host on every pull request. The kit ships at the platform version,
so upgrading `PlenipoVersion` upgrades the invariants.

```csharp
// tests/<Product>.IntegrationTests/Fixture.cs — the only harness file a product owns
public sealed class Fixture : PlenipoHostFixture<Program>
{
    public override ProductContract Contract { get; } = new(
        ModuleId:      "finance",
        ReadTool:      "summarize_spending",
        WriteTool:     "record_transaction",   // must be RequiresApproval = true
        ReadEndpoints: ["/api/finance/transactions", "/api/finance/budgets"])
    {
        // Rows every new tenant gets on first read (a starter taxonomy): the tenancy pack then checks
        // they are the tenant's OWN rows — disjoint by id from the first tenant's — instead of absent.
        // (A singleton tab's document needs no declaration: it is the tenant's own by definition.)
        SeededReadEndpoints = ["/api/finance/categories"],
    };
}

[CollectionDefinition("api")] public sealed class ApiCollection : ICollectionFixture<Fixture>;

[Collection("api")] public sealed class Spine(Fixture f)    : PlenipoSpineConformance<Program>(f);
[Collection("api")] public sealed class Manifest(Fixture f) : PlenipoManifestConformance<Program>(f);
[Collection("api")] public sealed class Tenancy(Fixture f)  : PlenipoTenancyConformance<Program>(f);
[Collection("api")] public sealed class RedTeam(Fixture f)  : PlenipoRedTeamConformance<Program>(f);
[Collection("api")] public sealed class Evals(Fixture f)    : PlenipoGoldenEvals<Program>(f);
```

When the write tool validates its arguments — a list that must not be empty, a code it looks up —
give `WritePrompt` a JSON object: `"Please advance candidates for me, using a tool. {\"references\":[\"alice\"]}"`.
The Mock provider takes the declared parameters in that object verbatim and fills the rest as usual.

Golden eval cases are JSON files under `Evals/cases/`; the package copies them to the test output.
Docker must be running: the fixture boots the real host on a Testcontainers pgvector instance.

The contract this implements, the invariant list, and what each rung proves:
`docs/TESTING_CONTRACT.md` in the Plenipo repository.
