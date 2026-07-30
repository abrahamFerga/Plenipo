namespace Plenipo.Modules.Sdk;

// Field names here (TabColumn.Field, TabEditorField.Field, {field} placeholders) address the
// endpoint's JSON — which the host serializes camelCase — so declare them camelCase
// ("monthlyLimit"), even though the C# property they came from is PascalCase.

/// <summary>
/// A column in a tab's server-driven data view: which row field to show, and its header.
/// <paramref name="Masked"/> is the display-side companion of the <c>[Pii]</c> attribute: declare
/// it on columns carrying account numbers, tokens, or similar — the shell renders the value
/// masked (last four characters showing) behind an explicit per-cell reveal toggle. Masking is a
/// screen-privacy affordance, not access control: the caller was already authorized to read the
/// value, it just shouldn't sit exposed on a shared or shoulder-surfable screen.
/// </summary>
public sealed record TabColumn(string Field, string Header, bool Masked = false)
{
    /// <summary>
    /// Optional: makes this column's value navigable, turning a named relation into a way to go
    /// look at it. A CLIENT route (the shell's own router — <c>/finance/accounts</c>), never an API
    /// path, optionally with <c>{field}</c> placeholders substituted from the row exactly as
    /// <see cref="TabRowAction.EndpointTemplate"/> does. Placeholders are optional on purpose: a
    /// fixed route that lands every row on the same tab is the common, useful case (a statement's
    /// destination account is on the accounts tab), so this is deliberately NOT validated for a
    /// <c>{</c> the way a row action's endpoint is.
    /// <para>
    /// Declared as an init-only property rather than a fourth positional parameter so the primary
    /// constructor and generated <c>Deconstruct</c> stay binary-compatible for products already
    /// compiled against a published package.
    /// </para>
    /// </summary>
    public string? LinkTemplate { get; init; }
}

/// <summary>
/// One choice in a field's select: the <paramref name="Value"/> that gets posted, and the
/// <paramref name="Label"/> a human reads. Canonical identifiers are often unreadable —
/// <c>America/Mexico_City</c> is exactly right to STORE and exactly wrong to SHOW — so a field
/// whose values are machine identifiers should label them. A bare string converts implicitly to
/// an option labelled with its own value, so <c>Options: ["checking", "savings"]</c> reads as it
/// always did; spread an existing array with <c>Options: [.. Codes]</c>.
/// </summary>
public sealed record TabEditorOption(string Value, string Label)
{
    public static implicit operator TabEditorOption(string value) => new(value, value);
}

/// <summary>Tokens for <see cref="TabEditorField.DefaultFrom"/> — values only the VIEWER's browser
/// can supply, which a manifest declared at startup cannot possibly know.</summary>
public static class FieldDefaultSources
{
    /// <summary>The viewer's own IANA time zone (e.g. <c>America/Mexico_City</c>). Ignored unless
    /// it is one of the field's options, so a module's curated list is never overridden with a
    /// value its endpoint would reject.</summary>
    public const string BrowserTimeZone = "browser-timezone";

    /// <summary>The ISO-4217 currency of the viewer's browser locale region (e.g. an <c>es-MX</c>
    /// browser → <c>MXN</c>). A GUESS, deliberately: a locale is not a bank account, so it is only
    /// a starting point the user can change, and — like every default — it is ignored unless it is
    /// one of the field's options.</summary>
    public const string BrowserCurrency = "browser-currency";
}

/// <summary>
/// A field in a tab's generic editor form: which row/body property, its label, and its shape.
/// <paramref name="Numeric"/> makes the shell render a number input and post a JSON number, so
/// endpoints binding <c>decimal</c>/<c>int</c> properties work without string-parsing shims.
/// A field whose valid values are KNOWN should say so — the shell renders a select instead of a
/// free-text guessing game: <paramref name="Options"/> for a fixed vocabulary (directions,
/// cadences), or <paramref name="OptionsEndpoint"/> + <paramref name="OptionsField"/> to draw
/// the choices from live data (account names from <c>/api/finance/accounts</c>'s <c>name</c>).
/// <para>
/// The shell never pre-picks an option on its own — but a field may say what it should start as.
/// <paramref name="Default"/> is a constant the manifest knows; <paramref name="DefaultFrom"/>
/// (see <see cref="FieldDefaultSources"/>) is for what only the viewer's browser knows, like their
/// time zone. Both are a starting point the user can always change, never a value posted behind
/// their back: an empty form still posts nothing.
/// </para>
/// <para>
/// <paramref name="Group"/> is a purely presentational hint the shell uses when it lays a form out
/// as sections (a <see cref="TabDescriptor.Singleton"/> tab): fields sharing a group render under
/// one heading, in declaration order. Ungrouped fields sit above the first heading. Ignored by the
/// table editor, where fields are always a flat list.
/// </para>
/// </summary>
public sealed record TabEditorField(
    string Field,
    string Label,
    bool Multiline = false,
    bool Required = true,
    bool Numeric = false,
    IReadOnlyList<TabEditorOption>? Options = null,
    string? OptionsEndpoint = null,
    string? OptionsField = null,
    bool Masked = false,
    string? Default = null,
    string? DefaultFrom = null,
    string? Group = null);

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
/// The geometry a <see cref="TabChart"/> renders with. Each kind reads the same three field
/// hints, but with kind-specific meaning — documented per member.
/// </summary>
public enum TabChartKind
{
    /// <summary>
    /// Time-series line (the default): <see cref="TabChart.XField"/> is an ISO date string,
    /// <see cref="TabChart.YField"/> the numeric measure, one line per distinct
    /// <see cref="TabChart.SeriesField"/> value.
    /// </summary>
    Line,

    /// <summary>
    /// Proportional donut: <see cref="TabChart.XField"/> is the category label,
    /// <see cref="TabChart.YField"/> its numeric share. Rows with the same label are summed;
    /// the shell caps named segments and rolls the tail into "Other".
    /// <see cref="TabChart.SeriesField"/> is ignored.
    /// </summary>
    Donut,

    /// <summary>
    /// Categorical grouped bars: <see cref="TabChart.XField"/> is the category label (rendered
    /// in row order), <see cref="TabChart.YField"/> the numeric measure, one bar per distinct
    /// <see cref="TabChart.SeriesField"/> value within each category (e.g. income vs. expense
    /// per month). The value axis always includes zero.
    /// </summary>
    Bar,
}

/// <summary>
/// A chart rendered over the tab's <see cref="TabDescriptor.DataEndpoint"/> rows — the shell
/// draws it instead of the generic table. <see cref="Kind"/> picks the geometry (time-series
/// line by default). One y-scale by design (never a dual axis).
/// </summary>
public sealed record TabChart
{
    /// <summary>Geometry to render. Defaults to <see cref="TabChartKind.Line"/>.</summary>
    public TabChartKind Kind { get; init; } = TabChartKind.Line;

    /// <summary>
    /// Row field holding the x value — an ISO date string for <see cref="TabChartKind.Line"/>
    /// (e.g. "takenOn"), the category label for <see cref="TabChartKind.Donut"/> and
    /// <see cref="TabChartKind.Bar"/> (e.g. "category").
    /// </summary>
    public required string XField { get; init; }

    /// <summary>Row field holding the numeric y value (e.g. "netWorth").</summary>
    public required string YField { get; init; }

    /// <summary>
    /// Optional row field splitting rows into one line (<see cref="TabChartKind.Line"/>) or one
    /// bar per category (<see cref="TabChartKind.Bar"/>) per distinct value (e.g. "currencyCode",
    /// "direction"). Ignored by <see cref="TabChartKind.Donut"/>.
    /// </summary>
    public string? SeriesField { get; init; }

    /// <summary>Axis label for the measure (e.g. "Net worth").</summary>
    public string? YLabel { get; init; }
}

/// <summary>
/// A tab-level command button: the shell POSTs (empty body) to <see cref="Endpoint"/> and
/// refreshes the tab's data with the returned message shown to the user. The UI shows the button
/// only to callers holding <see cref="Permission"/>; the endpoint stays authorization-gated
/// server-side regardless.
/// </summary>
public sealed record TabAction
{
    public required string Id { get; init; }

    /// <summary>Button label (e.g. "Approve batch").</summary>
    public required string Label { get; init; }

    /// <summary>POST target (e.g. "/api/finance/imports/latest/approve").</summary>
    public required string Endpoint { get; init; }

    /// <summary>Permission gating the button. Null = any user who can see the tab.</summary>
    public string? Permission { get; init; }

    /// <summary>Optional confirmation prompt shown before the POST (for consequential actions).</summary>
    public string? Confirm { get; init; }
}

/// <summary>
/// A per-row command button on a server-driven tab's table: the shell POSTs (empty body) to
/// <see cref="EndpointTemplate"/> with its <c>{field}</c> placeholder(s) substituted from the
/// row's values, shows the returned message, and refreshes the tab's data. This is how a
/// list-of-workpieces tab acts on ONE row (approve THIS import batch, retry THIS job) — a
/// tab-level <see cref="TabAction"/> can only ever hit a single fixed URL. The UI shows the
/// button only to callers holding <see cref="Permission"/>; the endpoint stays
/// authorization-gated server-side regardless.
/// </summary>
public sealed record TabRowAction
{
    public required string Id { get; init; }

    /// <summary>Button label (e.g. "Approve").</summary>
    public required string Label { get; init; }

    /// <summary>
    /// POST target with <c>{field}</c> placeholder(s) substituted from the row
    /// (e.g. <c>/api/finance/imports/{id}/approve</c>).
    /// </summary>
    public required string EndpointTemplate { get; init; }

    /// <summary>Permission gating the button. Null = any user who can see the tab.</summary>
    public string? Permission { get; init; }

    /// <summary>Optional confirmation prompt shown before the POST (for consequential actions).</summary>
    public string? Confirm { get; init; }
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
    /// Land on this tab when the app opens (instead of Chat — the shell's default landing).
    /// Opt-in: with no Home tab declared anywhere, the shell stays chat-first exactly as before.
    /// The first Home tab of the active module wins; Chat remains first in the nav either way.
    /// </summary>
    public bool Home { get; init; }

    /// <summary>
    /// Optional: a GET endpoint returning a JSON array, which the shell renders as a generic table using
    /// <see cref="Columns"/>. Lets a module's list-style tab show real data without shipping any custom UI.
    /// When null, the tab renders a placeholder (or content supplied by the consuming app).
    /// </summary>
    public string? DataEndpoint { get; init; }

    /// <summary>Columns for the <see cref="DataEndpoint"/> table. Empty falls back to the row's own fields.</summary>
    public IReadOnlyList<TabColumn> Columns { get; init; } = [];

    /// <summary>
    /// This <see cref="DataEndpoint"/> is ONE config object, not a list. The shell renders its single
    /// row as a labeled form — the <see cref="Editor"/>'s fields, grouped by <see cref="TabEditorField.Group"/>,
    /// prefilled from the row and saved as a whole — instead of a table with an Add button that never
    /// made sense for a singleton. Callers without the editor's permission see the values read-only.
    /// The endpoint still returns a one-element array, exactly as the table path expects.
    /// </summary>
    public bool Singleton { get; init; }

    /// <summary>Optional add/edit/delete affordances for the <see cref="DataEndpoint"/> table.</summary>
    public TabEditor? Editor { get; init; }

    /// <summary>Optional: render <see cref="DataEndpoint"/> rows as a time-series line chart instead of a table.</summary>
    public TabChart? Chart { get; init; }

    /// <summary>Optional tab-level command buttons (POST + refresh), permission-gated per action.</summary>
    public IReadOnlyList<TabAction> Actions { get; init; } = [];

    /// <summary>
    /// Optional per-row command buttons (POST to a <c>{field}</c>-templated URL + refresh),
    /// permission-gated per action.
    /// </summary>
    public IReadOnlyList<TabRowAction> RowActions { get; init; } = [];

    /// <summary>
    /// Optional drill-down: a GET endpoint with one <c>{field}</c> placeholder substituted from the
    /// row (e.g. <c>/api/legal/matters/{id}/detail</c>), returning a DETAIL DOCUMENT the shell
    /// renders generically. Gives list rows a composed detail view (a matter's
    /// parties/deadlines/tasks/documents in one page) with no custom UI.
    /// <para>The document's shape:</para>
    /// <code>
    /// {
    ///   title, subtitle?,
    ///   sections: [ { heading, tone?, text? }
    ///             | { heading, tone?, columns: [{field, header, masked?, linkTemplate?}], rows: [...] } ],
    ///   actions?: [ { id, label, endpoint, confirm? } ]
    /// }
    /// </code>
    /// <para>
    /// <c>tone</c> is optional and says what a section MEANS, so the shell can style severity
    /// instead of rendering a hard failure in the same grey as a data table: one of
    /// <c>"info"</c>, <c>"success"</c>, <c>"warning"</c>, <c>"danger"</c>. Omit it for ordinary
    /// content. An unrecognized value renders as if omitted — a typo degrades to neutral rather
    /// than breaking the page, which also means nothing will catch it for you, so spell it exactly.
    /// </para>
    /// <para>
    /// This document is deliberately untyped on the server: modules return anonymous objects and
    /// the shell is the only schema. Keep <c>heading</c> unique within a document — the shell keys
    /// sections by it.
    /// </para>
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
