using System.Net;
using Plenipo.Infrastructure.Agents;
using Xunit;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// Classification is an inference about the AI provider, and it is only sound for exceptions the
/// provider raised. A module tool calling a third-party API raises the same exception types with the
/// same statuses — so without a boundary marker, a connector 401 is reported as
/// <c>"The AI provider rejected the configured API key"</c>: confident, actionable, and pointing the
/// administrator at a screen where nothing is wrong. That is a worse failure than the opaque string
/// #131 set out to replace, because the reader has no reason to doubt it.
/// </summary>
public sealed class ToolFailureAttributionTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void A_connectors_own_status_is_never_read_as_the_providers(HttpStatusCode status)
    {
        var connectorFailure = new HttpRequestException("upstream said no", inner: null, status);
        var fromTool = new ToolInvocationFailedException("sync_ledger", connectorFailure);

        // The same exception, unmarked, is what the provider path classifies — so this pins the marker
        // as the thing doing the work, not some incidental property of the exception.
        Assert.NotEqual(AgentTurnFailure.Generic, AgentTurnFailure.Describe(connectorFailure));
        Assert.Equal(AgentTurnFailure.Generic, AgentTurnFailure.Describe(fromTool));
    }

    /// <summary>
    /// The classifier half of the tool-timeout case. That the middleware actually PRODUCES this shape —
    /// rather than rethrowing a tool's own HttpClient timeout bare, which would strand it here as an
    /// unmarked <see cref="TaskCanceledException"/> and have it read as a provider timeout — is pinned
    /// separately by <c>ToolApprovalTests.A_tools_own_timeout_is_marked_rather_than_read_as_the_providers</c>,
    /// which drives the middleware. Neither test is sufficient alone: this one would stay green against a
    /// middleware that never marks this path.
    /// </summary>
    [Fact]
    public void A_tool_that_times_out_does_not_become_a_provider_timeout()
    {
        var toolTimeout = new ToolInvocationFailedException(
            "fetch_statements", new TaskCanceledException("slow", new TimeoutException()));

        Assert.Equal(AgentTurnFailure.Generic, AgentTurnFailure.Describe(toolTimeout));

        // The same exception unmarked is what the bug looked like: a healthy provider blamed for a slow tool.
        Assert.Equal(AgentTurnFailure.TimedOut,
            AgentTurnFailure.Describe(new TaskCanceledException("slow", new TimeoutException())));
    }

    [Fact]
    public void A_tool_failure_stays_generic_even_under_the_pipelines_own_wrapping()
    {
        // Retries exhausted around a tool call: the marker is no longer the outermost exception.
        var wrapped = new AggregateException(
            new ToolInvocationFailedException(
                "sync_ledger", new HttpRequestException("nope", inner: null, HttpStatusCode.Unauthorized)));

        Assert.Equal(AgentTurnFailure.Generic, AgentTurnFailure.Describe(wrapped));
    }

    [Fact]
    public void A_provider_failure_in_a_later_branch_is_still_found()
    {
        // Following InnerException alone reads only InnerExceptions[0] and loses every other branch,
        // so a real provider failure raised alongside an unattributable one would report as Generic.
        var aggregate = new AggregateException(
            new InvalidOperationException("no evidence here"),
            new HttpRequestException("rejected", inner: null, HttpStatusCode.Unauthorized));

        Assert.Equal(AgentTurnFailure.KeyRejected, AgentTurnFailure.Describe(aggregate));
    }

    [Fact]
    public void A_cyclic_chain_terminates_rather_than_hanging_the_turn()
    {
        // Depth is bounded, so a self-referencing chain cannot spin inside the failure path.
        var inner = new InvalidOperationException("inner");
        var outer = new AggregateException(inner, new AggregateException(inner));

        Assert.Equal(AgentTurnFailure.Generic, AgentTurnFailure.Describe(outer));
    }
}
