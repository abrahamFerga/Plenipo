using System.Collections.Concurrent;
using Plenipo.Application.Auditing;
using Plenipo.Application.Channels;
using Plenipo.Core.Platform;
using Plenipo.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace Plenipo.Sample.Host.IntegrationTests;

/// <summary>
/// Boots the real sample API (Finance + Nutrition + Legal) over a throwaway Postgres container via
/// <see cref="WebApplicationFactory{TEntryPoint}"/>, so endpoint behaviour — auth, RBAC gating, the
/// approval gate, modules — is exercised through the full pipeline. The platform and audit contexts
/// share one container database (distinct schemas), and the chat assistant uses the Mock provider.
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = default!;

    public WebApplicationFactory<Program> Factory { get; private set; } = default!;

    public const string WhatsAppVerifyToken = "it-verify-token";
    public const string WhatsAppAppSecret = "it-whatsapp-app-secret";

    /// <summary>The same app with the WhatsApp channel enabled and the Cloud API sender replaced by a
    /// capturing fake — the channel is E2E-testable with no Meta account, credentials, or network.</summary>
    public WebApplicationFactory<Program> WhatsAppFactory { get; private set; } = default!;

    /// <summary>Outbound WhatsApp messages captured from <see cref="WhatsAppFactory"/>.</summary>
    public CapturingWhatsAppSender WhatsAppOutbox { get; } = new();

    /// <summary>Canned media served to <see cref="WhatsAppFactory"/> in place of the Meta media API.</summary>
    public FakeWhatsAppMediaClient WhatsAppMedia { get; } = new();

    /// <summary>The same app with the MCP pipeline on (a configured server activates it) and the tool
    /// PROVIDER replaced by a deterministic stub — the whole RBAC/audit/approval path around MCP tools
    /// is E2E-testable with no external server, npx, or network.</summary>
    public WebApplicationFactory<Program> McpFactory { get; private set; } = default!;

    public async Task InitializeAsync()
    {
        // Skip the resource-reaper sidecar, which can be flaky on Docker Desktop.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");

        _postgres = new PostgreSqlBuilder()
            .WithImage("pgvector/pgvector:pg16") // the RAG migration needs the vector extension
            .WithDatabase("plenipo_platform")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
        await _postgres.StartAsync();

        // Point the host at the container. Environment variables override appsettings.Development.json
        // (which targets the local docker-compose Postgres) — the same mechanism Aspire uses.
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-platform", _postgres.GetConnectionString());
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-audit", _postgres.GetConnectionString());

        Factory = new PlenipoAppFactory();

        WhatsAppFactory = Factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Channels:WhatsApp:Enabled", "true");
            builder.UseSetting("Channels:WhatsApp:VerifyToken", WhatsAppVerifyToken);
            builder.UseSetting("Channels:WhatsApp:AppSecret", WhatsAppAppSecret);
            builder.UseSetting("Channels:WhatsApp:AccessToken", "it-access-token");
            builder.UseSetting("Channels:WhatsApp:PhoneNumberId", "10000000001");
            builder.UseSetting("Channels:WhatsApp:ModuleId", "finance");
            builder.UseSetting("Channels:WhatsApp:TenantSlug", "dev");
            builder.UseSetting("Channels:WhatsApp:AllowUnknownSenders", "true");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IWhatsAppSender>(WhatsAppOutbox);
                services.AddSingleton<IWhatsAppMediaClient>(WhatsAppMedia);
            });
        });

        McpFactory = Factory.WithWebHostBuilder(builder =>
        {
            // The configured server only switches the MCP pipeline ON (its unreachable command also
            // exercises the "unavailable server degrades gracefully" path); the tools the agent
            // sees come from the stub provider below.
            builder.UseSetting("Mcp:Servers:0:Name", "fake");
            builder.UseSetting("Mcp:Servers:0:Transport", "Stdio");
            builder.UseSetting("Mcp:Servers:0:Command", "plenipo-it-mcp-server-that-does-not-exist");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<Plenipo.Application.Mcp.IMcpToolProvider>(new FakeMcpToolProvider());
            });
        });

        // First client build starts the host, which runs migrations + seeding.
        using var warmup = Factory.CreateClient();
        using var alive = await warmup.GetAsync("/alive");
        alive.EnsureSuccessStatusCode();
    }

    /// <summary>An HTTP client authenticated (via dev-auth headers) as the given role in the dev tenant.</summary>
    public HttpClient ClientFor(string role)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"it-{role}");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", "dev");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    /// <summary>An HTTP client authenticated as the given role in an arbitrary tenant — for multi-tenant
    /// isolation tests. The tenant must already exist (see <see cref="EnsureTenantAsync"/>).</summary>
    public HttpClient ClientForTenant(string role, string tenant)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-Subject", $"it-{role}-{tenant}");
        client.DefaultRequestHeaders.Add("X-Dev-Tenant", tenant);
        client.DefaultRequestHeaders.Add("X-Dev-Roles", role);
        return client;
    }

    /// <summary>Ensures a tenant with the given slug exists (only "dev" is seeded by default), so cross-tenant
    /// isolation can be exercised. Tenants are not tenant-owned, so no query filter applies.</summary>
    public async Task EnsureTenantAsync(string slug)
    {
        using var scope = Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (!await platform.Tenants.AnyAsync(t => t.Slug == slug))
        {
            platform.Tenants.Add(new Tenant { Name = $"{slug} tenant", Slug = slug });
            await platform.SaveChangesAsync();
        }
    }

    /// <summary>Loads a conversation with its messages straight from the database, bypassing tenant filters —
    /// for tests that assert on persisted conversation state (the message history).</summary>
    public async Task<Conversation> GetConversationAsync(Guid id)
    {
        using var scope = Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        return await platform.Conversations
            .IgnoreQueryFilters()
            .Include(c => c.Messages)
            .FirstAsync(c => c.Id == id);
    }

    /// <summary>Reads auth-audit events of a given type for a tenant straight from the audit database — for
    /// tests that assert security-relevant actions were recorded in the append-only trail.</summary>
    public async Task<IReadOnlyList<AuthAuditEntry>> AuthEventsForTenantAsync(string slug, AuthAuditEventType type)
    {
        using var scope = Factory.Services.CreateScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenant = await platform.Tenants.FirstAsync(t => t.Slug == slug);

        var audit = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        return await audit.AuthEvents
            .Where(e => e.TenantId == tenant.Id && e.EventType == type)
            .ToListAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-platform", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__plenipo-audit", null);

        if (McpFactory is not null)
        {
            await McpFactory.DisposeAsync();
        }

        if (WhatsAppFactory is not null)
        {
            await WhatsAppFactory.DisposeAsync();
        }

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private sealed class PlenipoAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Connectors:OperatorEnabled:local-folder", "true");
            builder.UseSetting("Connectors:LocalFolder:AllowedRoots:0", Path.GetTempPath());
            builder.UseSetting("Security:OutboundUrls:AllowHttp", "true");
            builder.UseSetting("Security:OutboundUrls:AllowPrivateNetworks", "true");

            // Route the plenipo-peer connector's HttpClient back into this TestServer, so the
            // peer-connector tests exercise a real system-to-system AG-UI conversation (the host
            // asking "itself" as if it were a remote deployment) without opening a network port.
            builder.ConfigureTestServices(services =>
            {
                services.AddHttpClient(Plenipo.Connectors.Peer.PlenipoPeerConnector.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());

                // Keyless stand-ins for the delegated-OAuth machinery: the IdP token exchange and
                // Microsoft Graph. The platform-side flow (state protection, token storage,
                // refresh, revoke-on-disable) stays fully real.
                services.AddSingleton<Plenipo.Application.Connectors.IOAuthTokenClient>(new FakeOAuthTokenClient());
                services.AddSingleton<Plenipo.Connectors.MsGraph.IGraphApiClient>(new FakeGraphApiClient());
                services.AddSingleton<Plenipo.Connectors.GoogleDrive.IDriveApiClient>(new FakeDriveApiClient());
            });
        }
    }
}

/// <summary>A fake Google Drive with one canned document; responds only to a fake token.</summary>
public sealed class FakeDriveApiClient : Plenipo.Connectors.GoogleDrive.IDriveApiClient
{
    public Task<IReadOnlyList<Plenipo.Connectors.GoogleDrive.DriveFile>> ListFilesAsync(
        string accessToken, string? nameContains, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Plenipo.Connectors.GoogleDrive.DriveFile>>(
            accessToken.StartsWith("fake-access-", StringComparison.Ordinal)
                ? [new("gdoc-1", "retainer-agreement.txt", 84, false)]
                : []);

    public Task<Plenipo.Connectors.GoogleDrive.DriveFileContent?> DownloadAsync(
        string accessToken, string fileId, CancellationToken cancellationToken = default) =>
        Task.FromResult(accessToken.StartsWith("fake-access-", StringComparison.Ordinal) && fileId == "gdoc-1"
            ? new Plenipo.Connectors.GoogleDrive.DriveFileContent(
                new MemoryStream("Retainer agreement between the firm and the client."u8.ToArray()), "text/plain")
            : null);
}

/// <summary>A fake IdP token endpoint: any code exchanges into a deterministic token.</summary>
public sealed class FakeOAuthTokenClient : Plenipo.Application.Connectors.IOAuthTokenClient
{
    public Task<Plenipo.Application.Connectors.OAuthTokens> ExchangeCodeAsync(
        Plenipo.Application.Connectors.OAuthClientConfig config, string code, string redirectUri, string codeVerifier,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new Plenipo.Application.Connectors.OAuthTokens(
            $"fake-access-{code}", "fake-refresh", DateTimeOffset.UtcNow.AddHours(1)));

    public Task<Plenipo.Application.Connectors.OAuthTokens?> RefreshAsync(
        Plenipo.Application.Connectors.OAuthClientConfig config, string refreshToken,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Plenipo.Application.Connectors.OAuthTokens?>(new(
            "fake-access-refreshed", refreshToken, DateTimeOffset.UtcNow.AddHours(1)));
}

/// <summary>A fake Graph drive with two canned documents; downloads only with a fake token.</summary>
public sealed class FakeGraphApiClient : Plenipo.Connectors.MsGraph.IGraphApiClient
{
    public Task<IReadOnlyList<Plenipo.Connectors.MsGraph.GraphDriveItem>> ListDriveItemsAsync(
        string accessToken, string? folderPath, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Plenipo.Connectors.MsGraph.GraphDriveItem>>(
            accessToken.StartsWith("fake-access-", StringComparison.Ordinal)
                ?
                [
                    new("item-1", "engagement-letter.txt", 64, false),
                    new("item-2", "fee-schedule.txt", 48, false),
                ]
                : []);

    public Task<Plenipo.Connectors.MsGraph.GraphFileContent?> DownloadAsync(
        string accessToken, string itemId, CancellationToken cancellationToken = default) =>
        Task.FromResult(accessToken.StartsWith("fake-access-", StringComparison.Ordinal) && itemId == "item-1"
            ? new Plenipo.Connectors.MsGraph.GraphFileContent(
                new MemoryStream("Engagement letter for the Contoso matter."u8.ToArray()), "text/plain")
            : null);
}

/// <summary>Two deterministic "MCP" tools: a read-only echo and a side-effecting alert (approval-gated).</summary>
public sealed class FakeMcpToolProvider : Plenipo.Application.Mcp.IMcpToolProvider
{
    private readonly IReadOnlyList<Plenipo.Application.Mcp.McpServerTool> _tools =
    [
        new("fake",
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                (string message) => $"MCP echo: {message}", name: "fake_echo_message"),
            RequiresApproval: false),
        new("fake",
            Microsoft.Extensions.AI.AIFunctionFactory.Create(
                (string message) => $"alert sent: {message}", name: "fake_send_alert"),
            RequiresApproval: true),
    ];

    public IReadOnlyList<Plenipo.Application.Mcp.McpServerTool> GetTools() => _tools;
}

[CollectionDefinition("api")]
public sealed class ApiCollection : ICollectionFixture<IntegrationFixture>;

/// <summary>An <see cref="IWhatsAppSender"/> that records what would have been sent to Meta.</summary>
public sealed class CapturingWhatsAppSender : IWhatsAppSender
{
    public ConcurrentQueue<(string To, string Text)> Sent { get; } = new();

    public Task SendTextAsync(string to, string text, CancellationToken cancellationToken = default)
    {
        Sent.Enqueue((to, text));
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="IWhatsAppMediaClient"/> serving canned bytes keyed by media id.</summary>
public sealed class FakeWhatsAppMediaClient : IWhatsAppMediaClient
{
    public ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> Media { get; } = new();

    public Task<WhatsAppMedia?> DownloadAsync(string mediaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Media.TryGetValue(mediaId, out var m)
            ? new WhatsAppMedia(new MemoryStream(m.Bytes), m.ContentType)
            : null);
}
