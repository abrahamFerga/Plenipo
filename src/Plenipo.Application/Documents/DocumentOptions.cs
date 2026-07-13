namespace Plenipo.Application.Documents;

/// <summary>
/// Deployment-level switch for the platform document tools (read_document, generate_pdf,
/// list_documents, ocr_document), bound from the "Documents" configuration section. ON by default —
/// they're a base capability with no external dependencies — and this is the third gate in the
/// layering, not the only one:
/// <list type="bullet">
///   <item>this flag decides whether the tools exist in the DEPLOYMENT at all;</item>
///   <item>per TENANT, admins turn them on/off by editing role baselines in the RBAC editor
///   (the <c>tools.documents.*</c> permissions — runtime-editable, like any tool grant);</item>
///   <item>per USER, the ordinary permission model applies before the model sees a schema.</item>
/// </list>
/// Disabling only removes the agent-facing tools: the file store and the module-facing seams
/// (<see cref="IDocumentReader"/>, <see cref="IPdfRenderer"/>) stay — module code depends on them.
/// </summary>
public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    public bool Enabled { get; set; } = true;
}
