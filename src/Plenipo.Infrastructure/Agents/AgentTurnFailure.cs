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

    // A 404 says "not here", not "no such model": a wrong base URL or Azure deployment path produces
    // one just as a retired model name does, and the two are indistinguishable from the response. Both
    // are fixed on the same screen, so the message names both rather than asserting the narrower one.
    internal const string ModelOrEndpointUnknown =
        "The AI provider does not recognise the configured model or endpoint. An administrator can check both under AI settings.";

    internal const string ProviderFailing =
        "The AI provider is currently failing. This is not a configuration problem — try again shortly.";

    internal const string Unreachable =
        "The AI provider could not be reached. An administrator can check the endpoint under AI settings.";

    internal const string TimedOut =
        "The AI provider did not respond in time. Try again shortly.";

    /// <summary>How far down an exception chain to look before giving up; guards a cyclic chain.</summary>
    private const int MaxDepth = 16;

    /// <summary>Maps <paramref name="exception"/> to user-facing text; never includes provider detail.</summary>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return Classify(exception, depth: 0) ?? Generic;
    }

    /// <summary>
    /// The message <paramref name="exception"/> warrants, or null when it carries no evidence and the
    /// search should continue into its inner causes.
    /// </summary>
    private static string? Classify(Exception exception, int depth)
    {
        if (depth > MaxDepth)
        {
            return null;
        }

        // A tool — or a connector it called — is not the AI provider, and its HTTP status says nothing
        // about one. A connector 401 read as a provider 401 would name the wrong screen with the same
        // confidence as a correct answer, which is worse than the generic string this class replaced.
        // Everything below this marker belongs to the tool, so the search stops rather than descending.
        if (exception is ToolInvocationFailedException)
        {
            return Generic;
        }

        if (StatusOf(exception) is { } status)
        {
            return FromStatus(status);
        }

        // No status: the request never got far enough to receive one (DNS, TLS, refused
        // connection, or the outbound-URL policy rejecting the endpoint).
        if (exception is HttpRequestException)
        {
            return Unreachable;
        }

        // A read that ran out of time: HttpClient reports its own timeout as a cancellation
        // carrying a TimeoutException, and requiring that TimeoutException is what separates a
        // timeout from a cancellation nobody attributed. The catch this serves spans the whole
        // run — middleware, module tools, connector fetches — so a bare cancellation is NOT
        // evidence the provider was slow, and reporting it as one would repeat the very
        // misattribution this class exists to remove, one level in. It stays Generic.
        if (exception is TimeoutException)
        {
            return TimedOut;
        }

        // Retries exhausted, or a parallel read, arrive as an AggregateException whose cause may sit in
        // any branch — following InnerException alone reads only the first and loses the rest.
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
            {
                if (Classify(inner, depth + 1) is { } fromBranch)
                {
                    return fromBranch;
                }
            }

            return null;
        }

        return exception.InnerException is { } single ? Classify(single, depth + 1) : null;
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
        (int)HttpStatusCode.NotFound => ModelOrEndpointUnknown,
        // 402 is how several providers report an exhausted prepaid balance.
        (int)HttpStatusCode.PaymentRequired => QuotaExhausted,
        >= 500 => ProviderFailing,
        // A 4xx we cannot attribute is usually the platform's own request being wrong, which is not
        // something the tenant can act on — say nothing rather than send them to the wrong screen.
        _ => Generic,
    };
}
