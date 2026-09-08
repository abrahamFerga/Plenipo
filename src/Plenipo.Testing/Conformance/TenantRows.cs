using System.Text.Json;

namespace Plenipo.Testing.Conformance;

/// <summary>
/// How the tenancy pack reads a product's list responses: the rows of a JSON array, or of the first
/// array-valued <c>items</c> / <c>rows</c> / <c>data</c> / <c>results</c> property of an object, and
/// the <c>id</c> of each row. The invariant the pack holds is "a second tenant sees none of the first
/// tenant's rows"; for a surface in <see cref="ProductContract.SeededReadEndpoints"/> that is decided
/// by comparing ids, for every other surface by the second tenant seeing no rows at all. Public so a
/// product can unit-test the shape the pack will see.
/// </summary>
public static class TenantRows
{
    private static readonly string[] RowProperties = ["items", "rows", "data", "results"];

    /// <summary>The rows of a list response, or none when the body is not a list shape the pack reads.</summary>
    public static IReadOnlyList<JsonElement> Rows(JsonElement root)
    {
        switch (root.ValueKind)
        {
            case JsonValueKind.Array:
                return root.EnumerateArray().ToArray();
            case JsonValueKind.Object:
                foreach (var name in RowProperties)
                {
                    if (root.TryGetProperty(name, out var rows) && rows.ValueKind == JsonValueKind.Array)
                    {
                        return rows.EnumerateArray().ToArray();
                    }
                }

                return [];
            default:
                return [];
        }
    }

    /// <summary>The <c>id</c> of a row as text (a string or a number), or null when the row carries none.</summary>
    public static string? IdOf(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("id", out var id))
        {
            return null;
        }

        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }

    /// <summary>
    /// The ids present in both responses — rows the second tenant was shown that belong to the first.
    /// Empty when the second tenant's rows are all its own (or carry no ids, which
    /// <see cref="PlenipoTenancyConformance{TProgram}"/> rejects separately).
    /// </summary>
    public static IReadOnlyList<string> SharedIds(JsonElement first, JsonElement second)
    {
        var firstIds = Rows(first).Select(IdOf).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return Rows(second).Select(IdOf).OfType<string>()
            .Where(firstIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
