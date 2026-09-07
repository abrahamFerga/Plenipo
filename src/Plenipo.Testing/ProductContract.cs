namespace Plenipo.Testing;

/// <summary>
/// What the kit needs to know about a product to run the platform's invariants against it. Every
/// value is read from the product's own manifest and role model — never invented — and the kit
/// verifies the tool names against the manifest before using them.
/// </summary>
/// <param name="ModuleId">The module's manifest id, also the AG-UI route segment.</param>
/// <param name="ReadTool">A tool the <paramref name="ApproverRole"/> may call without approval and
/// that is audited (the manifest default). Used to prove tool routing and audit isolation.</param>
/// <param name="WriteTool">A tool declared with <c>RequiresApproval = true</c>. Used to prove the
/// approval gate parks it, that a narrow role never sees it, and that approving executes it.</param>
/// <param name="ApproverRole">A role that may chat, call both tools, and decide approvals. Defaults
/// to the platform's <c>system_admin</c>; admin endpoints are always read as <c>system_admin</c>.</param>
/// <param name="NarrowRole">A role that may chat (<c>chat.use</c>) but must not hold the write
/// tool's permission nor <c>chat.approvals.manage</c>. The platform's built-in <c>user</c> role
/// satisfies this everywhere.</param>
/// <param name="ReadEndpoints">Tenant-scoped GET routes a second tenant must see nothing on, in
/// addition to the module's data-tab endpoints, which are probed automatically.</param>
/// <param name="ReadPrompt">The user turn that routes to <paramref name="ReadTool"/> on the Mock
/// provider. Defaults to the tool name spelled out, which the Mock matches by name tokens.</param>
/// <param name="WritePrompt">The user turn that routes to <paramref name="WriteTool"/>.</param>
/// <param name="DevTenant">The seeded development tenant slug.</param>
public sealed record ProductContract(
    string ModuleId,
    string ReadTool,
    string WriteTool,
    string ApproverRole = "system_admin",
    string NarrowRole = "user",
    IReadOnlyList<string>? ReadEndpoints = null,
    string? ReadPrompt = null,
    string? WritePrompt = null,
    string DevTenant = "dev")
{
    /// <summary>The routes from <see cref="ReadEndpoints"/>, never null.</summary>
    public IReadOnlyList<string> ReadRoutes => ReadEndpoints ?? [];

    /// <summary>The turn that routes to <see cref="ReadTool"/>.</summary>
    public string EffectiveReadPrompt => ReadPrompt ?? PromptFor(ReadTool);

    /// <summary>The turn that routes to <see cref="WriteTool"/>.</summary>
    public string EffectiveWritePrompt => WritePrompt ?? PromptFor(WriteTool);

    /// <summary>
    /// A turn the Mock provider routes to the named tool: it scores tools by how many of their
    /// name tokens (snake, kebab or space separated, four letters or longer) the text contains,
    /// so spelling the name out wins over every tool that shares fewer tokens.
    /// </summary>
    public static string PromptFor(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var spelled = toolName.Replace('_', ' ').Replace('-', ' ');
        return $"Please {spelled} for me, using a tool.";
    }
}
