using Cortex.Application.Jobs;
using Cortex.Modules.Sdk;

namespace Cortex.Application.Tests.Jobs;

/// <summary>
/// Covers the pure due-decision behind module-declared recurring jobs: every cadence's window,
/// first-run behaviour (no stamp yet), no-double-fire inside a window, and the catch-up-one
/// contract after downtime — the properties the scheduler's restart-safety rests on.
/// </summary>
public sealed class RecurringJobScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RecurringJobCadence.Hourly, 1)]
    [InlineData(RecurringJobCadence.Daily, 24)]
    [InlineData(RecurringJobCadence.Weekly, 24 * 7)]
    public void IntervalFor_MapsEachCadenceToItsWindow(RecurringJobCadence cadence, int hours)
    {
        Assert.Equal(TimeSpan.FromHours(hours), RecurringJobSchedule.IntervalFor(cadence));
    }

    [Fact]
    public void IntervalFor_UnknownCadence_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecurringJobSchedule.IntervalFor((RecurringJobCadence)99));
    }

    [Theory]
    [InlineData(RecurringJobCadence.Hourly)]
    [InlineData(RecurringJobCadence.Daily)]
    [InlineData(RecurringJobCadence.Weekly)]
    public void NoStampYet_IsDueImmediately(RecurringJobCadence cadence)
    {
        // A new tenant (or a newly shipped module) gets its first run on the next sweep — a digest
        // starts delivering on day one, not after a silent first window.
        Assert.True(RecurringJobSchedule.IsDue(Now, lastEnqueuedAt: null, cadence));
    }

    [Theory]
    [InlineData(RecurringJobCadence.Hourly)]
    [InlineData(RecurringJobCadence.Daily)]
    [InlineData(RecurringJobCadence.Weekly)]
    public void InsideTheWindow_IsNotDue_SoASweepNeverDoubleFires(RecurringJobCadence cadence)
    {
        // Just ran: the very next sweep (and every sweep until the window elapses) must skip it.
        Assert.False(RecurringJobSchedule.IsDue(Now, Now, cadence));
        Assert.False(RecurringJobSchedule.IsDue(Now.AddMinutes(2), Now, cadence));

        var almostAWindow = RecurringJobSchedule.IntervalFor(cadence) - TimeSpan.FromSeconds(1);
        Assert.False(RecurringJobSchedule.IsDue(Now + almostAWindow, Now, cadence));
    }

    [Theory]
    [InlineData(RecurringJobCadence.Hourly)]
    [InlineData(RecurringJobCadence.Daily)]
    [InlineData(RecurringJobCadence.Weekly)]
    public void ExactlyOneWindowElapsed_IsDue(RecurringJobCadence cadence)
    {
        Assert.True(RecurringJobSchedule.IsDue(Now + RecurringJobSchedule.IntervalFor(cadence), Now, cadence));
    }

    [Fact]
    public void LongDowntime_YieldsExactlyOneCatchUpRun_NotOnePerMissedWindow()
    {
        // The host slept through five daily windows. The first sweep back is due once…
        var wayPastDue = Now.AddDays(5);
        Assert.True(RecurringJobSchedule.IsDue(wayPastDue, Now, RecurringJobCadence.Daily));

        // …and after the caller re-stamps with the catch-up run's own time (not the missed
        // window's), the very next sweep is NOT due again — catch-up-one, never catch-up-all.
        Assert.False(RecurringJobSchedule.IsDue(wayPastDue.AddMinutes(2), wayPastDue, RecurringJobCadence.Daily));

        // The schedule re-anchors: the next natural window opens a full day after the catch-up.
        Assert.True(RecurringJobSchedule.IsDue(wayPastDue.AddDays(1), wayPastDue, RecurringJobCadence.Daily));
    }
}
