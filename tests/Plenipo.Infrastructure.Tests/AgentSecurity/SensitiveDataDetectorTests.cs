using Microsoft.Extensions.AI;
using Plenipo.Infrastructure.Agents.Security;
using System.Text.Json;

namespace Plenipo.Infrastructure.Tests.AgentSecurity;

public sealed class SensitiveDataDetectorTests
{
    [Fact]
    public void DetectAndRedact_RemovesPiiAndCredentialsWithoutReturningMatchedValues()
    {
        const string input =
            "Email alice@example.com, SSN 123-45-6789, card 4111 1111 1111 1111, key sk-proj-abcdefghijklmnopqrstuv.";

        var result = SensitiveDataDetector.DetectAndRedact(input);

        Assert.DoesNotContain("alice@example.com", result.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("123-45-6789", result.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("4111 1111 1111 1111", result.RedactedText, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-proj-", result.RedactedText, StringComparison.Ordinal);
        Assert.Contains("Email", result.Categories);
        Assert.Contains("UsSsn", result.Categories);
        Assert.Contains("CreditCard", result.Categories);
        Assert.Contains("Credential", result.Categories);
    }

    [Fact]
    public void DetectAndRedact_DoesNotTreatAnInvalidCardCandidateAsSensitive()
    {
        const string input = "Reference 4111 1111 1111 1112 is deliberately not a valid card.";

        var result = SensitiveDataDetector.DetectAndRedact(input);

        Assert.Equal(input, result.RedactedText);
        Assert.Empty(result.Categories);
    }

    [Fact]
    public void RedactArguments_RewritesStringToolArgumentsInPlace()
    {
        var arguments = new AIFunctionArguments
        {
            ["recipient"] = "alice@example.com",
            ["count"] = 2,
        };

        SensitiveDataDetector.RedactArguments(arguments);

        Assert.Equal("[REDACTED:EMAIL]", arguments["recipient"]);
        Assert.Equal(2, arguments["count"]);
    }

    [Fact]
    public void RedactArguments_TraversesNestedJsonObjectsAndArrays()
    {
        using var json = JsonDocument.Parse(
            """{"customer":{"email":"alice@example.com"},"recipients":["bob@example.com"],"count":2}""");
        var arguments = new AIFunctionArguments
        {
            ["payload"] = json.RootElement.Clone(),
        };

        SensitiveDataDetector.RedactArguments(arguments);

        var serialized = JsonSerializer.Serialize(arguments["payload"]);
        Assert.DoesNotContain("alice@example.com", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("bob@example.com", serialized, StringComparison.Ordinal);
        Assert.Contains("[REDACTED:EMAIL]", serialized, StringComparison.Ordinal);
        Assert.Contains("\"count\":2", serialized, StringComparison.Ordinal);
    }
}
