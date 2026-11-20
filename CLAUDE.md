@AGENTS.md

## How we work in Claude sessions
- The main chat is for **brainstorming and recording decisions** with the user (`docs/brain/`, `docs/plan.md`).
- **Implementation runs in background agents** (model: Sonnet 5.5), each in its own git worktree, so the chat
  stays free. The main session briefs them (they start cold: point them to `AGENTS.md`, the brain records and the
  plan items), reviews their result, merges it into the working branch, pushes, and shows the user screenshots.
- **The user guides the decisions.** Propose little, don't decide on the user's behalf, and ask **one question at a
  time**. Record Claude-only ideas as `Proposed`, never `Accepted`.
