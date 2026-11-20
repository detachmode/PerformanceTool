@AGENTS.md

## How we work in Claude sessions
- The main chat is for **brainstorming and recording decisions** with the user (`docs/brain/`, `docs/plan.md`).
- **Implementation runs in background agents** (model: Sonnet 5.5), each in its own git worktree, so the chat
  stays free. The main session briefs them (they start cold: point them to `AGENTS.md`, the brain records and the
  plan items), reviews their result, merges it into the working branch, pushes, and shows the user screenshots.
- **The user guides the decisions.** Propose little, don't decide on the user's behalf, and ask **one question at a
  time**. Record Claude-only ideas as `Proposed`, never `Accepted`.
- **Current phase: UI drafts with fake data** (see `docs/plan.md`). Show the user screenshots after every draft iteration.
- **Keep a background agent busy only with steps the user agreed on** (brain 0020: small steps, one feature at a time). Keep the queue in `docs/plan.md` ("Agent queue") and start the next item as soon as one finishes. User feedback goes to the front of the queue.
- **Use the tool's own language** (`docs/context.md`), not standard load-testing vocabulary or metrics, in UI, docs and agent briefs.
- **Calm UI (brain 0016):** show less, reveal on demand. Every agent brief for UI work must include this principle.
- **Screenshots:** never overwrite old screenshot files; use a new folder per set and commit-pinned links in PRs (see `AGENTS.md`).
- **Always open a PR when a step is done:** after merging an agent's finished work into the working branch, open a PR to `main` (screenshots in a new folder, commit-pinned links) and watch its CI. If a PR from the branch is already open, add the step to it and update its description.
