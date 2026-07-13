using System.Runtime.CompilerServices;
using Plenipo.Application.Agents;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Plenipo.AspNetCore.Realtime;

/// <summary>
/// Real-time chat surface. The client invokes <see cref="Stream"/> and receives a streamed sequence of
/// <see cref="AgentStreamEvent"/> (tokens, tool-invocation notices, completion). All authorization and
/// per-tool auditing happen inside the agent runner.
/// </summary>
[Authorize]
public sealed class AgentHub(IAuthorizedAgentRunner runner) : Hub
{
    public IAsyncEnumerable<AgentStreamEvent> Stream(AgentRunRequest request, CancellationToken cancellationToken) =>
        RunAsync(request, cancellationToken);

    private async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var evt in runner.RunAsync(request, cancellationToken))
        {
            yield return evt;
        }
    }
}
