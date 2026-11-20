# 0015. Blazor WebAssembly client + REST API server; fake-data demo on GitHub Pages

- Status: Accepted (user decision, not implemented)
- Date: 2026-11-20
- Supersedes the UI part of 0001 (interactive **server** rendering). The 0001 reasoning that **load is generated
  on the server** still holds.

## What
- The UI becomes a **Blazor WebAssembly** app that runs in the browser.
- The server becomes an ASP.NET Core host with **REST APIs** (and the load runner). The WebAssembly UI and coding
  **agents use the same APIs**, e.g. so an agent can inspect the **currently open run**.
- The WebAssembly app is published to **GitHub Pages with fake data** (the fake run simulator runs in the browser), so
  the user can try the UX without a server.

## Why
- REST APIs are needed anyway for agents (0009). A WebAssembly UI forces the UI onto those same APIs, so there is
  one source of truth and agents can see everything the UI sees.
- A static WebAssembly build can be hosted for free on GitHub Pages, which makes UX testing of the drafts easy.

## Consequences
- The UI talks to data through an abstraction: a fake, in-browser implementation for Pages and drafts, and an HTTP
  implementation against the server API later.
- Load generation stays on the server. Browsers can't do it reliably (CORS, connection limits).
- The generated parameter editor (0007) must work in the browser. Trimming must keep the parameter types'
  metadata.
- Until the HTTP data source and API exist, the UI only shows fake data (fits the current drafts phase).
