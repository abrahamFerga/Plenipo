---
description: >-
  Guarantee /usr/local/bin/copilot exists before the agent spawns it. Import into every workflow
  running engine:copilot; delete this file once upstream gh-aw writes that path on both branches.

# gh-aw compiles the agent command to spawn /usr/local/bin/copilot by ABSOLUTE path, but
# install_copilot_cli.sh only writes that path on its download branch. When it finds a usable build
# already in the runner toolcache it calls activate_cached_copilot_bin, which prepends the toolcache
# directory to PATH and GITHUB_PATH and returns — an absolute spawn can never see it. Which branch
# runs is decided by the runner image's cache state, so the same commit dies with a 0-second
# `spawn /usr/local/bin/copilot ENOENT` on one runner and succeeds on the next.
#
# Re-running the same installer with RUNNER_TOOL_CACHE pointed at an empty directory makes its
# toolcache scan find nothing, forcing the download branch and a real install into /usr/local/bin —
# reusing the installer's own checksum verification instead of hand-rolling a download.
#
# Covers the agent job only: pre-agent-steps is not injected into the detection job, which spawns
# the same absolute path and keeps the same exposure until upstream fixes it.
pre-agent-steps:
  - name: Ensure /usr/local/bin/copilot exists
    shell: bash
    run: |
      set -euo pipefail
      if [ -x /usr/local/bin/copilot ]; then
        echo "/usr/local/bin/copilot already present — nothing to repair"
        exit 0
      fi
      echo "::warning::copilot was activated from the runner toolcache, leaving /usr/local/bin/copilot absent; reinstalling"
      RUNNER_TOOL_CACHE="$(mktemp -d)" bash "${RUNNER_TEMP}/gh-aw/actions/install_copilot_cli.sh"
      /usr/local/bin/copilot --version
---
