using Cortex.Application.Ai;
using Xunit;

namespace Cortex.Application.Tests;

/// <summary>
/// The profile tool-selection filter feeds the runner's pre-model tool gate, so its semantics are
/// pinned here: absent/empty selections allow everything, exact names match ordinally, and only a
/// TRAILING '*' is a wildcard.
/// </summary>
public sealed class AgentToolSelectionTests
{
    [Fact]
    public void Null_or_empty_selection_allows_every_tool()
    {
        Assert.True(AgentToolSelection.AllowsAll(null));
        Assert.True(AgentToolSelection.AllowsAll([]));
        Assert.True(AgentToolSelection.Matches(null, "list_matters"));
        Assert.True(AgentToolSelection.Matches([], "list_matters"));
    }

    [Theory]
    [InlineData("list_matters", "list_matters", true)]
    [InlineData("list_matters", "list_matter", false)]
    [InlineData("list_matters", "LIST_MATTERS", false)] // ordinal, tool names are canonical
    [InlineData("ask_*", "ask_finance", true)]
    [InlineData("ask_*", "ask_", true)]
    [InlineData("ask_*", "task_finance", false)]
    [InlineData("*", "anything", true)]
    public void Patterns_match_exact_names_and_trailing_wildcards(string pattern, string tool, bool expected)
    {
        Assert.Equal(expected, AgentToolSelection.Matches([pattern], tool));
    }

    [Fact]
    public void Any_pattern_in_the_list_suffices()
    {
        string[] patterns = ["load_skill", "ask_*"];
        Assert.True(AgentToolSelection.Matches(patterns, "load_skill"));
        Assert.True(AgentToolSelection.Matches(patterns, "ask_legal"));
        Assert.False(AgentToolSelection.Matches(patterns, "run_skill_script"));
    }

    [Fact]
    public void A_wildcard_only_matches_as_prefix_not_infix()
    {
        // '*' anywhere but the end is treated as a literal character — no match for real names.
        Assert.False(AgentToolSelection.Matches(["a*k_finance"], "ask_finance"));
    }
}
