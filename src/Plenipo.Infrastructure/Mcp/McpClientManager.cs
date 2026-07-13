using System.Text;
using Plenipo.Application.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace Plenipo.Infrastructure.Mcp;

/// <summary>
/// Connects to the configured MCP servers in the BACKGROUND at startup, discovers their tools,
/// and caches the snapshot the tool source reads per turn. Connection or discovery failures are
/// logged and skipped — an unreachable MCP server degrades to "its tools aren't offered", it never
/// fails app start or a chat turn. Each discovered tool is renamed <c>{server}_{tool}</c> so names
/// (and therefore permissions and audit rows) are collision-free across servers.
/// </summary>
public sealed class McpClientManager(
    IOptions<McpOptions> options,
    ILoggerFactory loggerFactory,
    ILogger<McpClientManager> logger) : IMcpToolProvider, IHostedService, IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private volatile IReadOnlyList<McpServerTool> _tools = [];
    private Task? _connectTask;
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    public IReadOnlyList<McpServerTool> GetTools() => _tools;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Never block host startup on external processes/endpoints — connect in the background.
        _connectTask = Task.Run(() => ConnectAllAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Cancel only — the container disposes this singleton (both paths must stay idempotent,
        // since WebApplicationFactory can drive shutdown and disposal more than once).
        if (!_disposed)
        {
            await _stopping.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();

        if (_connectTask is not null)
        {
            try
            {
                await _connectTask;
            }
            catch
            {
                // Connection errors were already logged per server.
            }
        }

        foreach (var client in _clients)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Disposing an MCP client failed.");
            }
        }

        _clients.Clear();
        _stopping.Dispose();
    }

    private async Task ConnectAllAsync(CancellationToken cancellationToken)
    {
        var discovered = new List<McpServerTool>();
        foreach (var server in options.Value.Servers)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var name = SanitizeName(server.Name);
            if (name.Length == 0)
            {
                logger.LogWarning("Skipping an MCP server with no usable name.");
                continue;
            }

            try
            {
                var client = await McpClient.CreateAsync(CreateTransport(server), loggerFactory: loggerFactory, cancellationToken: cancellationToken);
                _clients.Add(client);

                var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
                foreach (var tool in tools)
                {
                    discovered.Add(new McpServerTool(
                        name,
                        tool.WithName(BuildToolName(name, tool.Name)),
                        server.RequiresApproval));
                }

                if (logger.IsEnabled(LogLevel.Information))
                {
                    logger.LogInformation("MCP server '{Server}' connected: {Count} tool(s) discovered.", name, tools.Count);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MCP server '{Server}' is unavailable; its tools will not be offered.", name);
            }
        }

        _tools = discovered;
    }

    private static IClientTransport CreateTransport(McpServerConfig server) =>
        server.Transport.Equals("Http", StringComparison.OrdinalIgnoreCase)
            ? new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url ?? throw new InvalidOperationException($"MCP server '{server.Name}': Url is required for the Http transport.")),
            })
            : new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.Command ?? throw new InvalidOperationException($"MCP server '{server.Name}': Command is required for the Stdio transport."),
                Arguments = server.Arguments,
            });

    /// <summary>The canonical agent-facing tool name: <c>{server}_{tool}</c>, both sanitized.</summary>
    public static string BuildToolName(string serverName, string toolName) =>
        $"{SanitizeName(serverName)}_{SanitizeName(toolName)}";

    /// <summary>
    /// Tool names feed permissions (<c>tools.mcp.{name}</c>) and audit rows, so they're clamped to
    /// a safe alphabet: lowercased, anything outside [a-z0-9_-] becomes '_'.
    /// </summary>
    public static string SanitizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            builder.Append(ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' ? ch : '_');
        }

        return builder.ToString().Trim('_');
    }
}
