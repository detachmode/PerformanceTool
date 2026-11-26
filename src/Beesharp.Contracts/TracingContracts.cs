namespace Beesharp.Contracts;

// /api/tracing/* (brain 0063).

/// <summary>UI configuration for distributed tracing exposed by the host.</summary>
/// <param name="TraceUrlTemplate">
/// Optional URL template with <c>{traceId}</c> (and optional <c>{spanId}</c>) placeholders for opening traces in an external UI (e.g. Jaeger or Kibana),
/// or <see langword="null"/> when none is configured on the server.
/// </param>
public sealed record TracingUiConfig(string? TraceUrlTemplate);
