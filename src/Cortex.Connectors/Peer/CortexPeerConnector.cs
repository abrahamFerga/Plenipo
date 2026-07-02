using Cortex.Application.Authorization;
using Cortex.Connectors.Sdk;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Connectors.Peer;

/// <summary>
/// Connects one Cortex system to another. Verticals are separate SYSTEMS — a firm might run
/// Cortex-for-legal and Cortex-for-finance as independent deployments (own repos, own hosts, own
/// databases) — and this connector is how they talk: the local agent gets an <c>ask_peer_system</c>
/// tool that forwards a question to the peer's agent over the open AG-UI protocol and returns its
/// answer. The peer applies its OWN auth, RBAC, tool gating, and audit to the forwarded question —
/// this system never reaches into the peer's data, it only converses.
/// </summary>
public sealed class CortexPeerConnector : IConnector
{
    public const string ConnectorId = "cortex-peer";

    public const string BaseUrlSetting = "BaseUrl";
    public const string ModuleIdSetting = "ModuleId";
    public const string PeerNameSetting = "PeerName";
    public const string AuthHeaderNameSetting = "AuthHeaderName";
    public const string AuthHeaderValueSetting = "AuthHeaderValue";

    /// <summary>Named HttpClient, overridable in tests (in-memory TestServer handler).</summary>
    public const string HttpClientName = "cortex-peer";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Cortex peer system",
        Description = "Ask another Cortex deployment's agent questions (e.g. the firm's finance system from the legal system). The peer enforces its own auth, permissions, and audit.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "link",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = BaseUrlSetting,
                Label = "Peer base URL",
                Description = "The peer Cortex API base URL, e.g. https://finance.example.com",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = ModuleIdSetting,
                Label = "Peer module id",
                Description = "The module to converse with on the peer (e.g. 'finance').",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = PeerNameSetting,
                Label = "Display name",
                Description = "How the agent should refer to the peer (e.g. 'the finance system').",
            },
            new ConnectorSettingDescriptor
            {
                Key = AuthHeaderNameSetting,
                Label = "Auth header name",
                Description = "Optional header carrying the service credential (e.g. 'Authorization').",
            },
            new ConnectorSettingDescriptor
            {
                Key = AuthHeaderValueSetting,
                Label = "Auth header value",
                Description = "The credential sent to the peer (e.g. 'Bearer eyJ…'). Stored protected.",
                IsSecret = true,
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "ask_peer_system",
                Description = "Ask the connected peer Cortex system's agent a question and return its answer (the peer applies its own permissions and audit).",
                Permission = Permissions.ForConnectorTool(ConnectorId, "ask_peer_system"),
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(HttpClientName);
        services.AddScoped<CortexPeerTools>();
        services.AddSingleton<IConnectorToolSource, CortexPeerToolSource>();
    }
}
