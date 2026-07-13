using Plenipo.Application.Ai;
using Plenipo.Core.Platform;

namespace Plenipo.Infrastructure.Tests;

public sealed class InstructionComposerTests
{
    private const string SystemPrompt = "You are Plenipo.";
    private const string ManifestInstructions = "You are the legal assistant.";

    private static AgentProfile Profile(AgentProfileMode mode, string instructions = "Speak like a litigator.") => new()
    {
        TenantId = Guid.NewGuid(),
        ModuleId = "legal",
        Name = "test",
        Instructions = instructions,
        Mode = mode,
    };

    [Fact]
    public void NoProfile_KeepsSystemPromptPlusManifest()
    {
        Assert.Equal(
            $"{SystemPrompt}\n\n{ManifestInstructions}",
            InstructionComposer.Compose(SystemPrompt, ManifestInstructions, profile: null));
    }

    [Fact]
    public void NoProfile_NoManifestInstructions_IsJustTheSystemPrompt()
    {
        Assert.Equal(SystemPrompt, InstructionComposer.Compose(SystemPrompt, null, profile: null));
        Assert.Equal(SystemPrompt, InstructionComposer.Compose(SystemPrompt, "  ", profile: null));
    }

    [Fact]
    public void AppendProfile_LayersAfterManifest_SoTheSpecializationWinsConflicts()
    {
        var composed = InstructionComposer.Compose(SystemPrompt, ManifestInstructions, Profile(AgentProfileMode.Append));

        Assert.Equal($"{SystemPrompt}\n\n{ManifestInstructions}\n\nSpeak like a litigator.", composed);
    }

    [Fact]
    public void ReplaceProfile_DropsManifestInstructions_ButNeverTheSystemPrompt()
    {
        var composed = InstructionComposer.Compose(SystemPrompt, ManifestInstructions, Profile(AgentProfileMode.Replace));

        Assert.Equal($"{SystemPrompt}\n\nSpeak like a litigator.", composed);
        Assert.DoesNotContain(ManifestInstructions, composed, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendProfile_WithNoManifestInstructions_StillApplies()
    {
        var composed = InstructionComposer.Compose(SystemPrompt, null, Profile(AgentProfileMode.Append));

        Assert.Equal($"{SystemPrompt}\n\nSpeak like a litigator.", composed);
    }
}
