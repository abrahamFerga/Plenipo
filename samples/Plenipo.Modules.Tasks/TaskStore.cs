using System.Collections.Concurrent;

namespace Plenipo.Modules.Tasks;

/// <summary>A task with a stable id, a title, and a done flag.</summary>
public sealed record TaskItem(Guid Id, string Title, bool Done);

/// <summary>
/// A tiny in-memory task store, registered as a singleton. A production module would persist with its
/// own EF Core <c>DbContext</c> (see the Finance sample) and scope rows by tenant; this stays in memory
/// so the template carries no infrastructure.
/// </summary>
public sealed class TaskStore
{
    private readonly ConcurrentQueue<TaskItem> _tasks = new();

    /// <summary>All tasks, in the order they were added.</summary>
    public IReadOnlyList<TaskItem> All() => _tasks.ToArray();

    /// <summary>Append a new, not-done task and return it.</summary>
    public TaskItem Add(string title)
    {
        var task = new TaskItem(Guid.CreateVersion7(), title, false);
        _tasks.Enqueue(task);
        return task;
    }
}
