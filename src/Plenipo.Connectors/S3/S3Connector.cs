using System.ComponentModel;
using System.Text;
using Plenipo.Application.Authorization;
using Plenipo.Application.Files;
using Plenipo.Connectors.Sdk;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Plenipo.Connectors.S3;

/// <summary>
/// Service-auth connector to an S3 bucket (AWS or any S3-compatible endpoint — MinIO, Cloudflare
/// R2 — via the optional service URL). The tenant admin configures credentials (secret,
/// write-only, protected at rest) and the bucket; agents can then browse and, with approval,
/// import objects into the tenant file store.
/// </summary>
public sealed class S3Connector : IConnector
{
    public const string ConnectorId = "s3";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Amazon S3",
        Description = "Browse and fetch documents from an S3 bucket (AWS or any S3-compatible service) the tenant already uses.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "cloud",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = "AccessKeyId",
                Label = "Access key id",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "SecretAccessKey",
                Label = "Secret access key",
                Description = "Stored protected; never shown again after saving.",
                Required = true,
                IsSecret = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "Bucket",
                Label = "Bucket",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "Region",
                Label = "Region",
                Description = "AWS region (default us-east-1). Ignored when a service URL is set.",
            },
            new ConnectorSettingDescriptor
            {
                Key = "ServiceUrl",
                Label = "Service URL",
                Description = "Optional: an S3-compatible endpoint (MinIO, Cloudflare R2, …).",
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_s3_objects",
                Description = "List objects in the connected S3 bucket, optionally under a prefix.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_s3_objects"),
            },
            new ToolDescriptor
            {
                Name = "fetch_from_s3",
                Description = "Copy an object from the connected bucket into the tenant file store. Side-effecting: imports external data, requires human approval.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_s3"),
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IS3ObjectClient, S3ObjectClient>();
        services.AddScoped<S3Tools>();
        services.AddSingleton<IConnectorToolSource, S3ToolSource>();
    }
}

/// <summary>
/// The s3 connector's tools. The connection resolves from the tenant's protected settings per
/// call — no cached clients across tenants — and fetch copies into the tenant file store so
/// every downstream capability works on the imported file unchanged.
/// </summary>
public sealed class S3Tools(IConnectorSettings settings, IS3ObjectClient s3, IFileStore files)
{
    private const string NotConfigured =
        "The S3 connector is not enabled for this tenant (or is missing its connection settings). " +
        "An admin can configure it under Integrations.";

    [Description("List objects in the connected S3 bucket (key and size), optionally filtered by a key prefix. Use fetch_from_s3 to import one.")]
    public async Task<string> ListS3Objects(
        [Description("Optional key prefix to filter by, e.g. 'contracts/2026/'.")] string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var objects = await s3.ListAsync(connection, prefix, cancellationToken);
        if (objects.Count == 0)
        {
            return prefix is null ? "The connected bucket is empty." : $"No objects match prefix '{prefix}'.";
        }

        var sb = new StringBuilder("Objects in the connected bucket:\n");
        foreach (var o in objects)
        {
            sb.AppendLine($"- {o.Key} ({o.Size:N0} bytes)");
        }

        if (objects.Count == 100)
        {
            sb.AppendLine("… (list capped at 100 — narrow with a prefix)");
        }

        return sb.ToString();
    }

    [Description("Copy an object from the connected S3 bucket into the tenant file store and return its file id (usable with read_document, attach tools, and indexing).")]
    public async Task<string> FetchFromS3(
        [Description("The object key (full path), as shown by list_s3_objects.")] string key,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var download = await s3.DownloadAsync(connection, key, cancellationToken);
        if (download is null)
        {
            return $"No object with key '{key}' exists in the connected bucket. Use list_s3_objects to see what is available.";
        }

        await using var content = download.Content;
        var fileName = key.Contains('/') ? key[(key.LastIndexOf('/') + 1)..] : key;
        var stored = await files.SaveAsync(
            fileName, download.ContentType, content,
            source: $"connector:{S3Connector.ConnectorId}", cancellationToken);

        return $"Imported '{stored.FileName}' ({stored.SizeBytes:N0} bytes) from S3. File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }

    private async Task<S3Connection?> ResolveConnectionAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(S3Connector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue("AccessKeyId", out var accessKey) || string.IsNullOrWhiteSpace(accessKey) ||
            !values.TryGetValue("SecretAccessKey", out var secretKey) || string.IsNullOrWhiteSpace(secretKey) ||
            !values.TryGetValue("Bucket", out var bucket) || string.IsNullOrWhiteSpace(bucket))
        {
            return null;
        }

        values.TryGetValue("Region", out var region);
        values.TryGetValue("ServiceUrl", out var serviceUrl);
        return new S3Connection(
            accessKey, secretKey, bucket,
            string.IsNullOrWhiteSpace(region) ? "us-east-1" : region,
            string.IsNullOrWhiteSpace(serviceUrl) ? null : serviceUrl);
    }
}

/// <summary>Supplies the S3 connector's executable tools.</summary>
public sealed class S3ToolSource : IConnectorToolSource
{
    public string ConnectorId => S3Connector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<S3Tools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "list_s3_objects",
                Permission = Permissions.ForConnectorTool(ConnectorId, "list_s3_objects"),
                Function = AIFunctionFactory.Create(tools.ListS3Objects, name: "list_s3_objects"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "fetch_from_s3",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_from_s3"),
                Function = AIFunctionFactory.Create(tools.FetchFromS3, name: "fetch_from_s3"),
                RequiresApproval = true,
            },
        ];
    }
}
