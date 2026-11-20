# 0010. Azure DevOps: run in the pipeline, store results as two artifacts

- Status: Accepted (user decision, not implemented)
- Date: 2026-11-20
- Narrows 0009 for Azure DevOps. The rest of 0009 (central server, Blob storage, remote runs, MCP) is still only
  a proposal; nothing there is decided yet.

## What
- In Azure DevOps the tool **runs inside the pipeline** (the CLI on the pipeline agent).
- Results are stored as **pipeline artifacts**, two per run:
  1. **Full artifact**: everything. All logs and details of the run.
  2. **Summary artifact**: small. Only the overview and data that takes little space.

## Why
- Running in the pipeline needs no extra infrastructure.
- Artifacts are the native way to keep results with the build that produced them.
- Splitting into a big and a small artifact means the overview is cheap and quick to fetch (by people, tools or
  agents) without downloading all the details. The full artifact is there when someone needs to dig deeper.

## Open (to decide with the user)
- Exactly which files/data go into the summary artifact vs the full artifact.
- Artifact names and retention.
- How results are shown in Azure DevOps (build summary, test tab, PR comment).
