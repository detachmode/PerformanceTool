# 0051. Assistant module: projects, ports, own agent loop, tools

- Status: Proposed
- Date: 2026-11-21

## What
Three projects (brain 0047):
- `src/Beesharp.Ai.Contracts`: wire DTOs only, no dependencies. A turn is a list of items (text, tool activity chip, view card),
  the changed areas and an optional error. View cards carry the query behind them plus a small preview (brain 0049).
- `src/Beesharp.Ai` (Core + Contracts + Microsoft.Extensions.AI 11.0.1 + .OpenAI, ASP.NET shared framework for the endpoints):
  `AssistantOptions` (section `AI`), `AddBeesharpAi` / `MapBeesharpAi`, `AssistantService` (the agent), `AssistantSessions`,
  `AssistantToolbox` (the tools), `Ports/`.
- `src/Beesharp/Ai/*Adapter.cs` (host): implement the ports over `PlanWorkspace`, `RunManager`, `IRunStorage`, `EnvironmentStore`,
  `ScriptScenarioStore`, `PreflightStore`.

Decisions:
1. **Ports instead of the host.** `IPlanWorkspacePort`, `IRunPort`, `IRunHistoryPort`, `IEnvironmentPort`, `IScriptPort` use 0-based indexes like the
   app; the tools translate to the 1-based numbers people see (entry 1, bee 12 = B12). The module is tested with fake ports.
2. **Our own tool loop** on `IChatClient`, without `FunctionInvokingChatClient`. Each tool call is recorded as a chip (name, one-line summary, input
   and output JSON), the call limit (`AI__MaxToolCalls`, default 24) is exact, and a fake `IChatClient` can script the model. With 0048 there is no
   approval step, so MEAI's approval types are not needed.
3. **Tools are methods** of `AssistantToolbox` marked `[AssistantTool("name")]` with `[Description]`s in the tool's language; `Build()` finds them by
   reflection. A tool answers with a small bounded object and tells the `TurnRecorder` what to show: `Read` (quiet chip), `Change` (chip, changed
   area) or `Show` (a card, no chip). Failures become failed chips and an `Error: ...` result, so the model can recover.
4. **Secrets.** Environment values of secret-looking keys (token, password, key, ...) are masked for the model (first two characters plus dots); a
   masked value sent back keeps the stored one. Literal secret scenario parameters are left out of plan views and diffs (`ParameterSpec.StripSecrets`).
5. **Pause is stop.** In Live mode the UI's Pause is Stop, so there is `start_run` and `stop_run` only.
6. **Bounded answers.** Lists page (default 20, max 100), log messages cut at 300 characters, scripts at 20000, timelines hold at most 60 bees.
7. **Sessions** live in memory: at most 50 (least recently used goes first), idle 4 hours, history trimmed to the last 50 messages once it passes 80
   (always restarting at a user message). One turn at a time per conversation (409 otherwise).
8. **Not configured** needs a model plus an API key or an endpoint. Status says why; chat answers 503; nothing fails at startup.

## Why
Clear boundaries and testability (brain 0047 asked for them). The own loop keeps the activity record and the call limit simple. Rejected:
automatic function invocation (hides the calls), generating tools from the REST API (wrong vocabulary, no bounding), one tool per UI button.

## Consequences
To add a tool: a method with `[AssistantTool]` in the toolbox file of its area, bounded output, a `Read` or `Change` summary. Nothing to register.
The client references only the contracts, so the trimmed WASM stays free of Microsoft.Extensions.AI.
