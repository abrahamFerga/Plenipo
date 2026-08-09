namespace Plenipo.Infrastructure.Agents;

/// <summary>
/// Marks a failure that came out of a module tool — or a connector the tool called — rather than out of
/// the AI provider.
/// <para>
/// The runner's failure classifier reads an exception's HTTP status to decide what the tenant should be
/// told. That inference is only sound for exceptions the <em>provider</em> raised: a module tool calling
/// a third-party API can raise a byte-identical 401, and reporting it as "the AI provider rejected the
/// configured API key" would send the administrator to the AI settings screen to re-enter a key that was
/// never the problem. A confident wrong answer is worse than the generic one it replaced, which is the
/// whole reason <see cref="AgentTurnFailure"/> exists — so the boundary that knows where a failure came
/// from records it here, and the classifier stops at this marker.
/// </para>
/// <para>
/// Cancellation is deliberately NOT wrapped: the runner distinguishes a caller abort from a provider
/// failure by catching <see cref="OperationCanceledException"/>, and hiding one inside this type would
/// turn a user closing the tab into a reported error.
/// </para>
/// </summary>
public sealed class ToolInvocationFailedException : Exception
{
    public ToolInvocationFailedException(string toolName, Exception innerException)
        : base($"The module tool '{toolName}' failed.", innerException) => ToolName = toolName;

    /// <summary>The tool whose invocation raised <see cref="Exception.InnerException"/>.</summary>
    public string ToolName { get; }
}
