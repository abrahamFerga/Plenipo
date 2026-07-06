namespace Cortex.Modules.Sdk;

// Field names here (TabColumn.Field, TabEditorField.Field, {field} placeholders) address the
// endpoint's JSON — which the host serializes camelCase — so declare them camelCase
// ("monthlyLimit"), even though the C# property they came from is PascalCase.

/// <summary>A column in a tab's server-driven data view: which row field to show, and its header.</summary>
public sealed record TabColumn(string Field, string Header);

/// <summary>
/// A field in a tab's generic editor form: which row/body property, its label, and its shape.
/// <paramref name="Numeric"/> makes the shell render a number input and post a JSON number, so
/// endpoints binding <c>decimal</c>/<c>int</c> properties work without string-parsing shims.
/// </summary>
public sealed record TabEditorField(string Field, string Label, bool Multiline = false, bool Required = true, bool Numeric = false);

/// <summary>
/// Optional mutation affordances for a server-driven tab: when declared, the shell's generic table
/// gains Add / Edit / Delete without the module shipping any custom UI. The UI shows the
/// affordances only to callers holding <see cref="Permission"/> — the endpoints themselves stay
/// authorization-gated server-side regardless.
/// </summary>
public sealed record TabEditor
{
    /// <summary>POST target for add and edit; the body is a JSON object of <see cref="Fields"/> values.</summary>
    public required string UpsertEndpoint { get; init; }

    /// <summary>
    /// Optional DELETE target with one <c>{field}</c> placeholder substituted from the row
    /// (e.g. <c>/api/legal/clauses/{slug}</c>). Null = no delete affordance.
    /// </summary>
    public string? DeleteEndpoint { get; init; }

    /// <summary>Permission gating the UI affordances (e.g. <c>legal.library.manage</c>).</summary>
    public required string Permission { get; init; }

    /// <summary>The editable fields, in form order.</summary>
    public IReadOnlyList<TabEditorField> Fields { get; init; } = [];

    /// <summary>
    /// The row field that identifies a record for editing (the upsert endpoint's match key, shown
    /// read-only when editing). Null = rows are add/delete only, no per-row Edit.
    /// </summary>
    public string? KeyField { get; init; }
}

/// <summary>
/// A navigation tab a module contributes to the dashboard. The React shell builds its sidebar and
/// routes purely from the tabs returned by the API, filtered by the caller's permissions — the
/// frontend hardcodes no domain routes.
/// </summary>
public sealed record TabDescriptor
{
    /// <summary>Stable id within the module (e.g. "cases", "transactions").</summary>
    public required string Id { get; init; }

    /// <summary>Sidebar label.</summary>
    public required string Label { get; init; }

    /// <summary>Client route the tab renders at (e.g. "/finance/transactions").</summary>
    public required string Route { get; init; }

    /// <summary>Optional icon name (resolved by the frontend icon set).</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Permission required to see and open this tab. <c>null</c> means visible to any user who has
    /// the module enabled. Tabs the user lacks permission for are omitted, not merely hidden.
    /// </summary>
    public string? Permission { get; init; }

    /// <summary>Sort order within the module's sidebar group.</summary>
    public int Order { get; init; }

    /// <summary>
    /// Optional: a GET endpoint returning a JSON array, which the shell renders as a generic table using
    /// <see cref="Columns"/>. Lets a module's list-style tab show real data without shipping any custom UI.
    /// When null, the tab renders a placeholder (or content supplied by the consuming app).
    /// </summary>
    public string? DataEndpoint { get; init; }

    /// <summary>Columns for the <see cref="DataEndpoint"/> table. Empty falls back to the row's own fields.</summary>
    public IReadOnlyList<TabColumn> Columns { get; init; } = [];

    /// <summary>Optional add/edit/delete affordances for the <see cref="DataEndpoint"/> table.</summary>
    public TabEditor? Editor { get; init; }

    /// <summary>
    /// Optional drill-down: a GET endpoint with one <c>{field}</c> placeholder substituted from the
    /// row (e.g. <c>/api/legal/matters/{id}/detail</c>), returning a DETAIL DOCUMENT the shell
    /// renders generically — <c>{ title, subtitle?, sections: [{ heading, text? } | { heading,
    /// columns: [{field, header}], rows: [...] }] }</c>. Gives list rows a composed detail view
    /// (a matter's parties/deadlines/tasks/documents in one page) with no custom UI.
    /// </summary>
    public string? DetailEndpoint { get; init; }

    /// <summary>
    /// Optional friendly empty-state message the shell shows when the tab has no <see cref="DataEndpoint"/>
    /// and the consuming app supplies no content (e.g. "Your food diary will appear here."). Gives a tab an
    /// intentional placeholder — useful for a capability that's declared in the manifest but not yet built —
    /// instead of a generic "nothing here" message.
    /// </summary>
    public string? Placeholder { get; init; }
}
