using Plenipo.Application.Agents;
using Plenipo.Infrastructure.Documents;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Plenipo.Api.Tests;

/// <summary>
/// The deployment-level switch for the platform document tools: on by default (they're a base,
/// dependency-free capability), and Documents:Enabled=false removes the agent-facing tool source
/// entirely — the model can never see a tool the deployment turned off. Per-tenant/per-user gating
/// is the RBAC layer's job and is covered by the tool-permission tests.
/// </summary>
public sealed class DocumentToolConfigTests
{
    [Fact]
    public async Task Document_tools_are_registered_by_default()
    {
        using var factory = new PlenipoApiFactory();
        using var warmup = factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();

        Assert.Contains(
            factory.Services.GetServices<IPlatformToolSource>(),
            source => source is DocumentToolSource);
    }

    [Fact]
    public async Task Documents_disabled_removes_the_tool_source_from_the_deployment()
    {
        using var factory = new DocumentsDisabledFactory();
        using var warmup = factory.CreateClient();
        (await warmup.GetAsync("/alive")).EnsureSuccessStatusCode();

        Assert.DoesNotContain(
            factory.Services.GetServices<IPlatformToolSource>(),
            source => source is DocumentToolSource);
    }

    private sealed class DocumentsDisabledFactory : PlenipoApiFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // UseSetting flows into IConfiguration before AddPlenipoInfrastructure reads the section.
            builder.UseSetting("Documents:Enabled", "false");
            base.ConfigureWebHost(builder);
        }
    }
}
