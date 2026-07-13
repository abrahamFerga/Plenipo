using System.Net.Http.Headers;
using System.Net.Http.Json;
using Plenipo.Application.Commerce;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Commerce;

/// <summary>
/// Dispatches the deploy-customer GitHub Actions workflow via the workflow-dispatch REST API —
/// the least-new-moving-parts path to per-customer Azure environments (the workflow reuses the
/// repo's existing Terraform with a per-customer state key). Configured via
/// <c>Commerce:Dedicated:{Owner,Repo,Workflow,Token}</c>; the token is a SECRET (a fine-grained
/// PAT or GitHub App token with actions:write) — user-secrets/Key Vault, never appsettings.
/// </summary>
public sealed class GitHubDedicatedEnvironmentProvisioner(
    IHttpClientFactory httpClients,
    IOptions<CommerceOptions> options,
    ILogger<GitHubDedicatedEnvironmentProvisioner> logger) : IDedicatedEnvironmentProvisioner
{
    public bool IsConfigured =>
        options.Value.Dedicated is { Owner.Length: > 0, Repo.Length: > 0, Token.Length: > 0 };

    public async Task DispatchAsync(DedicatedEnvironmentRequest request, CancellationToken cancellationToken = default)
    {
        var dedicated = options.Value.Dedicated
            ?? throw new InvalidOperationException("Commerce:Dedicated is not configured.");

        var client = httpClients.CreateClient(nameof(GitHubDedicatedEnvironmentProvisioner));
        client.BaseAddress ??= new Uri("https://api.github.com/");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("plenipo-billing-worker");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", dedicated.Token);

        // POST /repos/{owner}/{repo}/actions/workflows/{file}/dispatches → 204 (no run id; the
        // run is correlated by its run-name, which embeds the customer slug).
        using var response = await client.PostAsJsonAsync(
            $"repos/{dedicated.Owner}/{dedicated.Repo}/actions/workflows/{dedicated.Workflow}/dispatches",
            new
            {
                @ref = dedicated.Ref,
                inputs = new
                {
                    customer = request.Customer,
                    action = request.Action,
                    region = request.Region ?? "westeurope",
                    size = request.Size ?? "small",
                },
            },
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"deploy-customer dispatch failed: {(int)response.StatusCode} {body}");
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Dispatched deploy-customer {Action} for {Customer} ({Region}/{Size}).",
                request.Action, request.Customer, request.Region ?? "westeurope", request.Size ?? "small");
        }
    }
}
