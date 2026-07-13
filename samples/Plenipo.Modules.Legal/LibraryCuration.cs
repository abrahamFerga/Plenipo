namespace Plenipo.Modules.Legal;

/// <summary>Pure helper for chat-first library curation: slug derivation, matching the seed convention.</summary>
public static class LibraryCuration
{
    /// <summary>"Data Protection" → "data-protection" — the stable per-tenant clause identity.</summary>
    public static string Slugify(string clauseType) =>
        string.Join('-', clauseType.Trim().ToLowerInvariant()
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
}
