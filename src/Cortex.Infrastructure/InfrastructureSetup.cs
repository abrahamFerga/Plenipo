using Cortex.Application.Agents;
using Cortex.Application.Ai;
using Cortex.Application.Approvals;
using Cortex.Application.Auditing;
using Cortex.Application.Authorization;
using Cortex.Application.Connectors;
using Cortex.Application.Conversations;
using Cortex.Application.Files;
using Cortex.Application.Jobs;
using Cortex.Application.Modules;
using Cortex.Application.Notifications;
using Cortex.Application.Rag;
using Cortex.Application.Secrets;
using Cortex.Application.Skills;
using Cortex.Application.Usage;
using Cortex.Core.Identity;
using Cortex.Core.Multitenancy;
using Cortex.Infrastructure.Agents;
using Cortex.Infrastructure.Ai;
using Cortex.Infrastructure.Approvals;
using Cortex.Infrastructure.Auditing;
using Cortex.Infrastructure.Authorization;
using Cortex.Infrastructure.Connectors;
using Cortex.Infrastructure.Context;
using Cortex.Infrastructure.Conversations;
using Cortex.Infrastructure.Documents;
using Cortex.Infrastructure.Files;
using Cortex.Infrastructure.Jobs;
using Cortex.Infrastructure.Modules;
using Cortex.Infrastructure.Notifications;
using Cortex.Infrastructure.Persistence;
using Cortex.Infrastructure.Persistence.Interceptors;
using Cortex.Infrastructure.Rag;
using Cortex.Infrastructure.Secrets;
using Cortex.Infrastructure.Skills;
using Cortex.Infrastructure.Usage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Cortex.Infrastructure;

/// <summary>Wires the platform's infrastructure: persistence, multi-tenancy, RBAC, auditing, and the agent stack.</summary>
public static class InfrastructureSetup
{
    public static IHostApplicationBuilder AddCortexInfrastructure(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;

        AddRequestContext(services);
        AddPersistence(builder);
        AddSecretVault(builder);
        AddSkills(builder);
        AddMcp(builder);
        AddAuthorization(builder);
        AddAuditing(services);
        AddAgentStack(builder);
        AddFilesAndDocuments(builder);
        AddRag(builder);
        AddConnectors(services);

        return builder;
    }

    private static void AddConnectors(IServiceCollection services)
    {
        // Registered unconditionally: with no IConnector registrations the catalog is empty and the
        // tool feed contributes nothing. Enablement is per tenant, default-OFF, via the admin API.
        services.AddSingleton<IConnectorCatalog, ConnectorCatalog>();
        services.AddScoped<ITenantConnectorStore, TenantConnectorStore>();
        services.AddScoped<IConnectorToolCatalog, ConnectorToolCatalog>();
        services.AddScoped<ConnectorSettingsService>();
        services.AddScoped<Cortex.Connectors.Sdk.IConnectorSettings>(sp => sp.GetRequiredService<ConnectorSettingsService>());

        // Sync lane: resource-scoped bindings walked by a background job (incremental via
        // per-item stamps); the owning module attaches/indexes what gets imported.
        services.AddScoped<IConnectorBindingService, ConnectorBindingService>();
        services.AddSingleton<IJobHandler, ConnectorSyncJobHandler>();

        // Delegated (per-user OAuth) connectors: protected token sessions + the code exchange.
        services.AddHttpClient(OAuthTokenClient.HttpClientName);
        services.AddSingleton<IOAuthTokenClient, OAuthTokenClient>();
        services.AddScoped<ConnectorUserLoginService>();
        services.AddScoped<IConnectorUserLogins>(sp => sp.GetRequiredService<ConnectorUserLoginService>());
    }

    private static void AddRag(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.SectionName));
        var ragOptions = builder.Configuration.GetSection(RagOptions.SectionName).Get<RagOptions>() ?? new RagOptions();
        if (!ragOptions.Enabled)
        {
            return; // opt-in: a deployment without RAG registers nothing and offers no tool
        }

        var aiOptions = builder.Configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        services.AddSingleton<IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>(
            _ => EmbeddingGeneratorFactory.Create(ragOptions, aiOptions));

        services.AddScoped<IRagService, RagService>();
        services.AddScoped<RagTools>();
        services.AddSingleton<IPlatformToolSource, RagToolSource>();
        services.AddSingleton<IJobHandler, RagIngestJobHandler>();
    }

    private static void AddFilesAndDocuments(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<FileStorageOptions>(builder.Configuration.GetSection(FileStorageOptions.SectionName));
        var fileOptions = builder.Configuration.GetSection(FileStorageOptions.SectionName).Get<FileStorageOptions>()
            ?? new FileStorageOptions();
        fileOptions.ThrowIfInvalid(); // fail fast on AzureBlob without a connection string

        if (string.Equals(fileOptions.Provider, "AzureBlob", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IFileBlobStorage, AzureBlobFileStorage>();
        }
        else
        {
            services.AddSingleton<IFileBlobStorage, LocalFileBlobStorage>();
        }

        services.AddScoped<IFileStore, FileStore>();

        // Platform document tools, appended to every module's agent (each permission-gated). The
        // ocr_document tool appears only when the host registers an IOcrEngine implementation, and
        // the whole pack can be switched off per deployment (Documents:Enabled=false) — per-tenant
        // and per-user gating stays with the RBAC layer (tools.documents.* in the role editor).
        services.Configure<Cortex.Application.Documents.DocumentOptions>(
            builder.Configuration.GetSection(Cortex.Application.Documents.DocumentOptions.SectionName));
        var documentOptions = builder.Configuration
            .GetSection(Cortex.Application.Documents.DocumentOptions.SectionName)
            .Get<Cortex.Application.Documents.DocumentOptions>() ?? new Cortex.Application.Documents.DocumentOptions();
        if (documentOptions.Enabled)
        {
            services.AddScoped<DocumentTools>();
            services.AddSingleton<IPlatformToolSource, DocumentToolSource>();
        }

        // The same extraction/rendering for MODULE CODE (job handlers, reports) — Application-level
        // seams so modules never reference Infrastructure directly.
        services.AddScoped<Cortex.Application.Documents.IDocumentReader, DocumentReader>();
        services.AddSingleton<Cortex.Application.Documents.IPdfRenderer, PdfRenderer>();

        // Background jobs: modules enqueue long-running work (bulk review, batch imports); the
        // processor executes handlers with the enqueuer's identity restored.
        services.AddScoped<IJobQueue, DbJobQueue>();
        services.AddHostedService<JobProcessor>();
    }

    private static void AddRequestContext(IServiceCollection services)
    {
        // One scoped object backs both the current-user and tenant abstractions.
        services.AddScoped<RequestContext>();
        services.AddScoped<ICurrentUser>(sp => sp.GetRequiredService<RequestContext>());
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<RequestContext>());
    }

    private static void AddPersistence(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddScoped<AuditInterceptor>();

        // Platform DB: registered explicitly so the scoped audit interceptor can be injected, then
        // enriched with Aspire health checks + telemetry.
        services.AddDbContext<PlatformDbContext>((sp, options) =>
        {
            options.UseNpgsql(builder.Configuration.GetConnectionString(PlatformDbContext.ConnectionName));
            options.AddInterceptors(sp.GetRequiredService<AuditInterceptor>());
        });
        builder.EnrichNpgsqlDbContext<PlatformDbContext>();

        // Audit DB: append-only, no interceptor — wired through the Aspire integration directly.
        builder.AddNpgsqlDbContext<AuditDbContext>(AuditDbContext.ConnectionName);

        // Durable audit outbox: a minimal, interceptor-free context over the platform DB. PlatformDbContext's
        // migration creates the audit_outbox table; this context only reads/writes it.
        services.AddDbContext<OutboxDbContext>(options =>
            options.UseNpgsql(builder.Configuration.GetConnectionString(OutboxDbContext.ConnectionName)));
    }

    private static void AddAuthorization(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // Database (default) merges internal RBAC with token claims; Token delegates authorization
        // entirely to the external IdP. Fail fast on a typo rather than silently defaulting.
        services.Configure<AuthorizationSourceOptions>(
            builder.Configuration.GetSection(AuthorizationSourceOptions.SectionName));
        (builder.Configuration.GetSection(AuthorizationSourceOptions.SectionName).Get<AuthorizationSourceOptions>()
            ?? new AuthorizationSourceOptions()).ThrowIfInvalid();

        services.AddScoped<IPermissionResolver, PermissionResolver>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
    }

    private static void AddAuditing(IServiceCollection services)
    {
        services.AddScoped<IAuditLog, AuditLog>();
        // Flushes any audit records the durable outbox captured during an audit-DB outage.
        services.AddHostedService<AuditOutboxProcessor>();
    }

    private static void AddSecretVault(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<SecretsOptions>(builder.Configuration.GetSection(SecretsOptions.SectionName));
        services.AddSingleton<DataProtectionSecretVault>();

        // Backend selection is configuration, not code: Key Vault stores values externally and the
        // DB keeps kv: pointers; the default keeps DataProtection ciphertext inline. References are
        // prefix-tagged, so a deployment can switch providers without migrating existing secrets.
        var provider = builder.Configuration[$"{SecretsOptions.SectionName}:Provider"];
        if (string.Equals(provider, SecretsOptions.AzureKeyVaultProvider, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISecretVault, KeyVaultSecretVault>();
        }
        else
        {
            services.AddSingleton<ISecretVault>(sp => sp.GetRequiredService<DataProtectionSecretVault>());
        }
    }

    private static void AddSkills(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<SkillsOptions>(builder.Configuration.GetSection(SkillsOptions.SectionName));
        services.Configure<Cortex.Application.Commerce.CommerceOptions>(
            builder.Configuration.GetSection(Cortex.Application.Commerce.CommerceOptions.SectionName));
        services.AddSingleton<ISkillCatalog, FileSkillCatalog>();

        // The skill tools only exist as a tool source when skills are on — a deployment without
        // skills never shows the model load_skill/read_skill_resource/run_skill_script.
        if (builder.Configuration.GetSection(SkillsOptions.SectionName).Get<SkillsOptions>() is { Enabled: true })
        {
            services.AddScoped<SkillTools>();
            services.AddSingleton<IPlatformToolSource, SkillToolSource>();
        }
    }

    private static void AddMcp(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        // Deploy-time like skills: only a host with configured servers gets the MCP pipeline. The
        // manager connects in the background (never blocks startup); every discovered tool is
        // RBAC-gated (tools.mcp.*, granted to no role by default) and approval-gated per server.
        if (builder.Configuration.GetSection(Application.Mcp.McpOptions.SectionName).Get<Application.Mcp.McpOptions>() is not { Servers.Count: > 0 })
        {
            return;
        }

        services.Configure<Application.Mcp.McpOptions>(builder.Configuration.GetSection(Application.Mcp.McpOptions.SectionName));
        services.AddSingleton<Mcp.McpClientManager>();
        services.AddSingleton<Application.Mcp.IMcpToolProvider>(sp => sp.GetRequiredService<Mcp.McpClientManager>());
        services.AddHostedService(sp => sp.GetRequiredService<Mcp.McpClientManager>());
        services.AddSingleton<IPlatformToolSource, Mcp.McpToolSource>();
    }

    private static void AddAgentStack(IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.SectionName));
        var aiOptions = builder.Configuration.GetSection(AiOptions.SectionName).Get<AiOptions>() ?? new AiOptions();
        // Fail fast at startup on a misconfigured provider (e.g. OpenAI without a key) rather than on the
        // first chat, where the IChatClient is otherwise built lazily.
        AiOptionsValidator.ThrowIfInvalid(aiOptions);
        if (aiOptions.IsEnabled)
        {
            services.AddSingleton<IChatClient>(_ => ChatClientFactory.Create(aiOptions));
        }

        // The per-turn client: tenant provider connection (vaulted key) + per-agent model override,
        // cached per distinct connection. The singleton above remains the deployment default for
        // any host code that wants a bare client.
        services.AddSingleton<ITenantChatClientResolver, TenantChatClientResolver>();

        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddScoped<ITenantModuleStore, TenantModuleStore>();
        services.AddScoped<ITenantAiSettings, TenantAiSettingsResolver>();
        services.AddScoped<IAgentProfileResolver, AgentProfileResolver>();
        services.AddScoped<IInstructionSnapshotStore, InstructionSnapshotStore>();
        services.AddScoped<INotifier, Notifier>();
        services.AddScoped<INotificationWebhookConfigReader, NotificationWebhookConfigReader>();
        services.AddScoped<INotificationChannel, WebhookNotificationChannel>();
        services.AddHttpClient(WebhookNotificationChannel.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<ITokenUsageReader, TokenUsageReader>();
        services.AddScoped<Usage.BudgetAlerts>();

        // Cross-module handoff: ask_{module} tools (read-only nested turns; permission-gated).
        services.AddScoped<Handoff.HandoffTools>();
        services.AddSingleton<IPlatformToolSource, Handoff.HandoffToolSource>();
        services.AddScoped<IApprovalStore, ApprovalStore>();
        services.AddScoped<ApprovalExecutor>();
        services.AddScoped<IAuthorizedAgentRunner, AuthorizedAgentRunner>();
    }
}
