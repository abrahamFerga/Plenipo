namespace Plenipo.Application.Auditing;

/// <summary>
/// How an agent turn ended. Every turn records exactly one of these — a turn that fails before the
/// model is ever called is as auditable as one that completes, which is the point: "the assistant
/// didn't answer" is the question operators most often need to answer, and it is precisely the case
/// that leaves no token usage behind.
/// </summary>
public enum AgentRunOutcome
{
    /// <summary>The turn ran to completion and the assistant's reply was persisted.</summary>
    Completed = 0,

    /// <summary>The model call (or the surrounding turn) threw. See <c>ErrorKind</c> for the exception type.</summary>
    Error = 1,

    /// <summary>The agent security policy blocked the input or the model's output.</summary>
    BlockedBySecurity = 2,

    /// <summary>A conversation or monthly token budget was already exhausted, so the turn was refused.</summary>
    BudgetExceeded = 3,

    /// <summary>No usable chat client: the provider is unconfigured for the deployment or misconfigured for the tenant.</summary>
    ProviderUnavailable = 4,

    /// <summary>The module is unknown to the catalog or is not enabled for this tenant.</summary>
    ModuleUnavailable = 5,

    /// <summary>
    /// The caller stopped reading the stream (client disconnect) or the turn was cancelled. Recorded
    /// because the enumerator's disposal still runs the flush — an abandoned turn is not a lost one.
    /// </summary>
    Cancelled = 6,

    /// <summary>
    /// The request could not be served as asked: an unknown agent name, a model outside the advertised
    /// list, a misdeclared workflow, or a missing conversation. A caller error, not a platform fault.
    /// </summary>
    Rejected = 7,
}
