using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using Azure;
using Plenipo.Infrastructure.Agents;
using Xunit;

namespace Plenipo.Infrastructure.Tests;

/// <summary>
/// A failed turn has to tell the tenant which of several very different problems they hit — a rejected
/// key, an exhausted quota, a missing model, a provider outage — because on a hosted deployment the log
/// line carrying the real reason belongs to the operator, not to them. These tests pin the mapping, and
/// pin the part that matters most: the provider's own words never reach the caller.
/// </summary>
public sealed class AgentTurnFailureTests
{
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public void A_rejected_key_sends_the_administrator_to_AI_settings(HttpStatusCode status)
    {
        var message = AgentTurnFailure.Describe(ClientFailure(status));

        Assert.Equal(AgentTurnFailure.KeyRejected, message);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.PaymentRequired)]
    public void A_spent_quota_reads_as_a_quota_problem_not_a_broken_app(HttpStatusCode status)
    {
        Assert.Equal(AgentTurnFailure.QuotaExhausted, AgentTurnFailure.Describe(ClientFailure(status)));
    }

    [Fact]
    public void A_model_the_connection_does_not_carry_names_the_model_as_the_fix()
    {
        Assert.Equal(AgentTurnFailure.ModelUnknown, AgentTurnFailure.Describe(ClientFailure(HttpStatusCode.NotFound)));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void A_provider_outage_says_there_is_nothing_to_configure(int status)
    {
        Assert.Equal(AgentTurnFailure.ProviderFailing, AgentTurnFailure.Describe(new ClientResultException("boom", Response(status))));
    }

    [Fact]
    public void An_Azure_pipeline_failure_classifies_the_same_way_as_a_ClientModel_one()
    {
        // Azure.AI.OpenAI reaches for RequestFailedException on the paths that predate ClientModel
        // (managed identity, for one), and a 401 there is the same problem for the tenant.
        Assert.Equal(AgentTurnFailure.KeyRejected, AgentTurnFailure.Describe(new RequestFailedException(401, "unauthorized")));
    }

    [Fact]
    public void A_status_carried_on_a_plain_HttpRequestException_is_still_read()
    {
        var failure = new HttpRequestException("rate limited", inner: null, HttpStatusCode.TooManyRequests);

        Assert.Equal(AgentTurnFailure.QuotaExhausted, AgentTurnFailure.Describe(failure));
    }

    [Fact]
    public void A_request_that_never_reached_the_provider_reads_as_unreachable()
    {
        // No status: DNS, TLS, a refused connection, or the outbound-URL policy blocking the endpoint.
        Assert.Equal(AgentTurnFailure.Unreachable, AgentTurnFailure.Describe(new HttpRequestException("no such host")));
    }

    [Fact]
    public void A_refused_connection_is_read_through_the_pipelines_own_wrapping()
    {
        // The shape a live refused connection actually produces, captured from the ClientModel pipeline
        // against a dead endpoint: retries exhaust into an AggregateException, and the ClientResultException
        // beneath it reports Status = 0 because no response ever came back. Reading that 0 as a status is
        // what made this case report as the generic failure in the first runtime run of this fix.
        var refused = new AggregateException(
            "Retry failed after 4 tries.",
            new ClientResultException(
                "No connection could be made because the target machine actively refused it. (127.0.0.1:11434)",
                Response(0),
                new HttpRequestException(
                    "No connection could be made because the target machine actively refused it.",
                    new System.Net.Sockets.SocketException(10061))));

        Assert.Equal(AgentTurnFailure.Unreachable, AgentTurnFailure.Describe(refused));
    }

    [Fact]
    public void A_provider_read_that_times_out_says_so()
    {
        Assert.Equal(AgentTurnFailure.TimedOut, AgentTurnFailure.Describe(new TimeoutException()));
        // The runner only routes a cancellation here once it knows its own token was not the source.
        Assert.Equal(AgentTurnFailure.TimedOut, AgentTurnFailure.Describe(new TaskCanceledException()));
    }

    [Fact]
    public void A_wrapped_provider_failure_is_found_through_the_inner_exception()
    {
        var wrapped = new InvalidOperationException("agent pipeline", ClientFailure(HttpStatusCode.Unauthorized));

        Assert.Equal(AgentTurnFailure.KeyRejected, AgentTurnFailure.Describe(wrapped));
    }

    [Fact]
    public void An_unattributable_failure_keeps_the_generic_message()
    {
        // Narrowing the opaque case must not mean guessing at one. A 400 is usually the platform's own
        // request being wrong, which is not something the tenant can act on.
        Assert.Equal(AgentTurnFailure.Generic, AgentTurnFailure.Describe(new InvalidOperationException("something else")));
        Assert.Equal(AgentTurnFailure.Generic, AgentTurnFailure.Describe(ClientFailure(HttpStatusCode.BadRequest)));
    }

    [Fact]
    public void The_providers_own_words_never_reach_the_caller()
    {
        // Provider errors quote request payloads, org ids, and endpoint URLs. The classification is a
        // fixed platform string or nothing; the exception goes to the log and stays there.
        const string Leaky = "Incorrect API key sk-live-4f9c provided for org-acme at https://api.example.com/v1/chat";

        foreach (var failure in new Exception[]
        {
            new ClientResultException(Leaky, Response(401)),
            new RequestFailedException(429, Leaky),
            new HttpRequestException(Leaky, inner: null, HttpStatusCode.NotFound),
            new InvalidOperationException(Leaky),
        })
        {
            var message = AgentTurnFailure.Describe(failure);

            Assert.DoesNotContain("sk-live-4f9c", message, StringComparison.Ordinal);
            Assert.DoesNotContain("org-acme", message, StringComparison.Ordinal);
            Assert.DoesNotContain("api.example.com", message, StringComparison.Ordinal);
        }
    }

    private static ClientResultException ClientFailure(HttpStatusCode status) =>
        new("provider rejected the request", Response((int)status));

    private static PipelineResponse Response(int status) => new StubResponse(status);

    /// <summary>The smallest response a <see cref="ClientResultException"/> will accept.</summary>
    private sealed class StubResponse(int status) : PipelineResponse
    {
        public override int Status { get; } = status;
        public override string ReasonPhrase => string.Empty;
        public override Stream? ContentStream { get => null; set { } }
        public override BinaryData Content { get; } = BinaryData.FromString(string.Empty);
        protected override PipelineResponseHeaders HeadersCore { get; } = new StubHeaders();

        public override BinaryData BufferContent(CancellationToken cancellationToken = default) => Content;

        public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Content);

        public override void Dispose()
        {
        }
    }

    private sealed class StubHeaders : PipelineResponseHeaders
    {
        public override IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        public override bool TryGetValue(string name, out string? value)
        {
            value = null;
            return false;
        }

        public override bool TryGetValues(string name, out IEnumerable<string>? values)
        {
            values = null;
            return false;
        }
    }
}
