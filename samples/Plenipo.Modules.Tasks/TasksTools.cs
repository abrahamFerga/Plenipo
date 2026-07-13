using System.ComponentModel;

namespace Plenipo.Modules.Tasks;

/// <summary>
/// The Tasks module's agent tools: read the list and add to it. <c>add_task</c> writes, so the manifest
/// marks it as requiring approval — the agent is blocked until a human approves the call.
/// </summary>
public sealed class TasksTools(TaskStore store)
{
    [Description("List the current tasks and whether each one is done.")]
    public string ListTasks()
    {
        var tasks = store.All();
        if (tasks.Count == 0)
        {
            return "There are no tasks yet. Add one with add_task.";
        }

        var lines = tasks.Select(t => $"{(t.Done ? "[x]" : "[ ]")} {t.Title}");
        return $"You have {tasks.Count} task(s): {string.Join("; ", lines)}.";
    }

    [Description("Add a new task to the list. Returns the created task.")]
    public string AddTask(
        [Description("Short description of the task, e.g. 'Buy groceries'.")] string title)
    {
        var task = store.Add(title);
        return $"Added task \"{task.Title}\".";
    }
}
