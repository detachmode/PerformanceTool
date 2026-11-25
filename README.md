# Beesharp

A load-testing tool with a web UI: scenarios are C# (built in or uploaded as scripts), runs are stored in DuckDB.

## Run with Docker Compose

The image is published to the GitHub Container Registry for `linux/amd64` and `linux/arm64` (see `.github/workflows/ci.yml`), gated by unit/integration tests and an end-to-end smoke test on both architectures.
It runs on Linux, and on Windows and macOS through Docker Desktop.

Create a `compose.yaml`:

```yaml
services:
  beesharp:
    image: ghcr.io/detachmode/beesharp:latest
    restart: unless-stopped
    ports:
      - "5281:8080"            # UI and API: http://localhost:5281
    volumes:
      - beesharp-data:/app/data                   # stored runs (DuckDB)
      - beesharp-presets:/app/presets             # saved presets
      - beesharp-environments:/app/environments   # environments
      - beesharp-scripts:/app/scripts             # scenario, preflight and shutdown scripts
      # Optional: add a script from this repo's examples/ folder (read-only).
      - ./examples/http-health-check.cs:/app/scripts/http-health-check.cs:ro
    environment:
      # Optional: the AI assistant. Without AI__Model the chat icon stays hidden.
      # AI__Model: gpt-4.1-mini
      # AI__ApiKey: ${OPENAI_API_KEY}
      # AI__Endpoint: http://ollama:11434/v1   # any OpenAI-compatible server
      # Optional: send the actors' traces (actor, step and call spans) to an OpenTelemetry collector, e.g. Jaeger. Off without it.
      # Tracing__OtlpEndpoint: http://jaeger:4317   # gRPC; for http/protobuf use :4318 and Tracing__Protocol: http/protobuf
      # Optional: keep runs in memory only (nothing written to /app/data).
      # Storage__Provider: Memory

volumes:
  beesharp-data:
  beesharp-presets:
  beesharp-environments:
  beesharp-scripts:
```

Then:

```sh
docker compose up -d
docker compose logs -f beesharp
```

Open http://localhost:5281.

### Notes

- **Port:** the container listens on plain http on port `8080`. Put TLS on a reverse proxy in front of it
  (Caddy, Traefik, nginx, cloudflared); the app honours `X-Forwarded-For` and `X-Forwarded-Proto`.
- **Volumes:** named volumes start with the files that ship in the image (built-in example scripts, presets,
  an environment) and keep what you add in the UI across updates. Mounting a host folder over `/app/scripts`
  instead hides the shipped scripts, including the `preflight/` and `shutdown/` subfolders.
- **Own scripts:** mount single `.cs` files into `/app/scripts/` as shown above, or upload them in the editor.
  Scripts are compiled when the container starts.
- **Settings:** every key of `src/Beesharp/appsettings.json` can be set as an environment variable, with `__`
  between sections, e.g. `Storage__DuckDbPath: data/runs.duckdb`.
- **Update:** `docker compose pull && docker compose up -d`. Pin a version (`:1.2.3`) instead of `latest` to
  update on your own schedule.

## Command-line tool (`beesharp`)

Install `Beesharp.Cli` as a global .NET tool (requires the .NET 11 SDK/runtime):

```bash
dotnet tool install --global Beesharp.Cli
```

Update an existing installation:

```bash
dotnet tool update --global Beesharp.Cli
```

Once installed, the command is `beesharp`:

```bash
# Run a preset headlessly (e.g. in CI)
beesharp run --preset "Black Friday" --env Staging --root ./load-tests --export run.zip

# Inspect an exported run bundle offline or query a live server
beesharp runs summary run.zip
beesharp runs list --url http://localhost:5281
```

See [docs/cli.md](docs/cli.md) for all subcommands, options, exit codes, and local build instructions.

## Examples

`examples/` holds standalone .NET 11 file-based scenario scripts (`#:package Beesharp.Core@0.1.0`) and a full Aspire-style host project:

- `http-health-check.cs`: fetches one URL per run and fails on an error status or a slow answer. A plain scenario without steps.
- `checkout-steps.cs`: C# 15 `union` step-machine scenario (`Log in → Search → Open product → Add to cart → Pay`) with dynamic repeats, retries, and simulated calls.
- `host-app/HostExample.csproj`: Aspire-style `Beesharp.Host` project with embedded Blazor UI, plain .NET DI (`DemoTokenCache`), a compiled `Scenario<TParams, TStep>`, and a strongly typed default preset (`dotnet run --project examples/host-app` for the UI, or `dotnet run --project examples/host-app -- run --preset Smoke --duration 5` headless).
