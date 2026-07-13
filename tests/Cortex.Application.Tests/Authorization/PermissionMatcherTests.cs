using Cortex.Application.Authorization;

namespace Cortex.Application.Tests.Authorization;

public sealed class PermissionMatcherTests
{
    private static IReadOnlySet<string> Granted(params string[] permissions) =>
        new HashSet<string>(permissions, StringComparer.Ordinal);

    [Fact]
    public void ExactGrant_IsGranted()
    {
        var granted = Granted("tools.finance.categorize_transaction");
        Assert.True(PermissionMatcher.IsGranted(granted, "tools.finance.categorize_transaction"));
    }

    [Fact]
    public void MissingPermission_IsDenied()
    {
        var granted = Granted("tools.finance.categorize_transaction");
        Assert.False(PermissionMatcher.IsGranted(granted, "tools.legal.draft_contract"));
    }

    [Fact]
    public void GlobalWildcard_GrantsEverything()
    {
        var granted = Granted("*");
        Assert.True(PermissionMatcher.IsGranted(granted, "tools.legal.draft_contract"));
        Assert.True(PermissionMatcher.IsGranted(granted, "platform.users.manage"));
    }

    [Fact]
    public void ModuleWildcard_GrantsAllToolsInThatModule()
    {
        var granted = Granted("tools.finance.*");
        Assert.True(PermissionMatcher.IsGranted(granted, "tools.finance.categorize_transaction"));
        Assert.True(PermissionMatcher.IsGranted(granted, "tools.finance.summarize_spending"));
    }

    [Fact]
    public void ModuleWildcard_DoesNotLeakAcrossModules()
    {
        var granted = Granted("tools.finance.*");
        Assert.False(PermissionMatcher.IsGranted(granted, "tools.legal.draft_contract"));
    }

    [Theory]
    [InlineData("platform.*", "platform.users.manage", true)]
    [InlineData("platform.*", "platform.tenants.manage", true)]
    [InlineData("platform.users.*", "platform.users.manage", true)]
    [InlineData("platform.users.*", "platform.tenants.manage", false)]
    public void HierarchicalWildcards_WalkUpTheDottedPath(string grant, string required, bool expected)
    {
        Assert.Equal(expected, PermissionMatcher.IsGranted(Granted(grant), required));
    }

    [Fact]
    public void EmptyGrants_DenyEverything()
    {
        Assert.False(PermissionMatcher.IsGranted(Granted(), "chat.use"));
    }

    // Security-critical negatives — a wildcard is honoured ONLY at a segment boundary (a star that follows
    // a dot). These mirror @abrahamferga/cortex-ui's hasPermission tests so the enforcement and its client mirror can't
    // drift into a privilege-escalation bug.

    [Fact]
    public void PrefixWithoutWildcard_IsNotAGrant()
    {
        // Holding "tools.finance" must NOT grant "tools.finance.categorize" — only "tools.finance.*" would.
        Assert.False(PermissionMatcher.IsGranted(Granted("tools.finance"), "tools.finance.categorize"));
    }

    [Fact]
    public void StarGluedToAPartialToken_IsNotAWildcard()
    {
        // The star must follow a dot; "tools.finance*" is not a wildcard for "tools.finance.categorize".
        Assert.False(PermissionMatcher.IsGranted(Granted("tools.finance*"), "tools.finance.categorize"));
    }

    [Fact]
    public void ToolPermissionConvention_IsStable_AndItsModuleWildcardGrantsExactlyThatModule()
    {
        // The tool-permission format and its module wildcard are coupled: the whole tool-authorization
        // system relies on "tools.<module>.*" covering "tools.<module>.<tool>" (and nothing else).
        var financeTool = Permissions.ForTool("finance", "categorize_transaction");
        Assert.Equal("tools.finance.categorize_transaction", financeTool);
        Assert.Equal("tools.finance.*", Permissions.AllToolsFor("finance"));

        var financeWildcard = Granted(Permissions.AllToolsFor("finance"));
        Assert.True(PermissionMatcher.IsGranted(financeWildcard, financeTool));
        Assert.False(PermissionMatcher.IsGranted(financeWildcard, Permissions.ForTool("legal", "draft_contract")));
    }
}
