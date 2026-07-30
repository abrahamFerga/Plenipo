using Plenipo.Application.Authorization;
using Xunit;

namespace Plenipo.Application.Tests.Authorization;

/// <summary>
/// The deviation expansion itself: effective = (baseline ∖ suppressed) ∪ granted, plus the role-exists
/// test that replaces "does this role have any rows" — which stopped being an existence test the moment a
/// declared role legitimately had none.
/// </summary>
public sealed class RoleGrantsTests
{
    private static Dictionary<string, IReadOnlyList<string>> Rows(params (string Role, string[] Permissions)[] rows) =>
        rows.ToDictionary(r => r.Role, r => (IReadOnlyList<string>)r.Permissions, StringComparer.Ordinal);

    private static readonly Dictionary<string, IReadOnlyList<string>> NoRows = new(StringComparer.Ordinal);

    private static readonly Dictionary<string, string[]> Baseline = new(StringComparer.Ordinal)
    {
        ["user"] = ["chat.use", "chat.conversations.view"],
        ["paralegal"] = ["chat.use", "legal.matters.view"],
    };

    [Fact]
    public void NoDeviations_IsExactlyTheBaseline()
    {
        var effective = RoleGrants.Effective("user", Baseline, NoRows, NoRows);

        Assert.Equal(["chat.conversations.view", "chat.use"], effective.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void SuppressionRemoves_GrantAdds()
    {
        var effective = RoleGrants.Effective(
            "user",
            Baseline,
            Rows(("user", ["chat.approvals.manage"])),
            Rows(("user", ["chat.conversations.view"])));

        Assert.Equal(
            ["chat.approvals.manage", "chat.use"],
            effective.OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact]
    public void CustomRole_WithNoBaseline_GrantsExactlyItsRows()
    {
        var effective = RoleGrants.Effective("auditor", Baseline, Rows(("auditor", ["platform.audit.view"])), NoRows);

        Assert.Equal(["platform.audit.view"], effective);
    }

    [Fact]
    public void AnExplicitGrant_WinsOverASuppressionOfTheSamePermission()
    {
        // Endpoints never write both (the diff makes them disjoint), so this pins the reading for
        // hand-edited or partially-migrated data: the explicit grant is the more recent intent.
        var effective = RoleGrants.Effective(
            "user", Baseline, Rows(("user", ["chat.use"])), Rows(("user", ["chat.use"])));

        Assert.Contains("chat.use", effective);
    }

    [Fact]
    public void IsKnown_AcceptsADeclaredProductRoleWithNoRows()
    {
        // The regression the endpoints would otherwise hit: a paralegal role declared by the host has no
        // rows at all, so a row-existence check would reject it as "Unknown role".
        Assert.True(RoleGrants.IsKnown("paralegal", Baseline, []));
    }

    [Fact]
    public void IsKnown_AcceptsABuiltInAndACustomRole_RejectsAnInvention()
    {
        Assert.True(RoleGrants.IsKnown(Roles.TenantAdmin, Baseline, []));
        Assert.True(RoleGrants.IsKnown("auditor", Baseline, ["auditor"]));
        Assert.False(RoleGrants.IsKnown("not_a_role", Baseline, ["auditor"]));
    }
}
