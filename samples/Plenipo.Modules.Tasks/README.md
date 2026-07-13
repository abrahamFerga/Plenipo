# Plenipo.Modules.Tasks — the minimal module template

The smallest useful Plenipo module: an in-memory to-do list. It's the worked example behind
[`BUILDING_A_MODULE.md`](../../BUILDING_A_MODULE.md), and it shows the whole module surface in miniature:

- a **manifest** (`TasksModule`) declaring two tools and a data tab,
- a **read** tool (`list_tasks`) and a **write** tool (`add_task`) gated behind human approval,
- a **server-driven table** (the Tasks tab) that renders real data with no custom UI,
- **per-tool permissions** the platform enforces *before* the model is called.

## Files

| File | What it is |
|------|------------|
| `TasksModule.cs` | The `IModule`: manifest (tools, tabs, roles, instructions), service registration, the tab's data endpoint. |
| `TasksTools.cs` | The agent tools as plain `[Description]`-annotated methods. |
| `TasksToolSource.cs` | Binds each tool to its permission and exposes them as `ModuleTool`s. |
| `TaskStore.cs` | A tiny in-memory store (a real module persists with its own `DbContext`). |

## Use it as a starting point

1. Copy this folder and rename `tasks` → your vertical.
2. Reference it from your API host and call `builder.AddPlenipoModule<TasksModule>()`.
3. Run — RBAC, audit, token tracking, the admin dashboard, and chat all apply automatically.

It is intentionally **not** registered in `Plenipo.Sample.Host`, so the demo stays focused on the three
real-industry verticals (Finance, Nutrition, Legal). Register it in your own host to see it live.
