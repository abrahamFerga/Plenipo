using Plenipo.Testing.Evals;

namespace Plenipo.Sample.Host.IntegrationTests.Evals;

/// <summary>
/// Runs every golden-conversation eval under Evals/cases through the real API (full pipeline:
/// auth, RBAC tool filtering, approval gate, audit, Mock provider) and enforces each case's
/// behavioral contract. The runner ships in the Plenipo.Testing kit — this is the same class a
/// product derives — so the platform proves the runner on its own three sample modules. Add a case
/// by dropping a JSON file; no test code.
/// </summary>
[Collection("api")]
public sealed class GoldenConversationEvals(IntegrationFixture fixture) : PlenipoGoldenEvals<Program>(fixture);
