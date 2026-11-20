# 0016. Calm UI: show less, reveal on demand

- Status: Accepted (user principle)
- Date: 2026-11-20

## What
The UI currently has the same problem as similar tools: **too much information, which overloads the user's brain.**
From now on:
- Every view shows only what's needed to answer its main question at a glance. Everything else is **revealed on
  demand** (hover, click, expand, a secondary tab).
- Fewer numbers, fewer chips, fewer borders and boxes. Prefer one clear headline over many equal-weight figures.
- Statistics are secondary and compact (see 0012 for the popup, where the logs are the focus).
- When adding something to a view, ask what can be removed or hidden instead.

## Why
The user wants a tool that is pleasant and quick to read, distinct from typical load-testing dashboards that show
everything at once. Our own language and focus (0011, 0012, `docs/context.md`) should not be buried under metrics.

## Consequences
- Draft reviews check for overload: is each element needed at first sight? If not, hide it behind interaction.
- This refines 0006 ("everything clickable and inspectable"): inspectable doesn't mean visible all the time.
