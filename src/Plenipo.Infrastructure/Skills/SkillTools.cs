using System.ComponentModel;
using System.Text;
using Plenipo.Application.Skills;

namespace Plenipo.Infrastructure.Skills;

/// <summary>
/// The progressive-disclosure loop as platform tools. Keeping these in the normal tool pipeline —
/// instead of MAF's provider-injected functions — is deliberate: they get RBAC filtering
/// (<c>tools.skills.*</c>), tool-call audit, and the human-approval flow (scripts) exactly like
/// every other tool, and an approved script re-executes through the standard ApprovalExecutor.
/// </summary>
public sealed class SkillTools(ISkillCatalog catalog)
{
    [Description("Load a skill's full instructions by name. Call this when the user's request matches a skill in <available_skills>, then follow the returned instructions.")]
    public string LoadSkill(
        [Description("The skill name exactly as listed in <available_skills>.")] string name)
    {
        var instructions = catalog.GetInstructions(name);
        if (instructions is null)
        {
            return $"No skill named '{name}'. Available: {Available()}.";
        }

        return $"Skill '{name}' instructions:\n\n{instructions}";
    }

    [Description("Read a resource file bundled with a skill (lookup tables, policies, examples) by its relative path, as the skill's instructions direct.")]
    public string ReadSkillResource(
        [Description("The skill name.")] string skillName,
        [Description("The resource's relative path within the skill, e.g. 'references/table.md'.")] string resourcePath)
    {
        var content = catalog.ReadResource(skillName, resourcePath);
        return content
               ?? $"No resource '{resourcePath}' in skill '{skillName}' (paths are relative to the skill's own directory).";
    }

    [Description("Run a script bundled with a skill and return its output. Side-effecting and requires human approval.")]
    public async Task<string> RunSkillScript(
        [Description("The skill name.")] string skillName,
        [Description("The script's relative path within the skill, e.g. 'scripts/convert.py'.")] string scriptPath,
        [Description("Optional argument string passed to the script.")] string? arguments = null,
        CancellationToken cancellationToken = default)
    {
        return await catalog.RunScriptAsync(skillName, scriptPath, arguments, cancellationToken);
    }

    private string Available()
    {
        var names = catalog.List();
        if (names.Count == 0)
        {
            return "(none)";
        }

        var sb = new StringBuilder();
        foreach (var s in names)
        {
            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append(s.Name);
        }

        return sb.ToString();
    }
}
