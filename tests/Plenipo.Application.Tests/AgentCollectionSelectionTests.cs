using Plenipo.Application.Ai;

namespace Plenipo.Application.Tests;

/// <summary>
/// An agent's knowledge scope decides which corpora its retrieval can reach. It is applied on top of
/// the collection gates, so a pattern can only ever remove a collection the caller already had —
/// these tests pin the matching semantics that make "the Spanish employment-law assistant" a policy
/// rather than a prompt instruction.
/// </summary>
public sealed class AgentCollectionSelectionTests
{
    [Fact]
    public void No_selection_allows_everything()
    {
        Assert.True(AgentCollectionSelection.AllowsAll(null));
        Assert.True(AgentCollectionSelection.AllowsAll([]));
        Assert.True(AgentCollectionSelection.Matches(null, "legal", "matter", "Acme diligence"));
        Assert.True(AgentCollectionSelection.Matches([], "legal", "matter", "Acme diligence"));
    }

    [Fact]
    public void Path_uses_a_placeholder_so_unbound_collections_keep_three_segments()
    {
        Assert.Equal("legal/matter/Acme diligence", AgentCollectionSelection.Path("legal", "matter", "Acme diligence"));
        Assert.Equal("knowledge/-/EU statutes", AgentCollectionSelection.Path("knowledge", null, "EU statutes"));
        Assert.Equal("knowledge/-/EU statutes", AgentCollectionSelection.Path("knowledge", "", "EU statutes"));
    }

    [Theory]
    // Whole-module scope.
    [InlineData("legal/*", "legal", "matter", "Acme diligence", true)]
    [InlineData("legal/*", "finance", "invoice", "Acme", false)]
    // Every resource-bound collection of one type, in any module.
    [InlineData("*/matter/*", "legal", "matter", "Acme diligence", true)]
    [InlineData("*/matter/*", "legal", null, "Playbooks", false)]
    // One named library, unbound.
    [InlineData("knowledge/-/ES employment law", "knowledge", null, "ES employment law", true)]
    [InlineData("knowledge/-/ES employment law", "knowledge", null, "DE employment law", false)]
    // Prefix matching on names, which is how curated libraries get grouped.
    [InlineData("knowledge/-/ES *", "knowledge", null, "ES employment law", true)]
    [InlineData("knowledge/-/ES *", "knowledge", null, "FR employment law", false)]
    // A bare wildcard is the same as no selection.
    [InlineData("*", "anything", "at", "all", true)]
    public void Patterns_match_the_canonical_path(
        string pattern, string moduleId, string? resourceType, string name, bool expected)
    {
        Assert.Equal(expected, AgentCollectionSelection.Matches([pattern], moduleId, resourceType, name));
    }

    [Fact]
    public void Any_pattern_matching_admits_the_collection()
    {
        string[] scopes = ["legal/matter/*", "knowledge/-/ES employment law"];

        Assert.True(AgentCollectionSelection.Matches(scopes, "legal", "matter", "Acme diligence"));
        Assert.True(AgentCollectionSelection.Matches(scopes, "knowledge", null, "ES employment law"));
        Assert.False(AgentCollectionSelection.Matches(scopes, "knowledge", null, "DE employment law"));
        Assert.False(AgentCollectionSelection.Matches(scopes, "finance", "ledger", "2026"));
    }

    [Fact]
    public void Matching_is_case_insensitive_because_collection_names_are_user_typed()
    {
        Assert.True(AgentCollectionSelection.Matches(["legal/matter/acme diligence"], "legal", "matter", "Acme Diligence"));
        Assert.True(AgentCollectionSelection.Matches(["LEGAL/*"], "legal", "matter", "Acme"));
    }

    [Fact]
    public void Blank_and_whitespace_patterns_are_ignored_rather_than_matching_everything()
    {
        // A stray blank line in the admin textarea must not silently widen the scope.
        Assert.False(AgentCollectionSelection.Matches(["", "   ", "legal/*"], "finance", "invoice", "Acme"));
        Assert.True(AgentCollectionSelection.Matches(["", "legal/*"], "legal", "matter", "Acme"));
    }

    [Fact]
    public void Consecutive_wildcards_do_not_blow_up()
    {
        // The matcher backtracks iteratively; a pathological pattern must stay linear-ish rather
        // than catastrophically backtracking the way a naive regex translation would.
        var pattern = new string('*', 40) + "z";
        Assert.False(AgentCollectionSelection.MatchesPath([pattern], new string('a', 400)));
        Assert.True(AgentCollectionSelection.MatchesPath([pattern], new string('a', 400) + "z"));
    }
}
