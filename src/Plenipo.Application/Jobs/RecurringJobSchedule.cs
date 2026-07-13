using Plenipo.Modules.Sdk;

namespace Plenipo.Application.Jobs;

/// <summary>
/// Pure "is it due" logic for module-declared recurring jobs (<see cref="RecurringJobDescriptor"/>).
/// Kept free of any clock, database, or hosting concern so every scheduling property — each
/// cadence, first-run behaviour, catch-up-one, no-double-fire — is unit-testable in isolation;
/// the infrastructure scheduler fetches the last-run stamps and calls this.
/// </summary>
public static class RecurringJobSchedule
{
    /// <summary>The wall-clock interval one cadence window spans.</summary>
    public static TimeSpan IntervalFor(RecurringJobCadence cadence) => cadence switch
    {
        RecurringJobCadence.Hourly => TimeSpan.FromHours(1),
        RecurringJobCadence.Daily => TimeSpan.FromDays(1),
        RecurringJobCadence.Weekly => TimeSpan.FromDays(7),
        _ => throw new ArgumentOutOfRangeException(nameof(cadence), cadence, "Unknown recurring job cadence."),
    };

    /// <summary>
    /// Whether a run should be enqueued now, given when the job was last enqueued for this tenant.
    /// Due when no stamp exists yet (first sweep after a tenant or module appears) or when at
    /// least one full cadence interval has elapsed since the stamp. Because the caller re-stamps
    /// with the enqueue time (not the missed window's start), downtime past several windows
    /// yields exactly one catch-up run and re-anchors the schedule — the catch-up-one contract
    /// documented on <see cref="RecurringJobDescriptor"/>.
    /// </summary>
    public static bool IsDue(DateTimeOffset now, DateTimeOffset? lastEnqueuedAt, RecurringJobCadence cadence) =>
        lastEnqueuedAt is null || now - lastEnqueuedAt.Value >= IntervalFor(cadence);
}
