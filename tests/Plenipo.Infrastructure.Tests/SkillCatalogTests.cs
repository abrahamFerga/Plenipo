using Plenipo.Application.Modules;
using Plenipo.Application.Skills;
using Plenipo.Infrastructure.Skills;
using Plenipo.Modules.Sdk;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Plenipo.Infrastructure.Tests;

public sealed class SkillCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("plenipo-skills-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private FileSkillCatalog NewCatalog(params ModuleManifest[] modules) => new(
        Options.Create(new SkillsOptions { Enabled = true, Path = _root }),
        new StubModuleCatalog(modules),
        NullLogger<FileSkillCatalog>.Instance);

    private sealed class StubModuleCatalog(ModuleManifest[] manifests) : IModuleCatalog
    {
        public IReadOnlyList<ModuleManifest> Manifests => manifests;

        public bool TryGetManifest(string moduleId, out ModuleManifest? manifest)
        {
            manifest = manifests.FirstOrDefault(m => m.Id == moduleId);
            return manifest is not null;
        }
    }

    private void WriteSkill(string name, string frontmatterName, string description = "Does a thing. Use when testing.")
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(dir, "references"));
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"""
            ---
            name: {frontmatterName}
            description: {description}
            ---

            ## Usage

            1. Read `references/table.md`.
            """);
        File.WriteAllText(Path.Combine(dir, "references", "table.md"), "col-a | col-b");
    }

    [Fact]
    public void List_ReturnsNameAndDescription_FromFrontmatter()
    {
        WriteSkill("unit-converter", "unit-converter", "Convert units.");
        var catalog = NewCatalog();

        var skills = catalog.List();

        Assert.True(catalog.IsEnabled);
        var skill = Assert.Single(skills);
        Assert.Equal("unit-converter", skill.Name);
        Assert.Equal("Convert units.", skill.Description);
    }

    [Fact]
    public void Skill_WithMismatchedFrontmatterName_IsRejected()
    {
        // The agentskills.io contract: frontmatter name must match the directory name.
        WriteSkill("unit-converter", "different-name");

        Assert.Empty(NewCatalog().List());
    }

    [Fact]
    public void GetInstructions_ReturnsTheBodyAfterFrontmatter()
    {
        WriteSkill("unit-converter", "unit-converter");

        var instructions = NewCatalog().GetInstructions("unit-converter");

        Assert.NotNull(instructions);
        Assert.StartsWith("## Usage", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("---", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadResource_ReadsWithinTheSkill_AndRejectsTraversal()
    {
        WriteSkill("unit-converter", "unit-converter");
        File.WriteAllText(Path.Combine(_root, "outside.txt"), "must never be readable");
        var catalog = NewCatalog();

        Assert.Equal("col-a | col-b", catalog.ReadResource("unit-converter", "references/table.md"));
        Assert.Null(catalog.ReadResource("unit-converter", "../outside.txt"));
        Assert.Null(catalog.ReadResource("unit-converter", "..\\outside.txt"));
    }

    [Fact]
    public void ParseSkillFile_WithoutFrontmatter_YieldsNoName()
    {
        var (name, description, _) = FileSkillCatalog.ParseSkillFile("just a body, no frontmatter");

        Assert.Null(name);
        Assert.Null(description);
    }

    [Fact]
    public void Advertisement_ListsEverySkill_AndTheLoadInstruction()
    {
        var text = SkillAdvertisement.Append("BASE", [new SkillSummary("a-skill", "Does A."), new SkillSummary("b-skill", "Does B.")]);

        Assert.StartsWith("BASE", text, StringComparison.Ordinal);
        Assert.Contains("<available_skills>", text, StringComparison.Ordinal);
        Assert.Contains("- a-skill: Does A.", text, StringComparison.Ordinal);
        Assert.Contains("- b-skill: Does B.", text, StringComparison.Ordinal);
        Assert.Contains("load_skill", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Advertisement_NoSkills_LeavesInstructionsUntouched()
    {
        Assert.Equal("BASE", SkillAdvertisement.Append("BASE", []));
    }
}
