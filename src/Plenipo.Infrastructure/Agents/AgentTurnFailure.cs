using System.ClientModel;
using System.Net;
using Azure;

namespace Plenipo.Infrastructure.Agents;

/// <summary>
/// Turns the exception a provider threw mid-turn into a message the tenant can act on.
/// <para>
/// A turn fails for one of a few reasons that look identical from the chat window but need entirely
/// different responses: an API key the provider rejected (the administrator fixes it), an exhausted
/// quota (wait, or change plan), a model the connection does not carry (change the model), a provider
/// outage (nothing to fix), or an unreachable endpoint. Reporting all of them as one string leaves the
/// only person who could fix the problem with nothing to go on — and on a hosted deployment the log
/// line carrying the real reason is not theirs to read.
/// </para>
/// <para>
/// The provider's own message is deliberately NEVER surfaced: it can carry request-payload fragments,
/// organisation identifiers, and endpoint URLs. Classification picks one of the fixed strings below;
/// the exception itself goes to the log and nowhere else. Anything unrecognised keeps
/// <see cref="Generic"/>, so this narrows the opaque case rather than trading one guess for another.
/// </para>
/// </summary>
public static class AgentTurnFailure
{
    /// <summary>The fallback when the failure cannot be attributed to a known cause.</summary>
    public const string Generic = "The assistant could not complete the request.";

    internal const string KeyRejected =
        "The AI provider rejected the configured API key. An administrator can update it under AI settings.";

    internal const string QuotaExhausted =
        "The AI provider is rate-limiting this tenant, or its quota is exhausted. Try again shortly, or check the plan with the provider.";

    internal const string ModelUnknown =
        "The AI provider does not recognise the configured model. An administrator can change it under AI settings.";

    internal const string ProviderFailing =
        "The AI provider is currently failing. This is not a configuration problem — try again shortly.";

    internal const string Unreachable =
        "The AI provider could not be reached. An administrator can check the endpoint under AI settings.";

    internal const string TimedOut =
        "The AI provider did not respond in time. Try again shortly.";

    /// <summary>Maps <paramref name="exception"/> to user-facing text; never includes provider detail.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        // A timeout arrives as a cancellation the caller did not ask for; the runner only routes one
        // here once it has established its own token is not the source.
        if (exception is TimeoutException or OperationCanceledException)
        {
            return TimedOut;
        }

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (StatusOf(current) is { } status)
            {
                return FromStatus(status);
            }

            // No status: the request never got far enough to receive one (DNS, TLS, refused
            // connection, or the outbound-URL policy rejecting the endpoint).
            if (current is HttpRequestException)
            {
                return Unreachable;
            }

            if (current is TimeoutException)
            {
                return TimedOut;
            }
        }

        return Generic;
    }

    /// <summary>
    /// The HTTP status a provider exception carries, across the client stacks in use — or null when it
    /// carries none. A transport failure still arrives as a <see cref="ClientResultException"/> (the
    /// ClientModel pipeline wraps the socket error and reports <c>Status = 0</c>, under an
    /// <see cref="AggregateException"/> once retries are exhausted), so a non-positive status means "no
    /// response happened" and the walk has to continue to the inner cause rather than stop here.
    /// </summary>
    private static int? StatusOf(Exception exception) => exception switch
    {
        // OpenAI, Azure OpenAI, and Ollama all surface through System.ClientModel.
        ClientResultException { Status: > 0 } clientResult => clientResult.Status,
        // Azure SDK paths that predate the ClientModel pipeline (e.g. managed-identity failures).
        RequestFailedException { Status: > 0 } requestFailed => requestFailed.Status,
        HttpRequestException { StatusCode: { } code } => (int)code,
        _ => null,
    };

    private static string FromStatus(int status) => status switch
    {
        (int)HttpStatusCode.Unauthorized or (int)HttpStatusCode.Forbidden => KeyRejected,
        (int)HttpStatusCode.TooManyRequests => QuotaExhausted,
        (int)HttpStatusCode.NotFound => ModelUnknown,
        // 402 is how several providers report an exhausted prepaid balance.
        (int)HttpStatusCode.PaymentRequired => QuotaExhausted,
        >= 500 => ProviderFailing,
        // A 4xx we cannot attribute is usually the platform's own request being wrong, which is not
        // something the tenant can act on — say nothing rather than send them to the wrong screen.
        _ => Generic,
    };
}
