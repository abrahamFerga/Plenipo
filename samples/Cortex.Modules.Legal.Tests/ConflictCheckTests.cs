namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The conflicts matcher is recall-biased by design: it must surface candidates for a human to
/// clear, never quietly miss one — but without letting trivial fragments hit everything.
/// </summary>
public sealed class ConflictCheckTests
{
    [Theory]
    [InlineData("Initech", "Initech Corporation", true)] // query inside party
    [InlineData("Run a conflict check for Initech Corporation", "Initech Corporation", true)] // party inside sentence
    [InlineData("INITECH CORPORATION", "initech corporation", true)] // case-insensitive
    [InlineData("Globex", "Initech Corporation", false)]
    [InlineData("Al", "Al's Diner LLC", false)] // below minimum length: never matches
    [InlineData("Jane Doe; Initech", "Initech", true)] // multi-name input still hits
    public void Matches_is_loose_but_not_trivial(string query, string party, bool expected)
    {
        Assert.Equal(expected, ConflictCheck.Matches(query, party));
    }
}
