using System.Text.Json;
using Cortex.Cli;
using Xunit;

namespace Cortex.Cli.Tests;

/// <summary>
/// The wizard's one side effect is this merge — so it gets the tests: decided keys land in the
/// right sections, undecided keys touch nothing, and re-running is non-destructive (foreign
/// sections and previous choices survive verbatim).
/// </summary>
public sealed class CortexSettingsFileTests
{
    [Fact]
    public void Writes_decided_settings_into_the_right_sections()
    {
        var json = CortexSettingsFile.Merge(null, new SettingsPlan
        {
            AiProvider = "OpenAI",
            AiModel = "gpt-4o-mini",
            RagEnabled = true,
            EmbeddingProvider = "OpenAI",
            WhatsAppEnabled = true,
            FilesProvider = "AzureBlob",
            AuthAuthority = "https://contoso.ciamlogin.com/tid/v2.0",
            PermissionSource = "Token",
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("OpenAI", root.GetProperty("Ai").GetProperty("Provider").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("Ai").GetProperty("Model").GetString());
        Assert.True(root.GetProperty("Rag").GetProperty("Enabled").GetBoolean());
        Assert.True(root.GetProperty("Channels").GetProperty("WhatsApp").GetProperty("Enabled").GetBoolean());
        Assert.Equal("AzureBlob", root.GetProperty("Files").GetProperty("Provider").GetString());
        Assert.Equal("Token", root.GetProperty("Auth").GetProperty("PermissionSource").GetString());
    }

    [Fact]
    public void Undecided_settings_leave_the_file_untouched()
    {
        var json = CortexSettingsFile.Merge(null, new SettingsPlan { AiProvider = "Mock" });

        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.TryGetProperty("Rag", out _));
        Assert.False(doc.RootElement.TryGetProperty("Auth", out _));
        Assert.False(doc.RootElement.TryGetProperty("Channels", out _));
    }

    [Fact]
    public void Rerunning_preserves_previous_choices_and_foreign_sections()
    {
        var first = CortexSettingsFile.Merge(null, new SettingsPlan { AiProvider = "OpenAI", AiModel = "gpt-4o-mini", RagEnabled = true });

        // Simulate a hand-edit the wizard knows nothing about.
        var edited = first.Replace("}", """ ,"Cors": { "Origins": ["https://app.contoso.com"] } }""", StringComparison.Ordinal);
        edited = CortexSettingsFile.Merge(edited, new SettingsPlan { WhatsAppEnabled = true });

        using var doc = JsonDocument.Parse(edited);
        var root = doc.RootElement;
        // Previous choices survive...
        Assert.Equal("OpenAI", root.GetProperty("Ai").GetProperty("Provider").GetString());
        Assert.Equal("gpt-4o-mini", root.GetProperty("Ai").GetProperty("Model").GetString());
        Assert.True(root.GetProperty("Rag").GetProperty("Enabled").GetBoolean());
        // ...the foreign section survives...
        Assert.Equal("https://app.contoso.com", root.GetProperty("Cors").GetProperty("Origins")[0].GetString());
        // ...and the new decision lands.
        Assert.True(root.GetProperty("Channels").GetProperty("WhatsApp").GetProperty("Enabled").GetBoolean());
    }

    [Fact]
    public void Writes_skills_and_secret_storage_sections()
    {
        var json = CortexSettingsFile.Merge(null, new SettingsPlan
        {
            SkillsEnabled = true,
            SkillsPath = "skills",
            SecretsProvider = "AzureKeyVault",
            KeyVaultUri = "https://contoso-vault.vault.azure.net/",
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("Skills").GetProperty("Enabled").GetBoolean());
        Assert.Equal("skills", root.GetProperty("Skills").GetProperty("Path").GetString());
        Assert.Equal("AzureKeyVault", root.GetProperty("Secrets").GetProperty("Provider").GetString());
        Assert.Equal("https://contoso-vault.vault.azure.net/", root.GetProperty("Secrets").GetProperty("KeyVaultUri").GetString());
    }

    [Fact]
    public void Merging_into_a_partial_section_keeps_its_other_keys()
    {
        var existing = """{ "Ai": { "Provider": "Ollama", "Endpoint": "http://localhost:11434/v1" } }""";
        var json = CortexSettingsFile.Merge(existing, new SettingsPlan { AiModel = "llama3.1" });

        using var doc = JsonDocument.Parse(json);
        var ai = doc.RootElement.GetProperty("Ai");
        Assert.Equal("Ollama", ai.GetProperty("Provider").GetString());
        Assert.Equal("http://localhost:11434/v1", ai.GetProperty("Endpoint").GetString());
        Assert.Equal("llama3.1", ai.GetProperty("Model").GetString());
    }
}
