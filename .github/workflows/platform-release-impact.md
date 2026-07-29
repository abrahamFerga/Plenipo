---
on:
  release:
    types: [published]
  workflow_dispatch:
    inputs:
      release_tag:
        description: Published Plenipo release tag to assess.
        required: true
        type: string
engine: copilot
timeout-minutes: 15
max-ai-credits: 160K
permissions:
  contents: read
  issues: read
tools:
  github:
    toolsets: [repos, issues]
    min-integrity: approved
network:
  allowed:
    - github
safe-outputs:
  allowed-github-references: [abrahamFerga/networthy]
  github-app:
    client-id: ${{ vars.GH_AW_ROUTER_APP_ID }}
    private-key: ${{ secrets.GH_AW_ROUTER_APP_PRIVATE_KEY }}
    owner: abrahamFerga
    repositories: [networthy]
  create-issue:
    target-repo: abrahamFerga/networthy
    labels: [platform:upgrade]
    title-prefix: "[Plenipo upgrade] "
    max: 1
---

# Route a Plenipo release impact brief to Networthy

Read `consumers.json`, the published release notes or the manually supplied release tag, and the
changes since the previous release. This workflow currently has exactly one authorized destination:
Networthy. Do nothing if the release is documentation-only or if a non-closed issue already exists
for this release tag in Networthy.

When this run was manually dispatched, assess this exact release tag: `${{ inputs.release_tag }}`.

Create one concise upgrade issue only when the release changes a public package, host seam,
authentication/authorization behaviour, migration, document/RAG behaviour, job, connector, or UI
contract that Networthy may consume. Include the release tag, affected package or source area,
concrete upgrade steps, expected verification command, compatible shim retirement work, and links to
source evidence. Do not invent breaking changes and do not file an issue merely to announce a
release. Never modify code, pull requests, releases, project fields, or any repository other than
the configured Networthy target.
