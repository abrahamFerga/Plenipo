using Plenipo.Core.Identity;
using Plenipo.Modules.Sdk;

namespace Plenipo.AspNetCore.Endpoints;

/// <summary>
/// One mapping from <see cref="TabDescriptor"/> to the wire shape the shells render — shared by
/// the domain surface (<c>/api/platform/modules</c>) and the admin extension surface
/// (<c>/api/admin/extensions</c>), so a tab behaves identically wherever it appears: tabs the
/// caller lacks permission for are omitted (not hidden), and editors/actions ship only to callers
/// who can actually use them.
/// </summary>
internal static class TabDtoMapper
{
    internal static TabDto[] MapTabs(IEnumerable<TabDescriptor> tabs, ICurrentUser user) =>
        tabs
            .Where(t => t.Permission is null || user.HasPermission(t.Permission))
            .OrderBy(t => t.Order)
            .Select(t => new TabDto(
                t.Id, t.Label, t.Route, t.Icon, t.Home, t.DataEndpoint,
                t.Columns.Select(c => new TabColumnDto(c.Field, c.Header, c.Masked, c.LinkTemplate)).ToArray(),
                t.Placeholder,
                // The editor ships only to callers holding its permission, so the payload never
                // advertises affordances the user can't use (the endpoints stay gated regardless).
                t.Editor is { } e && user.HasPermission(e.Permission)
                    ? new TabEditorDto(
                        e.UpsertEndpoint, e.DeleteEndpoint, e.KeyField,
                        e.Fields.Select(ToFieldDto).ToArray())
                    : null,
                t.DetailEndpoint,
                t.Chart is { } chart
                    ? new TabChartDto(ToKindString(chart.Kind), chart.XField, chart.YField, chart.SeriesField, chart.YLabel)
                    : null,
                // Same rule as the editor: only advertise actions the caller can actually invoke.
                t.Actions
                    .Where(a => a.Permission is null || user.HasPermission(a.Permission))
                    .Select(a => new TabActionDto(a.Id, a.Label, a.Endpoint, a.Confirm))
                    .ToArray(),
                t.RowActions
                    .Where(a => a.Permission is null || user.HasPermission(a.Permission))
                    .Select(a => new TabRowActionDto(a.Id, a.Label, a.EndpointTemplate, a.Confirm))
                    .ToArray(),
                t.Singleton))
            .ToArray();

    internal static TabEditorFieldDto ToFieldDto(TabEditorField f) => new(
        f.Field, f.Label, f.Multiline, f.Required, f.Numeric,
        f.Options?.Select(o => new TabEditorOptionDto(o.Value, o.Label)).ToArray(),
        f.OptionsEndpoint, f.OptionsField, f.Masked, f.Default, f.DefaultFrom, f.Group);

    // The wire keeps the kind a lowercase string literal (the shell switches on it), not an
    // enum ordinal — adding a kind must never renumber what existing clients see.
    private static string ToKindString(TabChartKind kind) => kind switch
    {
        TabChartKind.Donut => "donut",
        TabChartKind.Bar => "bar",
        _ => "line",
    };
}

internal sealed record TabDto(
    string Id, string Label, string Route, string? Icon, bool Home, string? DataEndpoint, TabColumnDto[] Columns, string? Placeholder,
    TabEditorDto? Editor, string? DetailEndpoint, TabChartDto? Chart, TabActionDto[] Actions, TabRowActionDto[] RowActions,
    bool Singleton);

internal sealed record TabChartDto(string Kind, string XField, string YField, string? SeriesField, string? YLabel);

internal sealed record TabActionDto(string Id, string Label, string Endpoint, string? Confirm);

internal sealed record TabRowActionDto(string Id, string Label, string EndpointTemplate, string? Confirm);

// LinkTemplate ships with its {field} placeholders UNRESOLVED, like a row action's endpoint: the
// shell substitutes from the row it is rendering, because the server has no row here.
internal sealed record TabColumnDto(string Field, string Header, bool Masked, string? LinkTemplate);

internal sealed record TabEditorDto(string UpsertEndpoint, string? DeleteEndpoint, string? KeyField, TabEditorFieldDto[] Fields);

internal sealed record TabEditorOptionDto(string Value, string Label);

internal sealed record TabEditorFieldDto(
    string Field, string Label, bool Multiline, bool Required, bool Numeric,
    TabEditorOptionDto[]? Options, string? OptionsEndpoint, string? OptionsField, bool Masked,
    string? Default, string? DefaultFrom, string? Group);
