# Build your first Plenipo module

Plenipo is a **base platform**: a new vertical (finance, legal, nutrition, your industry) is a **module**,
not a fork. This guide builds a complete — if deliberately tiny — module from scratch: a **to-do list**.
By the end you'll have a chat agent with two tools, a data tab, per-tool permissions, audit logging, and a
human-approval gate for the tool that writes — most of which the platform gives you for free.

The finished code is in [`samples/Plenipo.Modules.Tasks`](samples/Plenipo.Modules.Tasks). Read it alongside
this guide, or copy that folder and rename `tasks` to your own vertical.

> **Prerequisite:** a host that references `Plenipo.AspNetCore` — either the sample
> ([`samples/Plenipo.Sample.Host`](samples/Plenipo.Sample.Host)) or your own. New to Plenipo? Run
> [GETTING_STARTED.md](GETTING_STARTED.md) first so you have the stack up.

## A module is four small pieces

| Piece | Type | Responsibility |
|-------|------|----------------|
| **Manifest** | `IModule.Manifest` | A *static* declaration of the module's tools, tabs, roles, and agent instructions. Read before any module code runs. |
| **Tools** | plain methods | What the agent can *do* — ordinary `[Description]`-annotated methods. |
| **Tool source** | `IModuleToolSource` | Binds each tool to the **permission** that gates it and hands the executables to the runner. |
| **Endpoints** *(optional)* | `IModule.MapEndpoints` | HTTP your module needs — e.g. the JSON behind a data tab. Persistence (your own `DbContext`) is optional too. |

Everything else — the module switcher, RBAC enforcement, the audit log, token tracking, the admin
dashboard, streaming chat — is the platform's job and applies automatically once you install the module.

## Step 1 — Declare the manifest

A module is a class implementing `IModule`. Its `Manifest` is **manifest-first**: the platform enumerates
capabilities, builds navigation, and enforces security from it *without executing your code*.

```csharp
public sealed class TasksModule : IModule
{
    public const string Id = "tasks";
    public const string ViewTasks = "tasks.items.view";

    public ModuleManifest Manifest { get; } = new()
    {
        Id = Id,
        DisplayName = "Tasks",
        Version = "1.0.0",
        AgentInstructions = "You are a concise task assistant. Use list_tasks to read and add_task to add …",
        SuggestedPrompts = ["List my tasks", "Add a task to buy groceries"],
        Roles = ["tasks:user", "tasks:admin"],
        Tools =
        [
            new ToolDescriptor
            {
                Name = "list_tasks",
                Description = "List the current tasks and whether each one is done.",
                Permission = Permissions.ForTool(Id, "list_tasks"),
            },
            new ToolDescriptor
            {
                Name = "add_task",
                Description = "Add a new task to the list.",
                Permission = Permissions.ForTool(Id, "add_task"),
                RequiresApproval = true,   // ← writes, so it needs a human's OK
            },
        ],
        Tabs =
        [
            new TabDescriptor { Id = "chat", Label = "Chat", Route = "/tasks/chat", Order = 0 },
            new TabDescriptor
            {
                Id = "tasks", Label = "Tasks", Route = "/tasks/list", Order = 1,
                Permission = ViewTasks,
                DataEndpoint = "/api/tasks/items",                       // ← a server-driven table…
                Columns = [new("title", "Task"), new("done", "Done")],   // …rendered from these columns
            },
        ],
    };
    // RegisterServices + MapEndpoints below…
}
```

Two things worth noticing already:
- **Permissions are conventions.** `Permissions.ForTool("tasks", "add_task")` is just the string
  `tools.tasks.add_task`. You never invent a permission system — you declare strings and the platform
  enforces them (with wildcards like `tools.tasks.*`).
- **`RequiresApproval = true`** on `add_task` is the entire opt-in to the human-in-the-loop gate.

## Step 2 — Write the tools

Tools are plain methods. The `[Description]` attributes are not documentation — they're what the model
reads to decide *which* tool to call and *how* to fill the arguments. Be specific.

```csharp
public sealed class TasksTools(TaskStore store)
{
    [Description("List the current tasks and whether each one is done.")]
    public string ListTasks() => /* … read store, return a sentence … */;

    [Description("Add a new task to the list. Returns the created task.")]
    public string AddTask(
        [Description("Short description of the task, e.g. 'Buy groceries'.")] string title)
        => /* … store.Add(title) … */;
}
```

Return human-readable strings — the agent narrates them back to the user. (`TaskStore` here is a tiny
in-memory list; a real module would persist with its own EF Core `DbContext` — see the Finance sample.)

## Step 3 — Bind tools to permissions

The manifest *describes* tools; the **tool source** supplies the *executables*. It runs inside the request
scope, so its tools can close over scoped services (a `DbContext`, the current user, …).

```csharp
public sealed class TasksToolSource : IModuleToolSource
{
    public string ModuleId => TasksModule.Id;

    public IReadOnlyList<ModuleTool> GetTools(IServiceProvider scoped)
    {
        var tools = scoped.GetRequiredService<TasksTools>();
        return
        [
            new ModuleTool
            {
                ModuleId = ModuleId, Name = "list_tasks",
                Permission = Permissions.ForTool(ModuleId, "list_tasks"),
                Function = AIFunctionFactory.Create(tools.ListTasks, name: "list_tasks"),
            },
            new ModuleTool
            {
                ModuleId = ModuleId, Name = "add_task",
                Permission = Permissions.ForTool(ModuleId, "add_task"),
                Function = AIFunctionFactory.Create(tools.AddTask, name: "add_task"),
                RequiresApproval = true,
            },
        ];
    }
}
```

This is where Plenipo's signature behaviour lives: **the runner filters this list by the caller's
permissions _before_ building the model request**, so a user without `tools.tasks.add_task` never even
sees that tool's schema. And a `RequiresApproval` tool is intercepted and parked for approval instead of
executing.

Wire the services in `RegisterServices`:

```csharp
public void RegisterServices(IServiceCollection services, IConfiguration configuration)
{
    services.AddSingleton<TaskStore>();
    services.AddSingleton<TasksTools>();
    services.AddSingleton<IModuleToolSource, TasksToolSource>();
}
```

## Step 4 — Back the tab with data (optional)

The Tasks tab declared a `DataEndpoint`. Map it in `MapEndpoints`; the shell renders the returned JSON
array as a table using the tab's `Columns` — no custom React required.

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    var group = endpoints.MapGroup("/api/tasks").WithTags("Tasks").RequireAuthorization();
    group.MapGet("/items", (TaskStore store) => Results.Ok(store.All()))
        .RequireAuthorization(PermissionRequirement.PolicyName(ViewTasks));
}
```

If your module owns persistence, this is also where a `DbContext` would come in — apply its migrations in
`IModule.MigrateAsync` and seed reference data in `SeedAsync`. (The Finance sample does exactly that.)

> **Field names are the endpoint's JSON, not your C# properties.** The host serializes camelCase, so
> `Columns`, editor fields, and `{field}` placeholders are declared camelCase (`"monthlyLimit"`) even
> though the property they came from is `MonthlyLimit`.

### Make the table editable — still no custom UI

A read-only table often isn't enough. Declare an `Editor` on the tab and the shell's generic table grows
**Add / Edit / Delete** — forms, validation, and confirmation dialogs included. The Finance budgets tab:

```csharp
Editor = new TabEditor
{
    UpsertEndpoint = "/api/finance/budgets",   // POST target; body = a JSON object of the fields
    Permission = EditBudgets,                  // affordances ship ONLY to callers holding this
    KeyField = "category",                     // identifies a row for Edit (locked in the form)
    Fields =
    [
        new("category", "Category"),
        new("monthlyLimit", "Monthly limit", Numeric: true),            // number input, posts a JSON number
        new("currency", "Currency (blank keeps current)", Required: false),
    ],
},
```

The contract, so your endpoint works unshimmed:

- **`UpsertEndpoint`** receives a POST for both add and edit; upsert by `KeyField` server-side.
- **`DeleteEndpoint`** (optional) is a template with one `{field}` placeholder — e.g.
  `/api/legal/clauses/{slug}` — resolved from the row. Omit it for no Delete.
- **`KeyField = null`** means add-only (no per-row Edit) — right for append-style data like a diary.
- **`Numeric: true`** renders a number input and posts a JSON **number**, so a `decimal`/`int` property
  binds directly. **`Required: false`** fields left blank are **omitted** from the body (your endpoint
  sees `null`, never `""`). **`Multiline: true`** renders a textarea.
- The `Permission` gates only the UI affordances; your endpoints stay authorization-gated regardless —
  declare `.RequireAuthorization(...)` on them like any other.

### Give rows a drill-down — `DetailEndpoint`

For "click a row, see the whole record" (a matter's parties, deadlines, and time in one page), declare a
`DetailEndpoint` template on the tab:

```csharp
DetailEndpoint = "/api/legal/matters/{id}/detail",
```

The shell adds a **View** button per row and renders whatever generic *detail document* your endpoint
returns: `{ title, subtitle?, sections: [ { heading, text? } | { heading, columns: [{field, header}],
rows: [...] } ] }` — prose sections and tables, composed however the record demands, still zero React.
The Legal sample's matter working file is the worked example.

## Step 5 — Install it

One line in your host:

```csharp
builder.AddPlenipoModule<TasksModule>();
```

That's the whole integration. The module now appears in the switcher, its agent answers chat with your
two tools and instructions, the Tasks tab shows live data, every tool call is permission-checked and
audited, token usage is tracked, and it's all visible in the admin dashboard — none of which you wrote.

## See it work

With `TasksModule` installed in your host (Step 5 — the sample host doesn't ship it), start the host and UI
(see [GETTING_STARTED.md](GETTING_STARTED.md)) and pick **Tasks** in the switcher:

1. **“List my tasks”** → `list_tasks` runs (read-only) and is recorded in **Admin → Audit Log**.
2. **“Add a task to buy groceries”** → `add_task` **writes**, so the agent is **blocked pending your
   approval**. Approve it from the **Approvals** panel — that's the human-in-the-loop gate, with zero
   extra code.
3. **Admin → Security** shows each tool mapped to the permission it requires.

> In development you're signed in as `system_admin`, which holds the global wildcard, so your tools and tab
> work immediately — no permission wiring needed. In production, access is by **permission**, not by role:
> every signed-in user sees the module in the switcher, but *using* its tools needs `tools.tasks.*` and
> *opening* its data tab needs `tasks.items.view`, granted per user from **Admin → Users & Roles**. By
> design, a role never auto-unlocks a module's tools (see `RolePermissions`); the `tasks:user` / `tasks:admin`
> roles you declared are yours to map onto those permissions in your product's role table.

## Test it

A module is plain code, so it's plain to test — no host required. The example ships with
[`samples/Plenipo.Modules.Tasks.Tests`](samples/Plenipo.Modules.Tasks.Tests), a handful of fast xUnit tests
that are a good shape for your own vertical:

- the **manifest** declares the tools you expect, with the right permissions and `RequiresApproval`;
- the **tool source** produces matching executables bound to those permissions (catches a renamed tool);
- a data tab's **`Columns`** line up with the row JSON (catches a typo that would render a blank column);
- the **tools** behave — adding a task then listing reflects it.

```bash
dotnet test samples/Plenipo.Modules.Tasks.Tests
```

Wire the same project into CI and your module is covered before it ever reaches a host.

## Where to go next

- **Full source:** [`samples/Plenipo.Modules.Tasks`](samples/Plenipo.Modules.Tasks) — copy it to start your own.
- **Persistence & migrations:** the Finance sample ([`samples/Plenipo.Modules.Finance`](samples/Plenipo.Modules.Finance)) — its own `DbContext`, tenant-scoped rows, seeded demo data.
- **How it all fits together:** [ARCHITECTURE.md](ARCHITECTURE.md) — the chat security spine, the module system, and the data model, with diagrams.
