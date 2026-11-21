# 0047. AI assistant: server-side agent in its own module, chat side panel

- Status: Accepted (user decisions below; point 5 superseded by 0048); module layout and API shape Proposed; not implemented
- Date: 2026-11-21

## What
An **assistant** chat in the app. An AI agent built on **Microsoft.Extensions.AI** (`IChatClient`, `AIFunction`
tools, function invocation) can do whatever the user can do in the UI: read the plan, runs, bees, calls and logs,
edit the plan, use presets, environments, custom scripts and preflights, and start, pause or stop a run.

User decisions (Accepted):
1. **The agent and its tools run on the server**, against the server's own services (`RunManager`, `IRunStorage`,
   `PresetStore`, `EnvironmentStore`, `ScriptScenarioStore`, `PreflightStore`). The browser only shows the chat.
2. **The server owns the editable plan and the presets in Live mode.** Today `ApiRunSource` keeps them only in the
   browser. They move to the server (a plan workspace service plus REST, presets through `PresetStore`), so the UI
   and the agent share one source of truth and the agent's edits show up in the editor.
3. **Configured via environment variables**, the ASP.NET configuration section `AI`: `AI__Endpoint`, `AI__ApiKey`,
   `AI__Model` (and `AI__Provider`, today only `OpenAI`). Without a model the assistant is "not configured".
4. **Provider: OpenAI-compatible only** for now (`Microsoft.Extensions.AI.OpenAI`). `AI__Endpoint` points it at
   OpenAI, Azure's OpenAI-compatible `/openai/v1`, Ollama `/v1`, LM Studio, vLLM, OpenRouter, ...
5. *(Superseded by 0048: no approval.)* **Confirm destructive actions**: reading and harmless edits run immediately. Starting, stopping or pausing a run
   and deleting anything (presets, environments, scripts, preflights, scenario entries) show an Approve / Reject
   card in the chat first.
6. **Right side panel**: a chat icon in the top bar opens a panel on the right that pushes the page aside
   (full-screen sheet on mobile), so the user can watch the agent change the plan or start a run.
7. **Hidden in Fake mode**: the chat icon only shows in Live mode and only when the server reports the assistant is
   configured. The GitHub Pages demo (fake data, no server) has no chat.

Module layout (Proposed): see the agent brief in `docs/plan.md` (item R). In short:
- `src/Beesharp.Ai.Contracts`: wire DTOs of the chat endpoint, no dependencies (client and server reference it).
- `src/Beesharp.Ai`: the agent. Options, `IChatClient` factory, tools, approval flow, chat sessions,
  `AddBeesharpAi(configuration)` and `MapBeesharpAi()`. It talks to the app only through small **port
  interfaces** it defines itself (plan workspace, runs, logs, environments, scripts, ...). It never references the
  host project.
- `src/Beesharp` (host): adapters that implement the ports on the existing services.
- `src/Beesharp.Client`: the side panel (`Components/Assistant/`), talking only to the contracts over HTTP.

## Why
- The user asked for a separate module with clear boundaries and a good public API, configured from env vars.
- Server-side: the API key never reaches the browser, and the agent can later be reused for a CLI or MCP (plan
  sections 2 and 5). The cost: it can't see the fake run in the browser, which is why the chat is hidden in Fake mode.
- Rejected: an agent loop in the browser with the server only passing model calls through (sees fake data, but splits
  the agent across two places); client-side tools for the plan (complex chat protocol).
- Ports instead of referencing the host: the AI module stays testable with fakes and doesn't depend on storage or
  the runner.

## Consequences
- `ApiRunSource` changes: plan and presets come from the server, edits go back to it, and agent edits must appear
  in an open editor.
- The model's tool calls follow the tool's language (`docs/context.md`: scenario, scenario entry, bee/actor, call,
  run, environment, ...). The tool names and descriptions are the agent's vocabulary.
- Every figure the assistant mentions should be a link into the UI when possible (brain 0029). Follow-up.
- Open: chat history persistence (in memory per session for now), streaming replies, more providers.
