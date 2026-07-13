# Golden-conversation evals

Prompt-shaped changes — a module's `AgentInstructions`, a tool's `[Description]`, an
**agent profile** an admin ships — change agent behavior without changing code. The eval
harness gives those changes the same regression net code has.

## How it works

Each eval is a JSON file under
`samples/Plenipo.Sample.Host.IntegrationTests/Evals/cases/`: one user turn against a
module's agent plus the behavioral contract the platform must honor. The runner
(`GoldenConversationEvals`) sends the turn through the **real API** — auth, RBAC tool
filtering, the approval gate, audit, the deterministic Mock provider — and asserts the
contract against the parsed AG-UI event stream. Keyless and CI-safe; runs with the rest
of the integration suite (`dotnet test samples/Plenipo.Samples.slnx`).

## Case schema

```jsonc
{
  "name": "legal-create-matter-needs-approval",
  "module": "legal",                          // AG-UI module id
  "message": "Create a matter for the Acme dispute",
  "role": "system_admin",                     // dev-auth role (default system_admin)
  "expectToolCalls": ["create_matter"],       // must appear as TOOL_CALL_START
  "forbidToolCalls": [],                      // must NOT be invoked
  "expectApproval": true,                     // approval_required CUSTOM event fired
  "replyMustContain": ["approval"],           // case-insensitive, on streamed text
  "replyMustNotContain": ["created matter"]   // the model may not claim success
}
```

Every case also implicitly asserts protocol health: `RUN_STARTED` + `RUN_FINISHED`
present, `RUN_ERROR` absent. Unknown JSON fields fail loudly (typo protection).

## What to cover when you change…

| Change | Add/adjust a case asserting |
|---|---|
| A tool description / name | the intent still routes to that tool (`expectToolCalls`) |
| A `RequiresApproval` flag | `expectApproval` + the reply doesn't claim completion |
| An agent profile (Append/Replace) | the reply reflects the profile's policy (`replyMustContain`) |
| RBAC baselines | `role` + `forbidToolCalls` for a role that must not reach the tool |
| A skill's description | the matching intent calls `load_skill` |

Limits: the Mock provider selects tools by name-token match — evals verify the
platform's contract (routing, gating, protocol), not real-model reasoning quality.
For provider-quality evals, point the same cases at a real provider out-of-band.
