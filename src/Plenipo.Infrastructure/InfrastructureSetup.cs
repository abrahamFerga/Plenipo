using Plenipo.Application.Agents;
using Plenipo.Application.Ai;
using Plenipo.Application.Approvals;
using Plenipo.Application.Auditing;
using Plenipo.Application.Authorization;
using Plenipo.Application.Channels;
using Plenipo.Application.Connectors;
using Plenipo.Application.Conversations;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Application.Modules;
using Plenipo.Application.Notifications;
using Plenipo.Application.Rag;
using Plenipo.Application.Secrets;
using Plenipo.Application.Security;
using Plenipo.Application.Skills;
using Plenipo.Application.Usage;
using Plenipo.Core.Identity;
using Plenipo.Core.Multitenancy;
using Plenipo.Infrastructure.Agents;
using Plenipo.Infrastructure.Ai;
using Plenipo.Infrastructure.Approvals;
using Plenipo.Infrastructure.Auditing;
using Plenipo.Infrastructure.Authorization;
using Plenipo.Infrastructure.Connectors;
using Plenipo.Infrastructure.Context;
using Plenipo.Infrastructure.Conversations;
using Plenipo.Infrastructure.Documents;
using Plenipo.Infrastructure.Files;
using Plenipo.Infrastructure.Jobs;
using Plenipo.Infrastructure.Modules;
using Plenipo.Infrastructure.Notifications;
using Plenipo.Infrastructure.Persistence;
using Plenipo.Infrastructure.Persistence.Interceptors;
using Plenipo.Infrastructure.Rag;
using Plenipo.Infrastructure.Secrets;
using Plenipo.Infrastructure.Skills;
using Plenipo.Infrastructure.Usage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Plenipo.Infrastructure;

/// <summary>Wires the platform's infrastructure: persistence, multi-tenancy, RBAC, auditing, and the agent stack.</summary>
public static class InfrastructureSetup
{
    public static IHostApplicationBuilder AddPlenipoInfrastructure(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;

        AddRequestContext(services);
        services.Configure<OutboundUrlOptions>(configuration.GetSection(OutboundUrlOptions.SectionName));
        services.AddSingleton<OutboundUrlPolicy>();
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
        services.AddScoped<Plenipo.Connectors.Sdk.IConnectorSettings>(sp => sp.GetRequiredService<ConnectorSettingsService>());

        // Sync lane: resource-scoped bindings walked by a background job (incremental via
        // per-item stamps); the owning module attaches/indexes what gets imported.
        services.AddScoped<IConnectorBindingService, ConnectorBindingService>();
        services.AddSingleton<IJobHandler, ConnectorSyncJobHandler>();

        // Delegated (per-user OAuth) connectors: protected token sessions + the code exchange.
        services.AddHttpClient(OAuthTokenClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
                sp.GetRequiredService<OutboundUrlPolicy>().CreateHttpMessageHandler());
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
        services.Configure<Plenipo.Application.Documents.DocumentOptions>(
            builder.Configuration.GetSection(Plenipo.Application.Documents.DocumentOptions.SectionName));

        // Config-driven OCR: Ocr:Provider=AzureDocumentIntelligence (+ endpoint + key) registers
        // the engine; everything consuming the IOcrEngine seam — the ocr_document tool, document
        // reading, product statement extractors — lights up without further wiring.
        services.Configure<Plenipo.Application.Documents.OcrOptions>(
            builder.Configuration.GetSection(Plenipo.Application.Documents.OcrOptions.SectionName));
        var ocrOptions = builder.Configuration
            .GetSection(Plenipo.Application.Documents.OcrOptions.SectionName)
            .Get<Plenipo.Application.Documents.OcrOptions>() ?? new Plenipo.Application.Documents.OcrOptions();
        if (ocrOptions.IsAzureDocumentIntelligence)
        {
            services.AddSingleton<Plenipo.Application.Documents.IOcrEngine, AzureDocumentIntelligenceOcrEngine>();
        }

        var documentOptions = builder.Configuration
            .GetSection(Plenipo.Application.Documents.DocumentOptions.SectionName)
            .Get<Plenipo.Application.Documents.DocumentOptions>() ?? new Plenipo.Application.Documents.DocumentOptions();
        if (documentOptions.Enabled)
        {
            services.AddScoped<DocumentTools>();
            services.AddSingleton<IPlatformToolSource, DocumentToolSource>();
        }

        // The same extraction/rendering for MODULE CODE (job handlers, reports) — Application-level
        // seams so modules never reference Infrastructure directly.
        services.AddScoped<Plenipo.Application.Documents.IDocumentReader, DocumentReader>();
        services.AddSingleton<Plenipo.Application.Documents.IPdfRenderer, PdfRenderer>();

        // Background jobs: modules enqueue long-running work (bulk review, batch imports); the
        // processor executes handlers with the enqueuer's identity restored. The scheduler turns
        // manifest-declared recurring jobs into queued work (idles when no module declares any).
        services.AddScoped<IJobQueue, DbJobQueue>();
        services.AddHostedService<JobProcessor>();
        services.AddHostedService<RecurringJobScheduler>();
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
        services.Configure<Plenipo.Application.Commerce.CommerceOptions>(
            builder.Configuration.GetSection(Plenipo.Application.Commerce.CommerceOptions.SectionName));
        services.AddScoped<Plenipo.Application.Commerce.ITenantProvisioningService, Commerce.TenantProvisioningService>();
        services.AddSingleton<Plenipo.Application.Commerce.IProductOfferingCatalog, Plenipo.Application.Commerce.ProductOfferingCatalog>();
        services.AddHttpClient(nameof(Commerce.GitHubDedicatedEnvironmentProvisioner));
        services.AddHttpClient(nameof(Commerce.StripeBillingMeter));
        services.AddSingleton<Plenipo.Application.Commerce.IBillingMeter, Commerce.StripeBillingMeter>();
        services.AddHttpClient(nameof(Commerce.StripeCheckout));
        services.AddSingleton<Plenipo.Application.Commerce.IStripeCheckout, Commerce.StripeCheckout>();
        services.AddSingleton<Plenipo.Application.Commerce.IDedicatedEnvironmentProvisioner, Commerce.GitHubDedicatedEnvironmentProvisioner>();
        services.AddHostedService<Commerce.BillingEventProcessor>(); // no-ops unless commerce is enabled
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
        services.AddHttpClient(ProviderAiModelCatalog.HttpClientName, client =>
            client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(sp =>
                sp.GetRequiredService<OutboundUrlPolicy>().CreateHttpMessageHandler());
        services.AddSingleton<IAiModelCatalog, ProviderAiModelCatalog>();

        services.AddSingleton<IModuleCatalog, ModuleCatalog>();
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        services.AddScoped<ITenantModuleStore, TenantModuleStore>();
        services.AddScoped<ITenantAiSettings, TenantAiSettingsResolver>();
        services.AddScoped<IAgentProfileResolver, AgentProfileResolver>();
        services.AddScoped<IInstructionSnapshotStore, InstructionSnapshotStore>();
        services.AddScoped<INotifier, Notifier>();
        services.AddScoped<INotificationWebhookConfigReader, NotificationWebhookConfigReader>();
        services.AddScoped<INotificationChannel, WebhookNotificationChannel>();
        services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
        services.AddSingleton<ISmtpTransport, SmtpClientTransport>();
        services.AddScoped<INotificationChannel, EmailNotificationChannel>(); // no-op until Email: is configured
        services.AddHttpClient(WebhookNotificationChannel.HttpClientName,
            client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(sp =>
                sp.GetRequiredService<OutboundUrlPolicy>().CreateHttpMessageHandler());
        services.AddScoped<IConversationStore, ConversationStore>();
        services.AddScoped<ITokenUsageReader, TokenUsageReader>();
        services.AddScoped<Usage.BudgetAlerts>();

        // Cross-module handoff: ask_{module} tools (read-only nested turns; permission-gated).
        services.AddScoped<Handoff.HandoffTools>();
        services.AddSingleton<IPlatformToolSource, Handoff.HandoffToolSource>();
        services.AddScoped<IApprovalStore, ApprovalStore>();
        services.AddScoped<ApprovalNotifier>();
        services.AddScoped<ApprovalExecutor>();
        services.AddScoped<IAuthorizedAgentRunner, AuthorizedAgentRunner>();

        // The channel-agnostic core every inbound conversation channel runs through (WhatsApp today).
        services.AddScoped<IChannelTurnService, Channels.ChannelTurnService>();
    }
}
