using Plenipo.Core.Entities;

namespace Plenipo.Modules.Legal.Persistence;

/// <summary>
/// A tenant's contract clause — the editable library the agent searches and drafts from. Seeded from
/// <see cref="LegalCatalog"/> defaults on first run; a firm curates it from there (management
/// endpoints), so drafting reflects the firm's own precedent, not a hardcoded sample.
/// </summary>
public sealed class TenantClause : TenantEntityBase
{
    /// <summary>Stable, URL-safe identifier within the tenant (e.g. "confidentiality").</summary>
    public required string Slug { get; set; }

    public required string Title { get; set; }
    public required string Category { get; set; }
    public required string Summary { get; set; }

    /// <summary>The clause text with {PartyA} / {PartyB} placeholders.</summary>
    public required string Template { get; set; }
}

public enum RuleSeverity
{
    Info = 0,
    Caution = 1,
    Critical = 2,
}

/// <summary>
/// A firm playbook rule — reviewer guidance the agent applies when reading contracts ("flag
/// uncapped liability", "never accept unilateral termination"). The playbook is what turns generic
/// document reading into firm-standard contract review.
/// </summary>
public sealed class PlaybookRule : TenantEntityBase
{
    public required string Title { get; set; }

    /// <summary>What to check and what to flag, written for the reviewing agent.</summary>
    public required string Guidance { get; set; }

    public RuleSeverity Severity { get; set; } = RuleSeverity.Caution;
}
