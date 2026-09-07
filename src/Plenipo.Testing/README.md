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
        ReadEndpoints: ["/api/finance/transactions", "/api/finance/budgets"]);
}

[CollectionDefinition("api")] public sealed class ApiCollection : ICollectionFixture<Fixture>;

[Collection("api")] public sealed class Spine(Fixture f)    : PlenipoSpineConformance<Program>(f);
[Collection("api")] public sealed class Manifest(Fixture f) : PlenipoManifestConformance<Program>(f);
[Collection("api")] public sealed class Tenancy(Fixture f)  : PlenipoTenancyConformance<Program>(f);
[Collection("api")] public sealed class Evals(Fixture f)    : PlenipoGoldenEvals<Program>(f);
```

Golden eval cases are JSON files under `Evals/cases/`; the package copies them to the test output.
Docker must be running: the fixture boots the real host on a Testcontainers pgvector instance.

The contract this implements, the invariant list, and what each rung proves:
`docs/TESTING_CONTRACT.md` in the Plenipo repository.
