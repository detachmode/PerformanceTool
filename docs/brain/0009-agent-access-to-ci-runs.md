# 0009. Agents can inspect CI performance runs: run bundles + one API/CLI (+ MCP)

- Status: Proposed (Claude's proposal, not decided by the user). Azure DevOps part decided in 0010.
- Date: 2026-11-20

## What
Performance runs happen in CI/CD (Azure DevOps first, GitHub Actions second) on short-lived agents. A
coding agent must be able to find and inspect such a run later, either from a harness via the CLI or directly
over HTTP with an API key.

**1. Run bundle: the unit of exchange.** Every run (UI, CLI or CI) writes a self-describing, versioned folder:

| File | Purpose |
|---|---|
| `manifest.json` | `schemaVersion`, run id (ULID), tool version, start/end, status, the plan (secrets stripped), thresholds and their verdicts, **CI context** (provider, org/project/repo, branch, commit, build id + URL, PR number), user tags (e.g. `env=staging`) |
| `summary.json` | overall and per-scenario stats (count, errors, dropped, target vs actual rate, min/avg/p50/p90/p95/p99/max), status-code histogram, **error groups** (message, count, sample sequence numbers), per-second time series |
| `results.ndjson(.gz)` | one line per execution (seq, entry, start offset, elapsed, status, bytes, error). Appended live, so it is crash-safe, streamable and easy to `jq`/`grep` |
| `summary.md` | short, LLM- and human-friendly report: verdict first, then the notable facts, then how to drill down (exact CLI commands) |
| `junit.xml` | one test case per threshold, for native CI test tabs |

**2. Three ways the bundle travels (all optional, combinable):**
- **CI artifact**: the pipeline publishes the bundle (`PublishPipelineArtifact` / `actions/upload-artifact`),
  JUnit goes to the test tab, and `summary.md` goes to the build summary (`##vso[task.uploadsummary]` /
  `$GITHUB_STEP_SUMMARY`) and as a PR comment. The comment includes the run id and the command to inspect it.
- **Central results server**: the PerformanceTool web app, deployed (e.g. Azure Container Apps / App Service),
  exposes a REST API. CI **pushes** the bundle with an API key, and agents **query** it with a read key. The UI
  shows CI runs in its history. Storage is files first, then Azure Blob Storage (bundles are just files).
- **Remote execution**: the pipeline asks the server to run a preset (`POST /api/runs`) and waits, for
  when load must come from a fixed location or a bigger machine than the CI agent.

**3. One command surface for agents.** The same `perftool` commands work against the local data folder or
against a server (`--server` / `PERFTOOL_SERVER` + `PERFTOOL_API_KEY`):
`runs list` (filter by commit, branch, PR, build, tag, preset, status), `runs show <id>` (summary),
`runs executions <id>` (filter by scenario, status, error, min latency, time window, limit), `runs errors <id>`,
`runs compare <a> <b>` / `--baseline main`, `runs pull <id>` (download the bundle), `runs pull --from azdo|github --build <id>`
(fetch the CI artifact via `az`/`gh`), and `run --push` / `run --remote`. Every command has `--json` with
documented schemas.
An optional **MCP server** (`perftool mcp`) wraps the same API as tools (`list_runs`, `get_run_summary`,
`query_executions`, `compare_runs`, `list_scenarios`, `start_run`) so harnesses can use it natively.

**4. Auth.** API keys with scopes (`runs:write` for pipelines, `runs:read` for agents, `runs:execute` for remote
runs). Keys are stored hashed and sent as `Authorization: Bearer <key>`. In Azure DevOps a key lives in a
(Key Vault-backed) secret variable group; in GitHub it's a repository/environment secret. Later: OIDC / workload
identity federation (Entra ID, GitHub OIDC) instead of static keys.

## Why
- CI agents disappear, so the evidence has to leave the agent in a form that survives and explains itself.
  A versioned bundle works for every transport: artifact, server, local disk.
- Artifacts alone aren't enough: they expire, need CI-specific credentials and tools, and are scattered across
  builds, so there's no history or comparison. A central server gives history, queries and baselines, but CI
  must still work without one. Hence both.
- Agents work best with **progressive disclosure**: a small summary first, then targeted queries (error
  groups, slowest executions, one scenario), instead of a dump of 100k lines. NDJSON plus server-side
  filters make that cheap.
- Linking runs to commit/PR/build lets an agent answer "did my change make it slower?" without being told
  which run to look at.
- One command surface (local or server) means agents, humans and pipelines share the same mental model and docs.

## Consequences
- Bundle schemas are a public contract: version them (`schemaVersion`), document them, change them compatibly.
- The UI must persist runs as bundles too (same writer), so UI, CLI and CI runs look identical.
- Secrets never enter a bundle (0007 stripping applies to the manifest's plan).
- CI context is auto-detected from environment variables (Azure DevOps `BUILD_*`/`SYSTEM_*`, GitHub `GITHUB_*`).
- Large runs: gzip `results.ndjson`, cap the time series resolution, and let the API filter and paginate.
- Ships with an Azure DevOps YAML template and a GitHub composite action/reusable workflow, plus a container image.
