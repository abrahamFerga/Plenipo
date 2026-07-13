namespace Plenipo.Application.Skills;

/// <summary>
/// Agent-skill configuration. Off by default; when enabled, <see cref="Path"/> points at the
/// deployment's skills directory (absolute, or relative to the application base directory with a
/// working-directory fallback).
/// </summary>
public sealed class SkillsOptions
{
    public const string SectionName = "Skills";

    public bool Enabled { get; set; }

    public string? Path { get; set; }

    /// <summary>Hard wall-clock cap on a skill script run.</summary>
    public int ScriptTimeoutSeconds { get; set; } = 60;
}
