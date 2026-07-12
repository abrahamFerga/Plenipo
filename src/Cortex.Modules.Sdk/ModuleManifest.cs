namespace Cortex.Modules.Sdk;

/// <summary>
/// The manifest-first declaration of everything a domain module offers: its identity, the tools it
/// exposes to the agent, the dashboard tabs it contributes, the roles that unlock it, and the system
/// instructions that steer its chat. Declared statically so the platform can enumerate capabilities,
/// build the navigation, and enforce security without executing module code.
/// </summary>
public sealed record ModuleManifest
{
    /// <summary>Stable lowercase identifier, e.g. "finance", "legal", "nutrition".</summary>
    public required string Id { get; init; }

    /// <summary>Human-facing name shown in the module switcher.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Semantic version of the module.</summary>
    public required string Version { get; init; }

    public string? Description { get; init; }

    /// <summary>Optional icon name for the module switcher.</summary>
    public string? Icon { get; init; }

    /// <summary>Tools this module exposes to the agent (metadata; executables registered separately).</summary>
    public IReadOnlyList<ToolDescriptor> Tools { get; init; } = [];

    /// <summary>Dashboard tabs this module contributes.</summary>
    public IReadOnlyList<TabDescriptor> Tabs { get; init; } = [];

    /// <summary>
    /// Admin-console pages this module contributes, rendered by the admin app under the module's
    /// name — the same server-driven machinery as <see cref="Tabs"/> (data table, editor, chart,
    /// actions), surfaced at <c>/admin</c> instead of the domain shell. Every admin tab MUST
    /// declare a <see cref="TabDescriptor.Permission"/> (validated at startup): an admin surface
    /// is never visible by default. The admin shell navigates by module + tab id, so
    /// <see cref="TabDescriptor.Route"/> is informational here.
    /// </summary>
    public IReadOnlyList<TabDescriptor> AdminTabs { get; init; } = [];

    /// <summary>Optional first-run setup wizard the shell renders when the module has no data yet.</summary>
    public OnboardingDescriptor? Onboarding { get; init; }

    /// <summary>Roles that, when assigned to a user, grant access to this module.</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>
    /// Notification categories this module emits, so users get a per-category mute switch
    /// (no row = on; a mute suppresses in-app and channel delivery alike). Optional — an
    /// undeclared category still delivers with the default-on preference, it just offers no
    /// switch. Declare <c>"{Id}.approvals"</c> here to let users mute the platform's
    /// approval-pending notifications for this module.
    /// </summary>
    public IReadOnlyList<NotificationCategoryDescriptor> NotificationCategories { get; init; } = [];

    /// <summary>
    /// Recurring background work this module ships (daily digests, reminder sweeps). The platform
    /// enqueues each declared job once per cadence window for every tenant with the module
    /// enabled, executed by the <c>IJobHandler</c> registered for the descriptor's kind under a
    /// tenant-scoped system identity — see <see cref="RecurringJobDescriptor"/> for the identity
    /// and missed-window contract. Optional.
    /// </summary>
    public IReadOnlyList<RecurringJobDescriptor> RecurringJobs { get; init; } = [];

    /// <summary>
    /// System instructions injected into the agent when chatting within this module's context.
    /// Lets each domain steer tone, guardrails, and tool usage independently.
    /// </summary>
    public string? AgentInstructions { get; init; }

    /// <summary>
    /// A few example prompts the chat surfaces as one-click starters, so a newcomer can immediately
    /// exercise the module's tools (e.g. "Summarize my spending") without knowing what to type. Optional.
    /// </summary>
    public IReadOnlyList<string> SuggestedPrompts { get; init; } = [];

    /// <summary>
    /// Named agents this module ships (selectable in the chat's agent picker). The plain module
    /// assistant — <see cref="AgentInstructions"/> with every permitted tool — always exists;
    /// these specialize or retask it. Optional.
    /// </summary>
    public IReadOnlyList<AgentDescriptor> Agents { get; init; } = [];

    /// <summary>
    /// Named multi-agent workflows this module ships (sequential chains of its agents),
    /// selectable in the chat's picker alongside <see cref="Agents"/>. Optional.
    /// </summary>
    public IReadOnlyList<WorkflowDescriptor> Workflows { get; init; } = [];

    /// <summary>
    /// Optional directory of agent-skill bundles ({skill-name}/SKILL.md) this module ships,
    /// resolved like the global <c>Skills:Path</c> (relative to the app base). Module skills are
    /// advertised and slash-invocable ONLY in this module's chat; like all skills they are
    /// deploy-time content shipped with the host — never tenant uploads.
    /// </summary>
    public string? SkillsPath { get; init; }
}
