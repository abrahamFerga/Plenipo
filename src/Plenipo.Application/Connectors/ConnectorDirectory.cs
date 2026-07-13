namespace Plenipo.Application.Connectors;

/// <summary>
/// The directory of first-party connectors that exist in the Plenipo ecosystem — including ones
/// this deployment did NOT install. The admin Integrations page shows these as "available", with
/// the package and registration call an operator needs, so discovering an integration doesn't
/// require reading platform source. Products' own connectors (e.g. Networthy's Plaid) are theirs
/// to ship and don't appear here; this list is only what the platform itself offers.
/// </summary>
public static class ConnectorDirectory
{
    /// <summary>A connector known to the ecosystem: enough metadata to find and install it.</summary>
    public sealed record KnownConnector(
        string Id, string DisplayName, string Description, string Package, string Registration);

    private const string BundlePackage = "Plenipo.Connectors";
    private const string BundleRegistration = "builder.AddPlenipoConnectors()";

    /// <summary>Every first-party connector, in display order.</summary>
    public static readonly IReadOnlyList<KnownConnector> All =
    [
        new("local-folder", "Local folder",
            "A keyless dev/test connector over a local directory — every deployment can exercise the connector pipeline without credentials.",
            BundlePackage, BundleRegistration),
        new("azure-blob", "Azure Blob Storage",
            "Service-auth access to an Azure Blob container (connection string configured per tenant).",
            BundlePackage, BundleRegistration),
        new("s3", "Amazon S3",
            "Service-auth access to an S3 bucket (access key configured per tenant).",
            BundlePackage, BundleRegistration),
        new("msgraph", "Microsoft 365 (Graph)",
            "Each user connects their own Microsoft account; OneDrive/SharePoint fetches run under that user's own permissions.",
            BundlePackage, BundleRegistration),
        new("google-drive", "Google Drive",
            "Each user connects their own Google account; Drive fetches run under that user's own permissions.",
            BundlePackage, BundleRegistration),
        new("documenso", "Documenso e-signature",
            "Send documents for signature via a hosted or self-hosted Documenso instance (API token per tenant).",
            BundlePackage, BundleRegistration),
        new("plenipo-peer", "Plenipo peer",
            "Talk to another Plenipo-based system — how separate verticals exchange data as peers.",
            BundlePackage, BundleRegistration),
    ];
}
