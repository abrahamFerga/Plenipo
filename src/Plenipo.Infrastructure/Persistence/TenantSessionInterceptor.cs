using System.Data.Common;
using Plenipo.Core.Multitenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Plenipo.Infrastructure.Persistence;

/// <summary>
/// Publishes the ambient tenant to the database session as <c>plenipo.tenant_id</c>, which is what
/// the row-level-security policies on the retrieval tables read.
/// <para>
/// This is a BACKSTOP, not the access control. Tenant isolation is enforced in three places already:
/// EF's global query filters, the explicit <c>TenantId</c> predicate inside both retrieval arms, and
/// the collection gates. RLS exists because hybrid search is the one place the platform writes raw
/// SQL — the one place a future edit could forget a predicate — and a leak there would be
/// cross-tenant. Belt and braces on the highest-consequence query in the system.
/// </para>
/// <para>
/// Set on connection open rather than per command: EF acquires a connection around each command, so
/// the setting is refreshed exactly when a pooled physical connection is handed to this scope, and
/// it costs one round trip per open rather than one per query. Pooling makes the value sticky, which
/// is precisely why it is re-set on every open instead of only when it changes.
/// </para>
/// </summary>
public sealed class TenantSessionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    /// <summary>Matches the GUC the migration's policies read.</summary>
    private const string SettingName = "plenipo.tenant_id";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyAsync(connection, CancellationToken.None).GetAwaiter().GetResult();
        base.ConnectionOpened(connection, eventData);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        // set_config with a parameter, never string concatenation: the value is a Guid here, but the
        // habit is what keeps it a Guid. The empty string is the documented "no tenant" value — a
        // background scope or a migration runs unconstrained rather than seeing zero rows.
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('" + SettingName + "', @tenant, false)";

        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenantContext.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
