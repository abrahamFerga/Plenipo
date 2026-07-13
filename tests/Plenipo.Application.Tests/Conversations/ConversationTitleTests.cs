using Plenipo.Application.Conversations;

namespace Plenipo.Application.Tests.Conversations;

public sealed class ConversationTitleTests
{
    [Fact]
    public void ShortMessage_IsUsedVerbatim_AfterTrimming()
    {
        Assert.Equal("How much did I spend?", ConversationTitle.Derive("  How much did I spend?  "));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyOrWhitespace_FallsBackToADefault(string? message)
    {
        Assert.Equal(ConversationTitle.Fallback, ConversationTitle.Derive(message));
    }

    [Fact]
    public void ExactlyMaxLength_IsNotTruncated()
    {
        var message = new string('a', ConversationTitle.MaxLength);
        Assert.Equal(message, ConversationTitle.Derive(message));
    }

    [Fact]
    public void LongerThanMax_IsTruncatedToMaxWithEllipsis()
    {
        var message = new string('a', ConversationTitle.MaxLength + 40);
        var title = ConversationTitle.Derive(message);

        Assert.Equal(ConversationTitle.MaxLength, title.Length);
        Assert.EndsWith("...", title);
        Assert.Equal(new string('a', ConversationTitle.MaxLength - 3), title[..^3]);
    }
}
