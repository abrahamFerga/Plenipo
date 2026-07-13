using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Plenipo.Application.Authorization;
using Plenipo.Application.Security;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.Peer;

/// <summary>
/// The peer connector's tool: one AG-UI turn against the configured peer (POST
/// /api/agui/{module}, SSE back), assembled from the TEXT_MESSAGE_CONTENT deltas. AG-UI is the
/// same open protocol the peer's own web UI speaks — no private API between systems.
/// </summary>
public sealed class PlenipoPeerTools(
    IConnectorSettings settings,
    IHttpClientFactory httpClients,
    OutboundUrlPolicy outboundUrls)
{
    private const string NotConfigured =
        "The Plenipo peer connector is not enabled for this tenant (or is missing its base URL / module id). " +
        "An admin can configure it under Integrations.";

    [Description("Ask the connected peer Plenipo system's agent a question (e.g. ask the finance system about a client's invoices from here). Returns the peer agent's answer. The peer enforces its own permissions and audits the exchange.")]
    public async Task<string> AskPeerSystem(
        [Description("The question to send to the peer system's agent.")] string question,
        CancellationToken cancellationToken = default)
    {
        var values = await settings.GetAsync(PlenipoPeerConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue(PlenipoPeerConnector.BaseUrlSetting, out var baseUrl) ||
            !values.TryGetValue(PlenipoPeerConnector.ModuleIdSetting, out var moduleId) ||
            string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(moduleId))
        {
            return NotConfigured;
        }

        var peerName = values.TryGetValue(PlenipoPeerConnector.PeerNameSetting, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : "the peer system";

        Uri destination;
        try
        {
            destination = await outboundUrls.RequireAllowedAsync(
                $"{baseUrl.TrimEnd('/')}/api/agui/{Uri.EscapeDataString(moduleId)}", cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return $"The peer destination is blocked by the deployment's outbound URL policy: {ex.Message}";
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            destination)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    messages = new[] { new { id = Guid.NewGuid().ToString("N"), role = "user", content = question } },
                }),
                Encoding.UTF8,
                "application/json"),
        };

        if (values.TryGetValue(PlenipoPeerConnector.AuthHeaderNameSetting, out var headerName) &&
            values.TryGetValue(PlenipoPeerConnector.AuthHeaderValueSetting, out var headerValue) &&
            !string.IsNullOrWhiteSpace(headerName) && !string.IsNullOrWhiteSpace(headerValue))
        {
            request.Headers.TryAddWithoutValidation(headerName, headerValue);
        }

        try
        {
            var client = httpClients.CreateClient(PlenipoPeerConnector.HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"{peerName} declined the request ({(int)response.StatusCode}). Check the connector's URL, module id, and credential.";
            }

            var (text, error) = await ReadAguiStreamAsync(response, cancellationToken);
            if (error is not null)
            {
                return $"{peerName} reported an error: {error}";
            }

            return string.IsNullOrWhiteSpace(text)
                ? $"{peerName} returned no answer."
                : $"Answer from {peerName}:\n{text}";
        }
        catch (HttpRequestException ex)
        {
            return $"Could not reach {peerName}: {ex.Message}";
        }
    }

    /// <summary>Assembles the assistant text from an AG-UI SSE stream (TEXT_MESSAGE_CONTENT deltas).</summary>
    private static async Task<(string Text, string? Error)> ReadAguiStreamAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            using var json = JsonDocument.Parse(line["data: ".Length..]);
            var type = json.RootElement.GetProperty("type").GetString();
            if (type == "TEXT_MESSAGE_CONTENT" && json.RootElement.TryGetProperty("delta", out var delta))
            {
                text.Append(delta.GetString());
            }
            else if (type == "RUN_ERROR")
            {
                var message = json.RootElement.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
                return (text.ToString(), message);
            }
        }

        return (text.ToString(), null);
    }
}

/// <summary>Supplies the peer connector's executable tools.</summary>
public sealed class PlenipoPeerToolSource : IConnectorToolSource
{
    public string ConnectorId => PlenipoPeerConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<PlenipoPeerTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "ask_peer_system",
                Permission = Permissions.ForConnectorTool(ConnectorId, "ask_peer_system"),
                Function = AIFunctionFactory.Create(tools.AskPeerSystem, name: "ask_peer_system"),
            },
        ];
    }
}
