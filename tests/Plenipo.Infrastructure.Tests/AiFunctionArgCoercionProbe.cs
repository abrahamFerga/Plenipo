using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// Probe: confirms an AIFunction created from a typed method coerces JSON-stored arguments back into the
/// method's parameter types. This is the mechanism the approval round-trip relies on to re-execute a
/// blocked tool with its recorded arguments.
/// </summary>
public sealed class AiFunctionArgCoercionProbe
{
    [Fact]
    public async Task AIFunction_CoercesStoredJsonArguments()
    {
        (string Description, decimal Amount, string? Category) captured = default;

        var fn = AIFunctionFactory.Create(
            (string description, decimal amount, string? category) =>
            {
                captured = (description, amount, category);
                return "ok";
            },
            name: "record_transaction");

        // Simulate the recorded arguments (as they'd be persisted as jsonb).
        const string storedJson = """{"description":"OXXO groceries","amount":123.45,"category":"Groceries"}""";
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(storedJson)!
            .ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        var result = await fn.InvokeAsync(new AIFunctionArguments(dict));

        Assert.Equal("OXXO groceries", captured.Description);
        Assert.Equal(123.45m, captured.Amount);
        Assert.Equal("Groceries", captured.Category);
        Assert.NotNull(result);
    }
}
