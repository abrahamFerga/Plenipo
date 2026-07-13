using System.Text;

namespace Plenipo.Application.Skills;

/// <summary>What the model sees during advertisement: enough to decide whether to load the skill.</summary>
public sealed record SkillSummary(string Name, string Description);

/// <summary>
/// The deployment's agent-skill library, following the MAF/agentskills.io progressive-disclosure
/// contract: skills advertise as name + description only; the agent loads instructions, reads
/// bundled resources, and runs bundled scripts on demand through the platform skill tools —
/// which ride Plenipo's normal RBAC, audit, and approval pipeline (scripts are approval-gated).
/// Skills are DEPLOY-TIME content (a directory shipped with the host), never tenant uploads:
/// scripts execute as unsandboxed subprocesses with the host's privileges.
/// </summary>
public interface ISkillCatalog
{
    public bool IsEnabled { get; }

    /// <summary>
    /// The skills visible in a module's chat: the global library plus that module's own
    /// (manifest <c>SkillsPath</c>) bundles. Null lists the global library only.
    /// </summary>
    public IReadOnlyList<SkillSummary> List(string? moduleId = null);

    /// <summary>The skill's full instruction body, or null when no such skill exists.</summary>
    public string? GetInstructions(string skillName);

    /// <summary>A bundled resource's text, or null when missing. Paths are confined to the skill's directory.</summary>
    public string? ReadResource(string skillName, string resourcePath);

    /// <summary>Runs a bundled script and returns its captured output. Paths are confined to the skill's directory.</summary>
    public Task<string> RunScriptAsync(string skillName, string scriptPath, string? arguments, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders the <c>&lt;available_skills&gt;</c> advertisement block appended to the agent's
/// instructions — the descriptions are what the model matches user intent against, exactly the
/// pattern MAF's own skills provider uses. Pure for testability.
/// </summary>
public static class SkillAdvertisement
{
    public static string Append(string instructions, IReadOnlyList<SkillSummary> skills)
    {
        if (skills.Count == 0)
        {
            return instructions;
        }

        var sb = new StringBuilder(instructions);
        sb.Append("\n\n<available_skills>\n");
        foreach (var skill in skills)
        {
            sb.Append("- ").Append(skill.Name).Append(": ").Append(skill.Description).Append('\n');
        }

        sb.Append("</available_skills>\n");
        sb.Append("When a user request matches an available skill, call load_skill with its name and follow " +
                  "the returned instructions; use read_skill_resource and run_skill_script as they direct.");
        return sb.ToString();
    }
}
