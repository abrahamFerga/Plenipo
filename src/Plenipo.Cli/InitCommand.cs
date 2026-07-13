using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Plenipo.Cli;

/// <summary>
/// `plenipo init` — the installer wizard (OpenClaw's proven shape): a timeline of steps shown
/// upfront, every step skippable, re-runs are non-destructive (untouched settings survive), and
/// every prompt has a matching flag so CI can run the whole thing non-interactively. Writes
/// plenipo.settings.json next to the target host; secrets are never written — the wizard prints the
/// `dotnet user-secrets` commands instead.
/// </summary>
public sealed class InitCommand : Command<InitCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("--path <DIR>")]
        [Description("Host project directory to configure (where plenipo.settings.json lands).")]
        public string Path { get; init; } = ".";

        [CommandOption("--non-interactive")]
        [Description("No prompts: apply only the values given as flags (unset values stay untouched).")]
        public bool NonInteractive { get; init; }

        [CommandOption("--ai-provider <PROVIDER>")]
        [Description("Deployment default: Mock | AzureOpenAI (managed identity) | Ollama | None. OpenAI/Anthropic are tenant-admin settings.")]
        public string? AiProvider { get; init; }

        [CommandOption("--ai-model <MODEL>")]
        public string? AiModel { get; init; }

        [CommandOption("--ai-endpoint <URL>")]
        public string? AiEndpoint { get; init; }

        [CommandOption("--rag")]
        [Description("Enable the knowledge/RAG pipeline.")]
        public bool? Rag { get; init; }

        [CommandOption("--embedding-provider <PROVIDER>")]
        [Description("Mock | OpenAI | AzureOpenAI | Ollama")]
        public string? EmbeddingProvider { get; init; }

        [CommandOption("--embedding-model <MODEL>")]
        public string? EmbeddingModel { get; init; }

        [CommandOption("--documents <BOOL>")]
        [Description("Enable the platform document tools (default true in the platform).")]
        public bool? Documents { get; init; }

        [CommandOption("--whatsapp <BOOL>")]
        [Description("Enable the WhatsApp channel (secrets via user-secrets afterwards).")]
        public bool? WhatsApp { get; init; }

        [CommandOption("--files-provider <PROVIDER>")]
        [Description("Local | AzureBlob")]
        public string? FilesProvider { get; init; }

        [CommandOption("--auth-authority <URL>")]
        [Description("OIDC authority (Entra External ID / B2C). Empty keeps dev auth in Development.")]
        public string? AuthAuthority { get; init; }

        [CommandOption("--auth-audience <ID>")]
        public string? AuthAudience { get; init; }

        [CommandOption("--permission-source <SOURCE>")]
        [Description("Database (internal RBAC + token) | Token (external IdP only)")]
        public string? PermissionSource { get; init; }

        [CommandOption("--skills <BOOL>")]
        [Description("Enable agent skills (deploy-time SKILL.md bundles shipped with the host).")]
        public bool? Skills { get; init; }

        [CommandOption("--skills-path <DIR>")]
        [Description("Directory of skill bundles, relative to the host (default: skills).")]
        public string? SkillsPath { get; init; }

        [CommandOption("--secrets-provider <PROVIDER>")]
        [Description("Where runtime-entered secrets (connector keys, tokens) rest: DataProtection | AzureKeyVault")]
        public string? SecretsProvider { get; init; }

        [CommandOption("--keyvault-uri <URL>")]
        [Description("Key Vault URI (required when --secrets-provider AzureKeyVault).")]
        public string? KeyVaultUri { get; init; }
    }

    protected override int Execute(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var targetDirectory = System.IO.Path.GetFullPath(settings.Path);
        if (!Directory.Exists(targetDirectory))
        {
            AnsiConsole.MarkupLine($"[red]Directory not found:[/] {targetDirectory}");
            return 1;
        }

        var plan = settings.NonInteractive ? PlanFromFlags(settings) : RunWizard(settings);
        if (plan.AiProvider is "OpenAI" or "Anthropic")
        {
            AnsiConsole.MarkupLine(
                "[red]OpenAI and Anthropic are configured per tenant under Admin → AI Settings; " +
                "deployment API keys are not supported.[/]");
            return 1;
        }

        var file = System.IO.Path.Combine(targetDirectory, PlenipoSettingsFile.FileName);
        var existing = File.Exists(file) ? File.ReadAllText(file) : null;
        File.WriteAllText(file, PlenipoSettingsFile.Merge(existing, plan) + Environment.NewLine);

        AnsiConsole.MarkupLine($"[green]✓[/] Wrote [bold]{file}[/] (existing settings preserved).");
        PrintNextSteps(plan, targetDirectory);
        return 0;
    }

    private static SettingsPlan PlanFromFlags(Settings s) => new()
    {
        AiProvider = s.AiProvider,
        AiModel = s.AiModel,
        AiEndpoint = s.AiEndpoint,
        RagEnabled = s.Rag,
        EmbeddingProvider = s.EmbeddingProvider,
        EmbeddingModel = s.EmbeddingModel,
        DocumentsEnabled = s.Documents,
        WhatsAppEnabled = s.WhatsApp,
        FilesProvider = s.FilesProvider,
        AuthAuthority = s.AuthAuthority,
        AuthAudience = s.AuthAudience,
        PermissionSource = s.PermissionSource,
        SkillsEnabled = s.Skills,
        SkillsPath = s.SkillsPath,
        SecretsProvider = s.SecretsProvider,
        KeyVaultUri = s.KeyVaultUri,
    };

    private static SettingsPlan RunWizard(Settings s)
    {
        AnsiConsole.Write(new Rule("[bold]Plenipo setup[/]"));
        AnsiConsole.MarkupLine(
            """
            Steps: [bold]1[/] AI provider · [bold]2[/] Knowledge (RAG) · [bold]3[/] Document tools ·
                   [bold]4[/] Channels · [bold]5[/] File storage · [bold]6[/] Authentication ·
                   [bold]7[/] Skills · [bold]8[/] Secret storage
            Every step can keep the current value; commercial chat connections are configured per tenant in Admin → AI Settings.
            Connectors and modules are enabled per tenant at runtime (admin console → Integrations / Modules).
            """);

        var aiProvider = s.AiProvider ?? AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]1/8[/] AI provider (Mock needs no key and exercises the full pipeline)")
                .AddChoices("Mock", "AzureOpenAI", "Ollama", "(keep current)"));
        string? aiModel = null, aiEndpoint = null;
        if (aiProvider is "AzureOpenAI" or "Ollama")
        {
            aiModel = s.AiModel ?? AnsiConsole.Ask<string>("   Model / deployment name:");
            if (aiProvider is "AzureOpenAI" or "Ollama")
            {
                aiEndpoint = s.AiEndpoint ?? AnsiConsole.Ask<string>("   Endpoint URL:");
            }
        }

        var rag = s.Rag ?? AnsiConsole.Confirm("[bold]2/8[/] Enable the knowledge pipeline (index documents, search_knowledge)?", defaultValue: false);
        string? embeddingProvider = null;
        if (rag)
        {
            embeddingProvider = s.EmbeddingProvider ?? AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("   Embedding provider (Mock is deterministic and keyless)")
                    .AddChoices("Mock", "OpenAI", "AzureOpenAI", "Ollama"));
        }

        var documents = s.Documents ?? AnsiConsole.Confirm("[bold]3/8[/] Keep the platform document tools (read/generate PDFs) enabled?", defaultValue: true);
        var whatsapp = s.WhatsApp ?? AnsiConsole.Confirm("[bold]4/8[/] Enable the WhatsApp channel?", defaultValue: false);
        var filesProvider = s.FilesProvider ?? AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]5/8[/] File storage")
                .AddChoices("Local", "AzureBlob", "(keep current)"));

        string? authority = s.AuthAuthority, audience = s.AuthAudience, permissionSource = s.PermissionSource;
        if (AnsiConsole.Confirm("[bold]6/8[/] Configure an external identity provider (Entra External ID / B2C)?", defaultValue: false))
        {
            authority ??= AnsiConsole.Ask<string>("   OIDC authority URL:");
            audience ??= AnsiConsole.Ask<string>("   Audience (API client id):");
            permissionSource ??= AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("   Authorization source")
                    .AddChoices("Database", "Token"));
        }

        var skills = s.Skills ?? AnsiConsole.Confirm("[bold]7/8[/] Enable agent skills (deploy-time SKILL.md bundles shipped with the host)?", defaultValue: false);
        string? skillsPath = null;
        if (skills)
        {
            skillsPath = s.SkillsPath ?? AnsiConsole.Ask("   Skills directory (relative to the host):", "skills");
        }

        var secretsProvider = s.SecretsProvider ?? AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[bold]8/8[/] Runtime secret storage (connector keys, OAuth tokens — entered later in the admin UI)")
                .AddChoices("DataProtection", "AzureKeyVault", "(keep current)"));
        string? keyVaultUri = s.KeyVaultUri;
        if (secretsProvider == "AzureKeyVault")
        {
            keyVaultUri ??= AnsiConsole.Ask<string>("   Key Vault URI (https://<vault>.vault.azure.net/):");
        }

        return new SettingsPlan
        {
            AiProvider = aiProvider is "(keep current)" ? null : aiProvider,
            AiModel = aiModel,
            AiEndpoint = aiEndpoint,
            RagEnabled = rag,
            EmbeddingProvider = embeddingProvider,
            DocumentsEnabled = documents,
            WhatsAppEnabled = whatsapp,
            FilesProvider = filesProvider is "(keep current)" ? null : filesProvider,
            AuthAuthority = authority,
            AuthAudience = audience,
            PermissionSource = permissionSource,
            SkillsEnabled = skills,
            SkillsPath = skillsPath,
            SecretsProvider = secretsProvider is "(keep current)" ? null : secretsProvider,
            KeyVaultUri = keyVaultUri,
        };
    }

    private static void PrintNextSteps(SettingsPlan plan, string targetDirectory)
    {
        var steps = new List<string>();
        if (plan.WhatsAppEnabled == true)
        {
            steps.Add($"dotnet user-secrets --project \"{targetDirectory}\" set \"Channels:WhatsApp:AppSecret\" \"<meta app secret>\"");
            steps.Add($"dotnet user-secrets --project \"{targetDirectory}\" set \"Channels:WhatsApp:AccessToken\" \"<cloud api token>\"");
        }

        if (plan.FilesProvider == "AzureBlob")
        {
            steps.Add($"dotnet user-secrets --project \"{targetDirectory}\" set \"Files:AzureBlobConnectionString\" \"<connection string>\"");
        }

        if (steps.Count > 0)
        {
            AnsiConsole.MarkupLine("\n[bold]Secrets (never written to the file) — run:[/]");
            foreach (var step in steps)
            {
                AnsiConsole.WriteLine("  " + step);
            }
        }

        AnsiConsole.MarkupLine(
            """

            [bold]Next:[/] run the host, then in the admin console (/admin):
              · [bold]Modules[/] — enable this system's modules per tenant
              · [bold]Integrations[/] — enable data-source connectors (Azure Blob, local folder, Plenipo peer)
              · [bold]Roles[/] — tune what each role may do
              · [bold]AI Settings / Agent Profiles[/] — per-tenant provider/model/key and per-agent composition

            [bold]Not wizard-covered:[/] MCP tool servers are declared by hand in the "Mcp" section of
            plenipo.settings.json (see docs/CONFIGURATION.md → "Wiring MCP tool servers").
            """);
    }
}
