namespace Plenipo.AspNetCore.RateLimiting;

/// <summary>
/// Configuration for the chat rate limiter (config section <c>RateLimiting</c>). The default is generous —
/// it's an abuse/cost backstop for the LLM-backed endpoints, not a product throttle — and can be tuned per
/// deployment.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimiting";
    public const int DefaultChatPermitsPerMinute = 120;

    /// <summary>Max chat turns per user per minute on the LLM-backed endpoints. Values ≤ 0 use the default.</summary>
    public int ChatPermitsPerMinute { get; set; } = DefaultChatPermitsPerMinute;
}
