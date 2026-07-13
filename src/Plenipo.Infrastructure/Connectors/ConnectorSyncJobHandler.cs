using System.Text.Json;
using Plenipo.Application.Connectors;
using Plenipo.Application.Files;
using Plenipo.Application.Jobs;
using Plenipo.Connectors.Sdk;
using Plenipo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Infrastructure.Connectors;

/// <summary>
/// Walks one sync binding: list the external location, import files whose stamp changed since the
/// last sync into the tenant file store, then hand the new file ids to the owning module's
/// <see cref="IConnectorSyncHandler"/> (attach + index). Incremental by construction — unchanged
/// items are skipped via the per-item stamp — and fail-closed on every seam: connector disabled,
/// sync source missing, or module handler missing all fail the job loudly.
/// </summary>
public sealed class ConnectorSyncJobHandler : IJobHandler
{
    public string Kind => ConnectorSyncJob.Kind;

    public async Task<string?> ExecuteAsync(JobExecutionContext context, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.Deserialize<ConnectorSyncArgs>(context.ArgumentsJson, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException("Connector sync arguments are missing.");

        var services = context.ScopedServices;
        var db = services.GetRequiredService<PlatformDbContext>();
        var files = services.GetRequiredService<IFileStore>();

        var binding = await db.ConnectorBindings.FirstOrDefaultAsync(b => b.Id == args.BindingId, cancellationToken)
            ?? throw new InvalidOperationException($"Sync binding {args.BindingId} does not exist.");

        if (!await services.GetRequiredService<ITenantConnectorStore>().IsEnabledAsync(binding.ConnectorId, cancellationToken))
        {
            throw new InvalidOperationException($"The '{binding.ConnectorId}' connector is not enabled for this tenant.");
        }

        var source = services.GetServices<IConnectorSyncSource>()
            .FirstOrDefault(s => string.Equals(s.ConnectorId, binding.ConnectorId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Connector '{binding.ConnectorId}' does not support sync.");

        var handler = services.GetServices<IConnectorSyncHandler>()
            .FirstOrDefault(h => string.Equals(h.ResourceType, binding.ResourceType, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No module handles synced files for resource type '{binding.ResourceType}'.");

        var listed = await source.ListAsync(binding.ExternalRef, cancellationToken)
            ?? throw new InvalidOperationException($"The '{binding.ConnectorId}' connector is not configured (no settings for this tenant).");

        var state = string.IsNullOrWhiteSpace(binding.SyncedItemsJson)
            ? new Dictionary<string, SyncedItem>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, SyncedItem>>(binding.SyncedItemsJson) ?? [];

        var imported = new List<Guid>();
        for (var i = 0; i < listed.Count; i++)
        {
            await context.ReportProgressAsync(
                (int)(i * 100.0 / Math.Max(1, listed.Count)),
                $"{i}/{listed.Count} files checked",
                cancellationToken);

            var item = listed[i];
            if (state.TryGetValue(item.Id, out var known) && known.Stamp == item.ContentStamp)
            {
                continue; // unchanged since last sync
            }

            await using var content = await source.OpenAsync(binding.ExternalRef, item.Id, cancellationToken);
            if (content is null)
            {
                continue; // vanished between list and open — next sync reconciles
            }

            var stored = await files.SaveAsync(
                item.Name, item.ContentType, content,
                source: $"connector:{binding.ConnectorId}", cancellationToken);
            state[item.Id] = new SyncedItem(stored.Id, item.ContentStamp);
            imported.Add(stored.Id);
        }

        if (imported.Count > 0)
        {
            await handler.OnFilesSyncedAsync(binding.ResourceId, imported, cancellationToken);
        }

        binding.SyncedItemsJson = JsonSerializer.Serialize(state);
        binding.LastSyncedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        await context.ReportProgressAsync(100, $"{listed.Count}/{listed.Count} files checked", cancellationToken);
        return JsonSerializer.Serialize(
            new { listed = listed.Count, imported = imported.Count, skipped = listed.Count - imported.Count },
            JsonSerializerOptions.Web);
    }

    private sealed record SyncedItem(Guid FileId, string Stamp);
}
