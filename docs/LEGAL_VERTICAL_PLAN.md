# Legal vertical — v1 plan

Derived from [research/legal-ai.md](../research/legal-ai.md) (Harvey + competitor analysis).

## Status (2026-07-02)

| Item | Status |
|------|--------|
| 1. Matter entity + live Matters tab | ✅ shipped (`7ca111f`) |
| 2. attach_document_to_matter | ✅ shipped (`7ca111f`) |
| 3. Matter Q&A with citations | ✅ instruction-enforced; citation round-trip pinned by the review-chain test |
| 4. Tenant clause library + playbook | ✅ shipped (`90fedfa`) |
| 5. Drafting to work product | ✅ chain prescribed + tested (`90fedfa`) |
| 6. Playbook contract review | ✅ chain prescribed + full-path test (LegalReviewTests) |
| 7. Bulk review table | ✅ shipped (`26d330f`) — job-backed (`090e2ec`), excerpt-grounded cells, table filed on the matter as PDF |
| 8. Guided workflows | ✅ starter prompts drive the packaged chains |
| 9. WhatsApp client intake | ✅ channel binds to legal per tenant; intake E2E test (WhatsAppLegalIntakeTests) |
| 10. Matter-level ACLs (ethical walls) | ✅ shipped with the RAG core — `restrict_matter_access` / `open_matter_access` tools; a walled matter is invisible in every matter tool, both tabs endpoints, AND its knowledge collection (`MatterRagGate`, fail closed) |

## v2 - daily-practice usability ("a real lawyer shop would use this")

| Item | Status |
|------|--------|
| 11. **Docketing**: matter deadlines with reminders - add_deadline / list_deadlines / complete_deadline tools, Deadlines tab (soonest-first, overdue flagged), DeadlineReminderService produces one inbox notification per deadline as its window opens (wall-respecting; latch = ReminderSentAt) | done |
| 12. **Conflict-of-interest check**: parties on matters (add_party client/adverse/related, list_parties) + check_conflicts across ALL matters at intake - recall-biased loose matching; walled matters report as anonymous screened hits (never named) | done |
| 13. **Time tracking**: log_time (quick capture, deliberately NOT approval-gated - append-only own-user data) / list_time (matter totals or own 14-day summary), Time tab with billable column; walls hold | done |
| 14. **Matter tasks**: add_task (free-text assignee + optional target date; approval-gated) / list_tasks / complete_task, Tasks tab (open first, dated soonest-first); hard remind-me dates stay in docketing | done |
| 15. **Engagement-letter intake flow**: INTAKE WORKFLOW instructions prescribe the 5-step chain (check_conflicts -> create_matter -> add_party each side -> draft_clause 'engagement letter' -> generate_pdf + attach); engagement-letter template seeded in the clause library; suggested prompt added; chain + conflicts flywheel integration-tested | done - v2 table complete |
| 16. **Matter brief**: get_matter_overview - the one-look status (parties, open deadlines w/ overdue flags, open tasks, time totals, documents); instructions steer 'brief me on X' to it | done |
| 17. **Billing export**: export_prebill (matter + optional date range) renders entries + billable/non-billable totals as a PDF and files it on the matter in one step (approval-gated; empty period leaves no orphan) | done |
| 18. **Live demo verification + mock argument syntax**: the full legal workday exercised against the running Aspire stack over AG-UI (create matter -> approve -> docket deadline -> approve -> log time -> brief -> pre-bill -> approve -> real %PDF bytes served). Mock provider upgraded so multi-argument tools are demoable keylessly: quoted spans fill string params in order, ISO dates fill *date* params, numbers fill numerics | done |
| 19. **Matter close-out**: close_matter refuses while deadlines/tasks are open (names the blockers; force only on explicit user confirmation, with a warning), reopen_matter restores; a closed matter's items leave every open list and its deadlines NEVER remind (scanner filters on status) | done |
| 20. **Client status letter**: draft_status_update composes a client-facing letter from real matter state (30-day completions, 60-day upcoming dates, hours - never internal notes/assignees/strategy) and files it as an explicit DRAFT for attorney review | done |
| 21. **Two-stage deadline reminders**: early heads-up when the window opens + a DUE-DAY final notice at/after the due moment ('DEADLINE DUE ... act now'); the final notice supersedes a pending early reminder (one urgent notification, never two); independent one-shot latches | done |
| 22. **Golden evals for the v2 surface**: six new declarative cases pin agent behavior - docket-deadline blocked for approval (quoted-arg routing), list-deadlines friction-free, conflicts check read-only, log_time's deliberate no-approval exception, close-matter blocked, brief routes to get_matter_overview | done - v2 wrap-up |
| 23. **Chat-first library curation** (parity with the-lawyer): save_clause / remove_clause / add_playbook_rule / remove_playbook_rule, approval-gated; removing the engagement-letter clause warns that intake step 4 depends on it; eval pins the approval posture | done |
| 24. **Editable server-driven tables** (platform-level): TabDescriptor.Editor declares upsert/delete endpoints + form fields + a permission; the generic table gains Add / Edit (key-field locked) / Delete-with-confirm; the payload ships the editor ONLY to permitted callers. Clauses + Playbook tabs wired - the library is now click-editable, not just chat-editable | done |
| 25. **Browser walkthrough of the-lawyer (real-click verification)**: full Add -> table -> agent-drafts-it round-trip on the live UI (clause added via the form, immediately drafted by the agent with parties substituted), Edit prefill with locked slug, Delete via confirm dialog, Playbook add/delete, and the admin Agent Profiles page (previously erroring on alpha.2) loads on alpha.4. No product defects found; one tester note: only the Aspire-proxied UI origin is CORS-whitelisted, so use the dashboard's URL, not Vite's target port | done |
| 26. **Matter drill-down** (platform-level detail documents): TabDescriptor.DetailEndpoint ({field} template) returns a generic detail document (title/subtitle + prose or sub-table sections) the shell renders with a View button per row and a Back; /api/legal/matters/{id}/detail composes the working file (parties, open deadlines w/ flags, open tasks, time totals, documents); walls 404 | done |

Pending user decisions: **product name** (EnBanc vs AdLitem, below) and **repo extraction timing**
(recommended: at first tagged release — see Repo strategy).

## v1 scope (build order)

1. **Matter entity + live Matters workspace tab (create/list matters; retire the placeholder tab)** _(effort: medium)_
   - Builds on: Module-owned DbContext + IModule.MigrateAsync (Finance/Nutrition pattern), TenantId global query filters, TabDescriptor with DataEndpoint/Columns for the server-driven table, IModuleToolSource tools create_matter (RequiresApproval) / list_matters
2. **attach_document_to_matter tool — the documented 'store this as part of the case of Julia Assange' flow — plus per-matter document listing** _(effort: small)_
   - Builds on: StoredFile/IFileStore + the plain-text chat-attachment file-id convention (already works on web, AG-UI, SignalR, WhatsApp) + the exact sketch in docs/DOCUMENT_TOOLS.md + RequiresApproval HITL gate
3. **Matter Q&A with citations: ask questions over a matter's documents, every claim cited to file id (+ page where extractable)** _(effort: medium)_
   - Builds on: read_document platform tool (PdfPig + IOcrEngine fallback) chained over matter files by the agent; citation format enforced via ModuleManifest.AgentInstructions; every tool call already audited
4. **Tenant clause library + firm playbook: persisted, admin-editable clauses and review rules replacing the static LegalCatalog** _(effort: small)_
   - Builds on: Existing LegalCatalog becomes seed data in the module schema; legal:admin role + PermissionRequirement-gated CRUD endpoints; playbook writes approval-gated
5. **Drafting to work product: draft an NDA/engagement letter from clause templates + matter facts, emitted as a stored PDF attached to the matter** _(effort: small)_
   - Builds on: draft_clause + generate_pdf platform tool + attach_document_to_matter, chained by the model in one turn; stored_files provenance records origin
6. **Playbook contract review: red-flag report (deviations, missing clauses, risk notes) for one uploaded contract, saved to the matter as a PDF memo** _(effort: medium)_
   - Builds on: read_document + playbook rows + generate_pdf; RequiresApproval before the report is stored; audit trail for the whole review
7. **Bulk review table: docs × questions extraction grid across all documents in a matter, rendered as a tab and exportable** _(effort: large)_
   - Builds on: read_document loop over matter files + server-driven table (TabDescriptor Columns/DataEndpoint) + per-conversation token budgets; needs a background-job seam for long runs — the one genuinely new platform primitive
8. **Guided workflows: packaged multi-step tasks (NDA review, diligence checklist, timeline extraction) that gather context stepwise and emit a defined work product** _(effort: medium)_
   - Builds on: ModuleManifest.AgentInstructions + SuggestedPrompts + composition of the tools above + HITL approvals for side-effecting steps
9. **WhatsApp client intake: a client sends a contract on WhatsApp with a caption, it lands on their matter and triggers the review workflow** _(effort: small)_
   - Builds on: WhatsApp channel (Meta media API → IFileStore with whatsapp provenance, allowlisted/explicit JIT phone-user provisioning — already end-to-end) + attach_document_to_matter; mostly prompts and per-tenant config
10. **Matter-level access control: ethical-wall enforcement that fails closed, per-matter membership, and a per-matter audit view** _(effort: medium)_
   - Builds on: The per-resource ACL seam (owner/editor/viewer) + custom IAuthorizationPolicyProvider policies + append-only audit DB filtered by matter

## Product name candidates

- **EnBanc** — From 'en banc' — the full bench sitting together; evokes collective judgment and thoroughness, exactly the multi-agent/HITL story. Web-checked: cleanest of the list — no legal-tech mark found (only EnBanc Equities, a judgment-finance firm in a different class).
- **AdLitem** — Latin 'for the suit' — a representative appointed to act within one proceeding, which is literally our matter-scoped-agent security model (an agent appointed per matter, fails closed outside it). Web-checked: only small European litigation boutiques use the term; no software mark found.
- **SecondChair** — The associate who sits second chair at trial: positions the AI as support that never leads — great bar-ethics optics ('your AI second chair'). Not web-verified; expect minor podcast/CLE usage, run a clearance search.
- **Silk** — 'Taking silk' = appointment as King's Counsel; short, premium, distinctly legal to common-law buyers. Caution: Amazon Silk (browser) is a well-known software mark and several Silk-named data/security startups exist — no legal-AI collision found, but class-42 clearance is needed.
- **Voir** — From 'voir dire' — Old French 'to speak the truth'; fits the citation-grounded, verify-everything posture, and it's a four-letter brandable. Unverified; Netflix's 'Voir' documentary series exists (different class).
- **BriefForge** — Forges briefs and work product; pairs with the existing NutriForge naming pattern in this codebase. Caution: the space is crowded — LexForge exists (lexforge.ai, lexforge.com, lexforgelegal.com) and BriefCatch is adjacent; differentiable but not pristine.
- **Obiter** — From 'obiter dictum' — a lovely, ownable legal word, but web check shows it is TAKEN: obiter.ai is a Canadian computational-law research platform. Recorded here so it isn't re-proposed. (Also eliminated by checks: Chancery — chancery.ai is a UK legal-AI firm; Paralex — paralex.ai; plus known collisions Docket (DocketAlarm/Docket AI), Gavel (gavel.io), Amicus (Amicus Attorney), and anything with Counsel (TR CoCounsel, LexisNexis CounselLink).)

## Repo strategy

Incubate in this repo now; extract to its own repo at the first tagged platform release. The platform README's end state is right — a product is a thin host in its own repo consuming the Cortex.* NuGet packages and @cortex/ui — but that path isn't live yet: there is no GitHub remote, publish.yml has never run, 0.1.0-alpha is untagged, and the GitHub Packages feed doesn't exist. A separate repo today could only consume the local pack feed (dotnet pack -o ./localfeed), adding repack-and-repin friction during exactly the phase when the vertical will expose platform gaps (citation conventions, a background-job seam for bulk review, finishing the per-resource ACL seam) that are cheapest to fix in the same commit as the module change. So: (1) Now — grow samples/Cortex.Modules.Legal in place (v1 items 1-6), keeping it package-shaped: reference only packable libraries (Cortex.Modules.Sdk, Cortex.Application, Cortex.Core), own DbContext/schema/migrations, no reaching into non-packable internals — CI's pack-and-consume smoke tests (eng/verify-packaging.sh) keep the boundary honest. (2) Extraction trigger — push the GitHub remote and tag 0.1.0-alpha so publish.yml populates the GitHub Packages feed; then create the product repo (e.g. enbanc-app): the module library + a thin branded host (AddCortexModule + CortexApp branding) + its own Terraform/CI, consuming Cortex.* from the feed. (3) After extraction — re-thin the in-repo Legal sample back to a small demo (or keep the current clause-library version as-is) so the samples solution keeps demonstrating a stateless module; the commercial vertical stops being a sample. Platform-shaped needs discovered post-extraction (background jobs, Word add-in seam, research-source connectors) go into the platform repo as primitives, not into the product.
