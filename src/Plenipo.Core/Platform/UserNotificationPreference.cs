using Plenipo.Core.Entities;

namespace Plenipo.Core.Platform;

/// <summary>
/// One user's stance on one notification category (e.g. mute "bill-reminders" without losing
/// everything else). No row means the category is on — rows exist only where a user changed
/// the default, so modules can add categories without a backfill.
/// </summary>
public sealed class UserNotificationPreference : TenantEntityBase
{
    public Guid UserId { get; set; }

    /// <summary>The category id a module declared (ModuleManifest.NotificationCategories).</summary>
    public required string Category { get; set; }

    public bool Enabled { get; set; } = true;
}
