using Cortex.Modules.Legal.Persistence;

namespace Cortex.Modules.Legal.Tests;

/// <summary>
/// The reminder latch semantics the docketing scanner relies on: a reminder fires only for an
/// open, never-reminded deadline whose window has opened — and exactly once.
/// </summary>
public sealed class MatterDeadlineTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 5, 12, 0, 0, TimeSpan.Zero);

    private static MatterDeadline Deadline(DateTimeOffset dueAt, int daysBefore = 3) => new()
    {
        Title = "Answer to complaint",
        DueAt = dueAt,
        ReminderDaysBefore = daysBefore,
    };

    [Fact]
    public void Fires_inside_the_window_and_when_overdue()
    {
        Assert.True(Deadline(Now.AddDays(2)).IsReminderDue(Now));  // window open (3 days before)
        Assert.True(Deadline(Now.AddDays(-1)).IsReminderDue(Now)); // overdue still reminds
        Assert.True(Deadline(Now.AddHours(1), daysBefore: 0).IsReminderDue(Now.AddHours(2)) ); // 0-day window = at/after due
    }

    [Fact]
    public void Does_not_fire_before_the_window_opens()
    {
        Assert.False(Deadline(Now.AddDays(10)).IsReminderDue(Now));
        Assert.False(Deadline(Now.AddDays(1), daysBefore: 0).IsReminderDue(Now)); // 0-day window: only at/after due
    }

    [Fact]
    public void Never_fires_twice_or_for_completed_deadlines()
    {
        var reminded = Deadline(Now.AddDays(1));
        reminded.ReminderSentAt = Now.AddHours(-1);
        Assert.False(reminded.IsReminderDue(Now));

        var completed = Deadline(Now.AddDays(1));
        completed.CompletedAt = Now.AddHours(-1);
        Assert.False(completed.IsReminderDue(Now));
    }

    [Fact]
    public void Final_notice_fires_at_or_after_due_and_exactly_once()
    {
        var due = Deadline(Now.AddHours(-1));
        Assert.True(due.IsFinalNoticeDue(Now)); // at/after the due moment

        Assert.False(Deadline(Now.AddDays(1)).IsFinalNoticeDue(Now)); // not before

        var noticed = Deadline(Now.AddHours(-1));
        noticed.FinalNoticeSentAt = Now.AddMinutes(-30);
        Assert.False(noticed.IsFinalNoticeDue(Now)); // one-shot

        var completed = Deadline(Now.AddHours(-1));
        completed.CompletedAt = Now.AddMinutes(-30);
        Assert.False(completed.IsFinalNoticeDue(Now)); // done is done
    }

    [Fact]
    public void Early_reminder_and_final_notice_are_independent_latches()
    {
        // An early-reminded deadline still gets its final notice at the due moment.
        var d = Deadline(Now.AddHours(-1));
        d.ReminderSentAt = Now.AddDays(-3);
        Assert.False(d.IsReminderDue(Now));
        Assert.True(d.IsFinalNoticeDue(Now));
    }
}
