using System.Text.Json.Serialization;
using Cortex.AspNetCore.Auth;
using Cortex.AspNetCore.Channels;
using Cortex.AspNetCore.Commerce;
using Cortex.AspNetCore.Endpoints;
using Cortex.AspNetCore.Identity;
using Cortex.AspNetCore.Middleware;
using Cortex.AspNetCore.Modules;
using Cortex.AspNetCore.RateLimiting;
using Cortex.AspNetCore.Realtime;
using Cortex.AspNetCore.Setup;
using Cortex.Infrastructure;
using Cortex.Infrastructure.Channels;
using Microsoft.AspNetCore.SignalR;

namespace Cortex.AspNetCore.Hosting;

/// <summary>
/// The one-call host setup for a Cortex platform host. A product's <c>Program.cs</c> becomes just:
/// <code>
/// var builder = WebApplication.CreateBuilder(args);
/// builder.AddCortexPlatform();
/// builder.AddCortexModule&lt;YourModule&gt;();   // repeat per module
/// var app = builder.Build();
/// await app.RunCortexPlatformAsync();
/// </code>
/// Everything the platform needs — Aspire service defaults, infrastructure, auth, rate limiting, realtime,
/// CORS, the admin console, and every platform endpoint — is wired here, identically for every host, so a
/// thin host never copies this boilerplate. The individual <c>AddCortex*</c>/<c>MapCortex*</c> methods remain
/// public for a host that needs to interleave custom wiring.
/// </summary>
public static class CortexHostSetup
{
    /// <summary>The CORS policy name applied to the Cortex SPA origins.</summary>
    public const string CorsPolicyName = "cortex-spa";

    /// <summary>
    /// Registers every platform service on the builder: Aspire service defaults, ProblemDetails + OpenAPI,
    /// Cortex infrastructure (EF/DI), authentication + authorization, rate limiting, request enrichment,
    /// enum-as-string JSON, SignalR realtime (with a Redis backplane when configured), and the SPA CORS policy.
    /// Call once, then add domain modules with <see cref="ModuleHostExtensions.AddCortexModule{TModule}"/>.
    /// </summary>
    public static WebApplicationBuilder AddCortexPlatform(this WebApplicationBuilder builder)
    {
        // The `cortex init` wizard's output — one declarative file layered BETWEEN appsettings.json
        // and appsettings.{Environment}.json (so environment files, user-secrets, and env vars all
        // still override wizard choices). Appending would jump the whole pipeline. Never holds secrets.
        var sources = ((IConfigurationBuilder)builder.Configuration).Sources;
        var appsettings = sources.LastOrDefault(s =>
            s is Microsoft.Extensions.Configuration.Json.JsonConfigurationSource { Path: "appsettings.json" });
        var wizardSource = new Microsoft.Extensions.Configuration.Json.JsonConfigurationSource
        {
            Path = "cortex.settings.json",
            Optional = true,
            ReloadOnChange = true,
        };
        wizardSource.ResolveFileProvider();
        sources.Insert(appsettings is null ? sources.Count : sources.IndexOf(appsettings) + 1, wizardSource);

        builder.AddServiceDefaults();

        builder.Services.AddProblemDetails();
        builder.Services.AddOpenApi();

        builder.AddCortexInfrastructure();

        builder.Services.AddCortexAuthentication(builder.Configuration, builder.Environment);
        builder.Services.AddAuthorization();
        builder.Services.AddCortexRateLimiting(builder.Configuration);
        builder.Services.AddScoped<IRequestEnricher, RequestEnricher>();

        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        // SignalR + identity-enrichment hub filter, with a Redis backplane when Redis is configured.
        builder.Services.AddCortexRealtime(builder.Configuration);

        // Inbound channels (docs/INBOUND_CHANNELS.md), both off unless configured:
        // WhatsApp (Meta Cloud API webhook) and email intake (IMAP polling) → authorized agent turns.
        builder.Services.AddCortexWhatsAppChannel(builder.Configuration);
        builder.Services.AddCortexEmailChannel(builder.Configuration);

        // Default to both dev front-ends: the domain UI (5173) and the admin console (5174). Override via config.
        var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>()
            ?? ["http://localhost:5173", "http://localhost:5174"];
        builder.Services.AddCors(options => options.AddPolicy(CorsPolicyName, policy => policy
            .WithOrigins(corsOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials()));

        return builder;
    }

    /// <summary>
    /// Builds the standard Cortex request pipeline and maps every platform endpoint (health, platform, chat,
    /// approvals, admin, AG-UI, the agent hub, and each installed module), plus the admin console. Exposed
    /// separately from <see cref="RunCortexPlatformAsync"/> for a host that needs to add its own endpoints or
    /// middleware around the platform's.
    /// </summary>
    public static WebApplication UseCortexPlatform(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();

        if (app.Environment.IsDevelopment())
        {
            app.MapOpenApi();
        }

        app.UseCors(CorsPolicyName);
        app.UseAuthentication();
        app.UseMiddleware<RequestEnrichmentMiddleware>();
        app.UseAuthorization();
        app.UseRateLimiter();

        // Serve the admin console (@cortex/admin-ui) at /admin when its built assets are present. No-op
        // otherwise, so a bare platform host still runs. Must come after UseAuthorization().
        app.UseCortexAdminConsole();

        // Serve the product's domain UI (@cortex/ui, built with the product's branding) at / when its
        // assets are present under wwwroot/app — the no-registry distribution path. No-op otherwise.
        app.UseCortexDomainUi();

        app.MapDefaultEndpoints();
        app.MapPlatformEndpoints();
        app.MapChatEndpoints();
        app.MapFileEndpoints();
        app.MapJobEndpoints();
        app.MapApprovalEndpoints();
        app.MapDisclosureEndpoints();
        app.MapNotificationEndpoints();
        app.MapAdminEndpoints();
        app.MapAdminExtensionEndpoints();
        app.MapUserInviteEndpoints();
        app.MapConnectorAdminEndpoints();
        app.MapConnectorOAuthEndpoints();
        app.MapAgui();
        app.MapCommerceEndpoints();
        app.MapWhatsAppChannel();
        app.MapHub<AgentHub>("/hubs/agent");
        app.MapCortexModules();

        return app;
    }

    /// <summary>
    /// Applies the platform database initializer and each installed module's migrations and seed data. Runs
    /// once at startup, before the app begins serving requests.
    /// </summary>
    public static async Task InitializeCortexAsync(this WebApplication app)
    {
        await DatabaseInitializer.InitializeAsync(app);
        await app.MigrateCortexModulesAsync();
        await app.SeedCortexModulesAsync();
    }

    /// <summary>
    /// Configures the pipeline (<see cref="UseCortexPlatform"/>), runs startup initialization
    /// (<see cref="InitializeCortexAsync"/>), and starts the host. The terminal call in a thin host's
    /// <c>Program.cs</c>.
    /// </summary>
    public static async Task RunCortexPlatformAsync(this WebApplication app)
    {
        app.UseCortexPlatform();
        await app.InitializeCortexAsync();
        await app.RunAsync();
    }
}
