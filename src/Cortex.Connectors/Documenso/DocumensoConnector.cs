using System.ComponentModel;
using Cortex.Application.Authorization;
using Cortex.Application.Files;
using Cortex.Application.Security;
using Cortex.Connectors.Sdk;
using Cortex.Modules.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Cortex.Connectors.Documenso;

/// <summary>
/// Service-auth connector to Documenso — the open-source e-signature platform (hosted or
/// self-hosted; the connector only speaks its public REST API). The tenant admin configures the
/// instance URL and an API token (secret, write-only, protected at rest); agents can then send a
/// stored document out for signature (with approval), track it, and file the signed copy back.
/// </summary>
public sealed class DocumensoConnector : IConnector
{
    public const string ConnectorId = "documenso";

    public ConnectorManifest Manifest { get; } = new()
    {
        Id = ConnectorId,
        DisplayName = "Documenso e-signature",
        Description = "Send documents for legally binding e-signature via Documenso (hosted or self-hosted) and file the signed copies back.",
        AuthMode = ConnectorAuthMode.Service,
        Icon = "pen-tool",
        Settings =
        [
            new ConnectorSettingDescriptor
            {
                Key = "BaseUrl",
                Label = "Instance URL",
                Description = "https://app.documenso.com, or your self-hosted instance.",
                Required = true,
            },
            new ConnectorSettingDescriptor
            {
                Key = "ApiToken",
                Label = "API token",
                Description = "Stored protected; never shown again after saving.",
                Required = true,
                IsSecret = true,
            },
        ],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "send_for_signature",
                Description = "Send a stored document out for e-signature. Side-effecting and outward-facing: the recipient is emailed a signing request; requires human approval.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "send_for_signature"),
                RequiresApproval = true,
            },
            new ToolDescriptor
            {
                Name = "check_signature_status",
                Description = "Check where a signature request stands (who has signed, who is pending).",
                Permission = Permissions.ForConnectorTool(ConnectorId, "check_signature_status"),
            },
            new ToolDescriptor
            {
                Name = "fetch_signed_document",
                Description = "Copy a completed, signed document back into the tenant file store. Side-effecting: imports external data, requires human approval.",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_signed_document"),
                RequiresApproval = true,
            },
        ],
    };

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpClient(DocumensoApiClient.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(sp =>
                sp.GetRequiredService<OutboundUrlPolicy>().CreateHttpMessageHandler());
        services.AddSingleton<IDocumensoClient, DocumensoApiClient>();
        services.AddScoped<DocumensoTools>();
        services.AddSingleton<IConnectorToolSource, DocumensoToolSource>();
    }
}

/// <summary>
/// The documenso connector's tools. The connection resolves from the tenant's protected settings
/// per call; documents flow file store → Documenso → file store, so the signed original lives
/// with everything else the platform knows how to read, attach, and index.
/// </summary>
public sealed class DocumensoTools(IConnectorSettings settings, IDocumensoClient documenso, IFileStore files)
{
    private const string NotConfigured =
        "The Documenso connector is not enabled for this tenant (or is missing its instance URL / API token). " +
        "An admin can configure it under Integrations.";

    [Description("Send a stored document (by file id) out for e-signature. The recipient is emailed a signing request. Returns the signature-request id for check_signature_status.")]
    public async Task<string> SendForSignature(
        [Description("The stored file id (a GUID) of the document to sign — e.g. a generated PDF.")] string fileId,
        [Description("The signer's email address.")] string recipientEmail,
        [Description("The signer's display name.")] string recipientName,
        [Description("Optional request title; defaults to the file name.")] string? title = null,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        if (!Guid.TryParse(fileId, out var id))
        {
            return $"'{fileId}' is not a file id. Use the id from list_documents or a generate_pdf result.";
        }

        var stored = await files.FindAsync(id, cancellationToken);
        await using var content = await files.OpenReadAsync(id, cancellationToken);
        if (stored is null || content is null)
        {
            return $"No stored file with id {id} exists. Use list_documents to find the right one.";
        }

        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        var documentId = await documenso.SendForSignatureAsync(connection, new SignatureRequest(
            string.IsNullOrWhiteSpace(title) ? stored.FileName : title.Trim(),
            stored.FileName, buffer.ToArray(), recipientEmail.Trim(), recipientName.Trim()), cancellationToken);

        return $"Sent '{stored.FileName}' to {recipientEmail} for signature. " +
               $"Signature-request id: {documentId} (track with check_signature_status; " +
               "fetch_signed_document files the signed copy once completed).";
    }

    [Description("Check a signature request's status: overall state plus each recipient's signing status.")]
    public async Task<string> CheckSignatureStatus(
        [Description("The signature-request id returned by send_for_signature.")] string requestId,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var status = await documenso.GetStatusAsync(connection, requestId.Trim(), cancellationToken);
        if (status is null)
        {
            return $"No signature request '{requestId}' exists on the connected Documenso instance.";
        }

        return $"Signature request {status.DocumentId}: {status.Status}" +
               $"{(status.Recipients.Length == 0 ? "" : $" — {status.Recipients}")}";
    }

    [Description("Copy a COMPLETED signed document back into the tenant file store and return its file id (usable with attach tools and read_document).")]
    public async Task<string> FetchSignedDocument(
        [Description("The signature-request id returned by send_for_signature.")] string requestId,
        CancellationToken cancellationToken = default)
    {
        var connection = await ResolveConnectionAsync(cancellationToken);
        if (connection is null)
        {
            return NotConfigured;
        }

        var signed = await documenso.DownloadSignedAsync(connection, requestId.Trim(), cancellationToken);
        if (signed is null)
        {
            return $"Signature request '{requestId}' has no completed document yet — check_signature_status first.";
        }

        await using var content = signed.Content;
        var stored = await files.SaveAsync(
            signed.FileName, "application/pdf", content,
            source: $"connector:{DocumensoConnector.ConnectorId}", cancellationToken);

        return $"Filed signed document '{stored.FileName}' ({stored.SizeBytes:N0} bytes). " +
               $"File id: {stored.Id}. Download: /api/files/{stored.Id}";
    }

    private async Task<DocumensoConnection?> ResolveConnectionAsync(CancellationToken cancellationToken)
    {
        var values = await settings.GetAsync(DocumensoConnector.ConnectorId, cancellationToken);
        if (values is null ||
            !values.TryGetValue("BaseUrl", out var baseUrl) || string.IsNullOrWhiteSpace(baseUrl) ||
            !values.TryGetValue("ApiToken", out var token) || string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        return new DocumensoConnection(baseUrl, token);
    }
}

/// <summary>Supplies the documenso connector's executable tools.</summary>
public sealed class DocumensoToolSource : IConnectorToolSource
{
    public string ConnectorId => DocumensoConnector.ConnectorId;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scopedServices)
    {
        var tools = scopedServices.GetRequiredService<DocumensoTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "send_for_signature",
                Permission = Permissions.ForConnectorTool(ConnectorId, "send_for_signature"),
                Function = AIFunctionFactory.Create(tools.SendForSignature, name: "send_for_signature"),
                RequiresApproval = true,
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "check_signature_status",
                Permission = Permissions.ForConnectorTool(ConnectorId, "check_signature_status"),
                Function = AIFunctionFactory.Create(tools.CheckSignatureStatus, name: "check_signature_status"),
            },
            new ModuleTool
            {
                ModuleId = $"connectors.{ConnectorId}",
                Name = "fetch_signed_document",
                Permission = Permissions.ForConnectorTool(ConnectorId, "fetch_signed_document"),
                Function = AIFunctionFactory.Create(tools.FetchSignedDocument, name: "fetch_signed_document"),
                RequiresApproval = true,
            },
        ];
    }
}
