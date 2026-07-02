using Cortex.Modules.Legal;

namespace Cortex.Modules.Legal.Tests;

public sealed class LegalCatalogTests
{
    [Fact]
    public void Search_IsCaseInsensitive_AndMatchesCategoryAndSummary()
    {
        Assert.Contains(LegalCatalog.Search("CONFIDENTIAL"), c => c.Id == "confidentiality");
        // "Risk allocation" is a category shared by indemnification + limitation-of-liability.
        Assert.True(LegalCatalog.Search("risk").Count() >= 2);
    }

    [Fact]
    public void Search_BlankQuery_ReturnsNothing()
    {
        Assert.Empty(LegalCatalog.Search("   "));
    }

    [Fact]
    public void Render_SubstitutesBothParties()
    {
        var clause = LegalCatalog.Search("indemnification").First();

        var rendered = LegalCatalog.Render(clause, "Acme Corp", "Beta LLC");

        Assert.Contains("Acme Corp", rendered.Body, StringComparison.Ordinal);
        Assert.Contains("Beta LLC", rendered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{PartyA}", rendered.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{PartyB}", rendered.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_BlankParties_FallBackToPlaceholders()
    {
        var clause = LegalCatalog.Search("governing").First();

        var rendered = LegalCatalog.Render(clause, "", "  ");

        Assert.Contains("Party A", rendered.Body, StringComparison.Ordinal);
        Assert.Contains("Party B", rendered.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftClause_UnknownType_AsksToSearchFirst()
    {
        var result = new LegalTools().DraftClause("teleportation rights", "A", "B");
        Assert.Contains("search_clauses", result, StringComparison.Ordinal);
    }

    [Fact]
    public void DraftClause_KnownType_RendersWithDisclaimer()
    {
        var result = new LegalTools().DraftClause("termination", "Acme Corp", "Beta LLC");

        Assert.Contains("Acme Corp", result, StringComparison.Ordinal);
        Assert.Contains("not legal advice", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Manifest_DeclaresClauseAndMatterToolsWithApprovalGatedWrites()
    {
        var manifest = new LegalModule().Manifest;

        Assert.Equal("legal", manifest.Id);
        Assert.Equal(
            ["search_clauses", "draft_clause", "create_matter", "list_matters", "attach_document_to_matter", "list_matter_documents"],
            manifest.Tools.Select(t => t.Name));

        // The side-effecting matter tools are held for human approval; the read tools are not.
        Assert.All(
            manifest.Tools.Where(t => t.Name is "create_matter" or "attach_document_to_matter"),
            t => Assert.True(t.RequiresApproval));
        Assert.All(
            manifest.Tools.Where(t => t.Name is "list_matters" or "list_matter_documents"),
            t => Assert.False(t.RequiresApproval));

        Assert.Contains(manifest.Tabs, t => t.Id == "chat");
        // The Matters tab is live: server-driven data, no placeholder needed.
        var mattersTab = manifest.Tabs.First(t => t.Id == "matters");
        Assert.Equal("/api/legal/matters", mattersTab.DataEndpoint);
    }
}
