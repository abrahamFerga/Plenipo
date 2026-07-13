using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.AspNetCore.Realtime;

/// <summary>
/// Configures the platform's real-time chat surface: SignalR with the identity-enrichment hub filter,
/// string-enum JSON, and — when a Redis connection is configured — the Redis backplane. The backplane
/// lets the agent hub scale horizontally: a message published by one API replica reaches SignalR
/// clients connected to any other replica. Without Redis it falls back to the in-memory backplane
/// (fine for a single instance / local dev).
/// </summary>
public static class RealtimeSetup
{
    /// <summary>Aspire connection name for the Redis resource backing the SignalR backplane.</summary>
    public const string RedisConnectionName = "plenipo-redis";

    public static ISignalRServerBuilder AddPlenipoRealtime(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<EnrichmentHubFilter>();

        var signalR = services
            .AddSignalR(options => options.AddFilter<EnrichmentHubFilter>())
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var redis = configuration.GetConnectionString(RedisConnectionName);
        if (!string.IsNullOrWhiteSpace(redis))
        {
            signalR.AddStackExchangeRedis(redis);
        }

        return signalR;
    }
}
