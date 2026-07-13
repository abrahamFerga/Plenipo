namespace Plenipo.Modules.Sdk;

/// <summary>
/// A notification category a module emits (the <c>Category</c> it passes to the notifier), declared
/// manifest-first so the platform can offer users a per-category mute switch without executing
/// module code. Example: <c>new("bill-reminders", "Bill reminders", "A recurring charge is due soon.")</c>.
/// </summary>
public sealed record NotificationCategoryDescriptor(string Id, string Label, string? Description = null);
