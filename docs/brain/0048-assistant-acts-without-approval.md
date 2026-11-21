# 0048. The assistant acts without approval

- Status: Accepted
- Date: 2026-11-21

## What
The assistant (brain 0047) runs every tool directly, including starting, pausing or stopping a run and deleting
presets, environments, scripts, preflights or scenario entries. There is no Approve / Reject card. The chat shows
what the agent did as activity chips. Supersedes point 5 of 0047.

## Why
User decision: the app's data is not that important, so a confirmation step only slows the user down.

## Consequences
No approval flow in `Beesharp.Ai` or the contracts (no pending approval, no approve endpoint). The activity chips are
the record of what changed and drill down to it (brain 0029).
