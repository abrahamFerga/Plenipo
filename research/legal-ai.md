# Legal AI vertical — market research & v1 plan

*Prepared July 2026 for the Plenipo legal vertical. Current state: `samples/Plenipo.Modules.Legal` is a thin, stateless clause-library + drafting demo (8 hardcoded clauses, `search_clauses` / `draft_clause` tools, a placeholder Matters tab). This report maps the market (Harvey and its competitors), fixes the ownership boundary vs. practice-management systems, and derives a v1 scope where each feature composes an existing Plenipo primitive.*

## Executive summary

- The legal-AI market has converged on a standard shape: **cited document Q&A + matter workspaces + bulk docs×questions review + playbook redlining + template drafting + agentic workflows**, wrapped in an enterprise security posture. These are table stakes, not differentiators.
- The **ownership boundary** is settled and every successful vendor respects it: AI products own analysis, drafting, workflows, and a matter-scoped permission model; they **integrate with** (never rebuild) practice management (matters-of-record, billing, trust accounting, intake), DMS (iManage/NetDocuments), docketing (LawToolBox-class), and wall/conflict policy (Intapp).
- **Plenipo is unusually well positioned** for this vertical: the platform's document tools (`read_document`/`generate_pdf`/OCR seam), tenant file store, chat-attachment convention, HITL approvals, pre-model-call tool authorization, append-only audit, and WhatsApp channel map almost one-to-one onto the table-stakes list. The lawyer scenario ("store this as part of the case of Julia Assange") is literally the worked example in `docs/DOCUMENT_TOOLS.md`.
- v1 = a **Matter-centric legal module**: matter entity + attach-document + cited Q&A + tenant playbook + PDF drafting + playbook review, then bulk review and guided workflows. The only genuinely new platform primitive needed is a background-job seam for long-running bulk review.
- Ship it **in this repo first**, extract to its own repo when the GitHub remote + package publishing go live.

## 1. Where the market is: Harvey's product surface

Harvey (100,000+ legal professionals, 1,000+ customers, 60+ countries, more than half the AmLaw 100) defines the reference surface:

| Surface | What it is |
|---|---|
| **Assistant** | Legal chat over prompts + attached files (up to 10,000 Vault files per thread); native .docx editing in-thread with per-edit justifications; citations whenever source material is present. |
| **Vault** | Per-project repositories (100k files / 100 GB); two query modes: aggregated cited answers, and **Review Tables** — bulk extraction across up to 10,000 files with 7 typed column formats, conditional columns, Excel export with verified/flagged cells. |
| **Workflows / Agents** | Pre-built guided multi-step tasks (Draft from Template, Timeline of Key Events, Diligence Insights, transcript analysis, translate/proofread/transcribe…) plus a no-code Workflow Builder with conditionals, classification, role-based permissions; firm playbooks/style guides embeddable. |
| **Knowledge** | Research over 500+ vetted sources: agentic EDGAR, EUR-Lex, licensed publishers, and **Ask LexisNexis** (June 2025 alliance — US primary law with Shepard's graph grounding); firm Vault knowledge bases registrable as retrieval sources. |
| **Word/Outlook add-ins** | Ask + Edit modes; tracked-changes redlines on 100+ page documents; runs firm playbooks against the open document; Outlook capture into Vault; M365 Copilot; mobile. |
| **Citations & hallucination controls** | Inline citations to source sections; claim-decomposition pipeline cross-checks factual claims before display (claimed ~0.2% hallucination rate, no independent benchmark); Review Table verify/flag states survive export. |
| **Matters & collaboration** | Matter-centric organization; bidirectional iManage, NetDocuments, SharePoint, Google Drive; Shared Spaces (branded firm-client workspaces, resource-level permissions, dual-side admin approval, full audit); **Intapp Walls sync** — ethical walls enforced across Assistant/Vault/Workflows. |
| **Security** | SOC 2 Type II, ISO 27001/27701/42001, GDPR/CCPA; no training on customer data; Zero Data Retention from model providers; SAML SSO, audit logs, IP allow-listing, data residency (US/EU-CH/AU). |
| **2026 direction** | Long-horizon agents constrained by ethical walls; "Memory" for cross-matter context retention. |

**Day-to-day usage:** first-draft agreements from firm precedent; data-room-scale due diligence (review tables → cited red-flags memo); closing checklists/8-Ks; cited case-law research; discovery and transcript analysis; in-house NDA/supplier review against playbooks. Evidence: A&O Shearman firmwide (7,000+ people, human audit of all outputs); GSK Stockmann 15–20% savings on structured diligence, up to 75% on unstructured data rooms; Talanx up to 60% faster reviews via the Word add-in.

## 2. Competitor landscape

- **CoCounsel (Thomson Reuters)** — exclusive Westlaw/Practical Law grounding; Deep Research memos + **Deep Research Verify** (automated citation verification); Tabular Analysis (10k docs × 100 questions); litigation skills (deposition prep); agent-era rebuild GA Aug 2026; 1M+ users.
- **Legora** — the closest Harvey clone (Europe-first); signature **Tabular Review** grid (docs as rows, prompts as columns, tens of thousands of parallel calls); Word add-in with precedent-based drafting + DeepL translation; client **Portal**; connected-workspace positioning.
- **Spellbook** — Word-native contract review/redlining for smaller firms/in-house; learns user preferences; **market benchmarking** against 2,300+ industry-standard agreements (unique); Spellbook Associate multi-document transactional agent.
- **Luminance** — proprietary legal-trained models + "Panel of Judges" cross-validation; **Autopilot**: fully autonomous agent-to-agent NDA negotiation (unique); 80+ languages; spans into CLM (obligation tracking, renewals).
- **Robin AI** — contract-repository-centric (Reports/Reviews/Draft/Agent tiers, 500k+ doc repository, obligation checklists). **Caveat:** managed services sold to Scissero (Dec 2025), Microsoft hired a raft of its engineers for the Word team (Jan 2026) — treat as weakened.
- **Paxton** — affordable all-in-one for solo/small firms; patent-pending **Citator** (overruled/affirmed/questioned analysis); **Confidence Indicator** on every answer (unique transparency feature); published 94.7% non-hallucination benchmark; SOC 2/ISO/HIPAA.

### Feature matrix

| Vendor | Cited doc Q&A | Matter/vault workspaces | Bulk review grid | Playbook review & redlining | Template drafting | Word add-in | Research grounding | Verification controls | Agentic workflows | Autonomous negotiation | Client portal | DMS/PM integrations |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| **Harvey** | ✓ | ✓ Vault | ✓ Review Tables | ✓ | ✓ | ✓ | ✓ EDGAR/LexisNexis/500+ | ✓ claim pipeline | ✓ + builder | — | ✓ Shared Spaces | ✓ iManage/NetDocs/Intapp |
| **CoCounsel** | ✓ | ◐ | ✓ Tabular Analysis | ✓ | ✓ | ◐ M365 | ✓ Westlaw/PL (exclusive) | ✓ Verify | ✓ | — | — | ✓ Litify/Smokeball/HighQ |
| **Legora** | ✓ | ✓ | ✓ Tabular Review | ✓ | ✓ + translation | ✓ | ✓ | ◐ | ✓ | — | ✓ Portal | ◐ |
| **Spellbook** | ✓ | ◐ Associate | ✓ dataroom | ✓ + learns prefs | ✓ + market benchmark | ✓ native | — | ◐ | ✓ Associate | — | — | ◐ |
| **Luminance** | ✓ 80+ langs | ✓ CLM repo | ✓ diligence | ✓ | ✓ | ✓ | — | ✓ Panel of Judges | ✓ | ✓ Autopilot | ◐ | ✓ CLM |
| **Robin AI** | ✓ | ✓ repository | ✓ Reports | ✓ Reviews | ✓ Draft | ✓ | — | ◐ | ✓ Agent | ◐ NDA-only | — | ✓ CMS |
| **Paxton** | ✓ | ◐ | ◐ | ◐ | ✓ | — | ✓ Citator | ✓ Confidence | ◐ | — | — | — |
| **Plenipo Legal v1 (target)** | ✓ file-id cites | ✓ Matters | ◐ matter-scale | ◐ red-flags | ✓ PDF | — post-v1 | — post-v1 | ◐ cites+HITL+audit | ✓ tools+approvals | — | — (WhatsApp intake) | — post-v1 |

Legend: ✓ shipped/core · ◐ partial/adjacent · — absent.

## 3. Table stakes (in most/all products)

1. **Cited document Q&A** — answers grounded in uploaded sources with links back to the passage; hallucination controls (Verify, Citator, Confidence, Panel of Judges) are the trust layer. Non-negotiable.
2. **Matter/project workspaces** scoping documents, threads, and work product to one engagement.
3. **Bulk multi-document review** as a docs × questions table with typed extraction and export — the standard diligence/discovery UX.
4. **Playbook contract review/redlining + template drafting** — flag risky/missing clauses, propose fixes, draft from firm precedent.
5. **Word add-in** — lawyers live in Word; every competitor meets them there. (Microsoft hiring Robin AI's Word engineers signals this layer commoditizes into Word itself.) Acceptable v1 gap for us; mandatory later.
6. **Agentic multi-step workflows** — 2025–26 wave; every vendor ships them.
7. **Enterprise security posture** — SOC 2/ISO-class, no-training-on-client-data, SSO, audit logs, residency, matter-scoped permissions/ethical walls that fail closed.
8. **Integration posture** — anchor to the PM/DMS client-matter identity; never rebuild billing/docketing/conflicts/document-of-record.

## 4. The ownership boundary (AI product vs. practice management)

Synthesis across Clio/MyCase/Smokeball/Actionstep vs. Harvey/CoCounsel/Vincent:

**An AI-first legal product MUST OWN:** (1) document/matter-corpus analysis with citation-grounded answers; (2) drafting from firm templates + matter facts; (3) research grounding (own or licensed corpus — later for us); (4) multi-step agent workflows; (5) a matter-scoped permission model where the client-matter is the atomic security boundary, fails closed, and produces audit logs; (6) Vault-style project workspaces for bulk review.

**MUST INTEGRATE WITH (never rebuild):** PM systems (matters-of-record, contacts/parties, billing/invoicing/trust accounting, intake/CRM — trust accounting alone is bar-regulated); DMS (iManage/NetDocuments/SharePoint) as document system of record; rules-based docketing engines (LawToolBox-class — a licensed-content problem, not a feature); Intapp-class wall/conflict policy (sync walls in, enforce at retrieval/context/output); Word/Outlook as authoring surfaces.

**GRAY ZONE (AI may generate, PM owns the ledger):** suggested time entries/billing narratives from activity, matter summarization, intake-form triage.

**ANTI-PATTERN:** minting your own canonical matter IDs, storing canonical documents, or running a billing ledger. Note for us: Plenipo matters are workspaces; when a firm has a PM/DMS, our Matter entity should carry an external client-matter reference rather than pretend to be the system of record.

**The boundary is moving from the PM side too:** Clio bought vLex ($1B) to fuse Vincent AI in; Smokeball embeds CoCounsel; MyCase IQ does matter Q&A natively. PM systems will pull commodity AI in — which is why the AI layer must be *better at analysis/workflows*, not try to out-PM them.

## 5. Platform fit: what Plenipo already gives us

| Legal need (table stake) | Plenipo primitive, today |
|---|---|
| Matter workspaces | Module-owned DbContext + `TenantId` global query filters + server-driven tabs (`TabDescriptor` with `DataEndpoint`/`Columns`) |
| Document ingest | Tenant file store (`IFileStore`, `stored_files`, local/Azure Blob), chat attachments, `POST /api/files` |
| Doc analysis | `read_document` (PdfPig + pluggable `IOcrEngine` fallback), `list_documents` — platform tools on every module's agent |
| Work product | `generate_pdf` (stored, linked, provenance) |
| "Store this as part of the case" | The plain-text file-id attachment convention — survives web UI, AG-UI, SignalR, **and WhatsApp**; `AttachDocumentToMatter` is sketched in `docs/DOCUMENT_TOOLS.md` |
| Ethical walls / least privilege | Pre-model-call tool filtering (LLM never sees an unauthorized tool schema), layered RBAC, per-resource ACL seam (owner/editor/viewer), runtime-editable role baselines per tenant |
| Human-in-the-loop | `RequiresApproval` gate on side-effecting tools — the approval artifact competitors bolt on |
| Audit (professional-responsibility trail) | Append-only dual-database audit of every tool call, data change, token spend |
| Client channel | WhatsApp (Meta Cloud API, HMAC-verified, inbound media → file store with provenance, JIT phone users) — a channel none of the six competitors has |
| Multi-tenancy / SaaS | Row-level isolation by construction; admin/RBAC console; token budgets; Terraform + CI/CD |

**Gaps (honest):** no Word add-in; no licensed research corpus (and the current module correctly refuses to invent citations); no background-job primitive for long-running bulk review; per-resource ACLs are a seam, not a finished feature; PDF-first (not .docx) work product; no DMS connectors.

## 6. v1 scope, in build order

Bias: features the platform makes cheap first; the one new platform primitive (background jobs) deferred to the single feature that needs it.

| # | Feature | Composes (existing Plenipo primitive) | Effort |
|---|---|---|---|
| 1 | **Matter entity + live Matters tab** (create/list; retire placeholder) | Module DbContext + migrations (Finance/Nutrition pattern); tenant query filters; `TabDescriptor` DataEndpoint/Columns; `create_matter` (RequiresApproval) / `list_matters` tools | Medium |
| 2 | **`attach_document_to_matter`** + per-matter doc list | `StoredFile`/`IFileStore` + chat-attachment file-id convention + the exact sketch in DOCUMENT_TOOLS.md + HITL gate | Small |
| 3 | **Matter Q&A with citations** (every claim cited to file id/page) | `read_document` chained over matter files; citation format via `ModuleManifest.AgentInstructions`; audit built in | Medium |
| 4 | **Tenant clause library + firm playbook** (persisted, admin-editable) | `LegalCatalog` becomes seed data; `legal:admin` + permission-gated CRUD; approval-gated writes | Small |
| 5 | **Drafting to work product** (NDA/engagement letter from templates + matter facts → stored PDF on the matter) | `draft_clause` + `generate_pdf` + `attach_document_to_matter` chained in one turn | Small |
| 6 | **Playbook contract review** (red-flag memo per contract, saved to matter) | `read_document` + playbook rows + `generate_pdf`; approval before storing | Medium |
| 7 | **Bulk review table** (docs × questions across a matter, exportable) | `read_document` loop + server-driven grid + token budgets; **needs a background-job seam** (the new platform primitive) | Large |
| 8 | **Guided workflows** (NDA review, diligence checklist, timeline extraction) | `AgentInstructions` + `SuggestedPrompts` + composition of tools 1–7 + HITL approvals | Medium |
| 9 | **WhatsApp client intake** (client sends contract → lands on their matter → review runs) | WhatsApp channel (media → file store, JIT users — already E2E) + `attach_document_to_matter` | Small |
| 10 | **Matter-level access control** (ethical walls, fail closed) + per-matter audit view | Per-resource ACL seam + `IAuthorizationPolicyProvider` policies + audit DB filtered by matter | Medium |

**Deliberately out of v1** (post-v1 roadmap, in rough order): Word add-in (or ride Word's own AI); .docx work product; licensed research grounding (vLex-class API); DMS connectors (iManage/NetDocuments); external matter-ID sync to PM systems; client portal (WhatsApp is our wedge instead). **Never build:** billing, trust accounting, docketing rules, conflicts administration.

## 7. Product naming

"Plenipo for Lawyer" is uncommercial. Candidates (collision-checked July 2026 where noted):

| Name | Rationale | Collision status |
|---|---|---|
| **EnBanc** | The full bench sitting together — collective judgment, thoroughness | **Clean(est)** — only EnBanc Equities, a judgment-finance firm, different class |
| **AdLitem** | "For the suit" — an agent appointed to act within one matter = literally our matter-scoped security model | **Clean** — only small EU litigation boutiques use the term; no software mark found |
| **SecondChair** | The associate at trial: AI as support, never lead counsel — good bar-ethics optics | Unverified; expect minor podcast/CLE usage — run clearance |
| **Silk** | "Taking silk" = King's Counsel; short, premium | Caution — Amazon Silk (browser) + Silk data/security startups; no legal-AI mark found |
| **Voir** | From "voir dire" — "to speak the truth"; fits the cite-everything posture | Unverified; Netflix "Voir" doc series (different class) |
| **BriefForge** | Forges briefs; pairs with NutriForge naming | Crowded — LexForge (lexforge.ai/.com), BriefCatch adjacent |
| **Obiter** | Obiter dictum — lovely word | **TAKEN** — obiter.ai (Canadian computational-law research) |

Eliminated by checks: **Chancery** (chancery.ai, UK legal AI), **Paralex** (paralex.ai), **Briefsmith** (briefsmith.ai, content marketing), **Docket** (DocketAlarm, Docket AI), **Gavel** (gavel.io), **Amicus** (Amicus Attorney), anything **Counsel** (TR CoCounsel, LexisNexis CounselLink). Known-taken per brief: Harvey, Clio, Luminance.

**Recommendation:** EnBanc or AdLitem — both verified low-collision, both genuinely legal, and AdLitem doubles as the security-model story.

## 8. Repo strategy

**Incubate here now; extract at first tagged release.** The README's end state (products are thin hosts in their own repos consuming the NuGet/npm packages) is correct, but the publishing path isn't live: no GitHub remote, publish.yml never run, 0.1.0-alpha untagged, no GitHub Packages feed. A separate repo today could only consume the local pack feed, adding repack friction during exactly the phase when v1 items 3/7/10 will expose platform gaps (citation conventions, background jobs, ACL completion) that are cheapest to fix in the same commit.

1. **Now:** grow `samples/Plenipo.Modules.Legal` in place (v1 items 1–6), package-shaped: reference only packable libraries (`Plenipo.Modules.Sdk`, `Plenipo.Application`, `Plenipo.Core`), own schema/migrations, no non-packable internals — `eng/verify-packaging.sh` keeps the boundary honest in CI.
2. **Extraction trigger:** push the remote, tag `0.1.0-alpha` (publish.yml populates the GitHub Packages feed), then create the product repo (e.g. `enbanc-app`): module library + thin branded host (`AddPlenipoModule` + `PlenipoApp` branding) + own Terraform/CI.
3. **After extraction:** re-thin the in-repo Legal sample to a demo; platform-shaped needs discovered later (background jobs, Word add-in seam, research connectors) land in the platform repo as primitives, not in the product.

## Sources

### Harvey
- https://www.harvey.ai/platform · https://www.harvey.ai/platform/vault · https://help.harvey.ai/articles/vault
- https://help.harvey.ai/articles/ask-questions-directly-in-review-tables · https://help.harvey.ai/articles/assistant-workflows
- https://www.harvey.ai/products/workflows · https://www.harvey.ai/blog/introducing-workflow-builder · https://www.harvey.ai/blog/introducing-harvey-agents
- https://www.harvey.ai/platform/knowledge · https://help.harvey.ai/release-notes/edgar-knowledge-source-enhancements
- https://www.harvey.ai/blog/lexisnexis-harvey-strategic-alliance · https://www.harvey.ai/blog/introducing-ask-lexisnexis
- https://help.harvey.ai/articles/harvey-for-word · https://www.harvey.ai/blog/improved-word-experience · https://www.harvey.ai/blog/harveys-new-drafting-tools-meet-you-where-you-work
- https://www.harvey.ai/platform/microsoft-integrations · https://help.harvey.ai/articles/editing-files-in-assistant
- https://www.harvey.ai/blog/harvey-imanage-integration · https://help.harvey.ai/articles/netdocuments-integration
- https://www.harvey.ai/platform/shared-spaces · https://www.harvey.ai/blog/shared-spaces-and-collaboration-in-harvey
- https://www.harvey.ai/blog/intapp-partners-with-harvey-bringing-ethical-wall-enforcement-directly-into-the-platform · https://www.harvey.ai/blog/long-horizon-agents-and-ethical-walls
- https://www.harvey.ai/security · https://trust.harvey.ai/ · https://www.harvey.ai/blog/security-by-design
- https://www.harvey.ai/blog/top-harvey-use-cases · https://www.harvey.ai/blog/harvey-in-practice-how-m-and-a-teams-use-harvey · https://www.harvey.ai/blog/ai-use-cases-powering-daily-legal-work · https://www.harvey.ai/blog/ai-case-management-software
- https://www.aivortex.io/legal/ai-tools/does-harvey-ai-hallucinate/ · https://legaltechnology.com/2026/01/08/harvey-to-build-memory-for-context-retention-across-matters/ · https://www.harvey.ai/blog/the-brief-april-2026
- https://www.msba.org/site/site/content/News-and-Publications/News/General-News/An_Overview_of_Harvey_AIs_Features_for_Lawyers.aspx

### Competitors
- https://legal.thomsonreuters.com/en/products/cocounsel-legal · https://www.thomsonreuters.com/en-us/posts/innovation/cocounsel-legal-june-2026-releases/ · https://www.thomsonreuters.com/en-us/posts/innovation/the-next-generation-of-cocounsel-legal-is-here-and-early-access-starts-now/ · https://www.lawnext.com/2026/06/thomson-reuters-opens-early-access-to-the-next-generation-of-cocounsel-legal-saying-beta-users-fing-loved-the-product.html · https://www.thomsonreuters.com/en/press-releases/2026/january/thomson-reuters-expands-cocounsel-legal-to-uk-continuing-its-transformation-of-legal-work-with-agentic-ai-innovation
- https://legora.com/product · https://legora.com/product/word-add-in · https://legora.com/product/workflows · https://www.legaltechnologyhub.com/vendors/legora/ · https://gc.ai/blog/legora-legal-ai-review
- https://spellbook.com/ · https://spellbook.com/features/review · https://www.legaltechnologyhub.com/vendors/spellbook/ · https://gc.ai/blog/spellbook-legal-ai-review · https://lawyerist.com/reviews/artificial-intelligence-in-law-firms/spellbook-review-artificial-intelligence-for-lawyers/
- https://www.luminance.com/ · https://www.luminance.com/press/luminance-enhances-the-legal-industrys-only-100-ai-autonomous-contract-negotiation-tool-to-show-the-why-behind-every-decision-and-opens-it-to-the-entire-enterprise/ · https://www.aivortex.io/legal/ai-tools/luminance-standalone/ · https://www.lawyersweekly.com.au/biglaw/43999-luminance-upgrades-world-first-ai-powered-contract-negotiation-tool
- https://robinai.com/ · https://robinai.com/news-and-resources/robin-university/word-add-in-an-intelligent-ai-sidekick-for-contract-review · https://www.robinai.com/post/robin-ai-launches-word-addin · https://legaltechnology.com/2026/01/12/microsoft-hires-raft-of-robin-ai-engineers-to-bolster-its-word-team/ · https://agenticcontractreview.com/vs-robin-ai/
- https://www.paxton.ai/ · https://www.paxton.ai/research · https://www.paxton.ai/drafting · https://www.paxton.ai/post/introducing-the-paxton-ai-citator-setting-new-benchmarks-in-legal-research · https://www.lawnext.com/2024/07/paxton-ai-releases-benchmarking-data-showing-94-accuracy-of-its-legal-research-tool-also-releases-new-confidence-indicator-feature.html · https://lawyerist.com/reviews/artificial-intelligence-in-law-firms/paxton-ai-review-artificial-intelligence-for-lawyers/

### Practice management & the boundary
- https://www.clio.com/manage/ · https://help.clio.com/hc/en-us/articles/9285920226075-Clio-Manage-Matters-Overview · https://lawyerist.com/reviews/law-practice-management-software/clio/ · https://www.clio.com/grow/ · https://www.clio.com/features/online-intake-forms/ · https://www.clio.com/about/press/clio-signs-definitive-agreement-to-acquire-vlex/
- https://www.smokeball.com/ · https://lawtoolbox.com/smokeball/ · https://support.smokeball.com/hc/en-us/articles/6112123480983-Save-a-Conflict-Check-Report
- https://www.actionstep.com/conflict-check/
- https://www.mycase.com/products/legal-ai-software/ · https://lawyerist.com/reviews/artificial-intelligence-in-law-firms/mycase-iq-review-artificial-intelligence-for-lawyers/ · https://supportcenter.mycase.com/en/articles/9370197-8am-iq-faqs
- https://www.lawnext.com/2026/03/exclusive-smokeball-and-thomson-reuters-partner-to-integrate-cocounsel-legal-ai-with-practice-management-platform.html · https://www.thomsonreuters.com/en-us/posts/innovation/a-legal-partner-ecosystem-the-profession-demands/

### Naming collision checks (July 2026)
- https://obiter.ai/ (taken — Canadian computational-law research) · https://paralex.ai/ (taken — AI legal support) · https://chancery.ai/ (taken — UK legal document intelligence) · https://www.briefsmith.ai/ (taken — content-marketing tool) · https://lexforge.ai/ and https://www.lexforge.com/ (crowded) · https://www.businesssoftwarehelp.com/solutioneer/enbanc-equities-llc (EnBanc: only non-legal-tech use found) · https://www.adlitem.law/en (AdLitem: only small law firms use the term)