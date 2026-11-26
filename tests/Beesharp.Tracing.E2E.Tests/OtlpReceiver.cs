using Google.Protobuf;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Trace.V1;

namespace Beesharp.Tracing.E2E.Tests;

/// <summary>One exported span as the test saw it, with the IDs as lower-case hex (like in Beesharp's storage).</summary>
public sealed record ReceivedSpan(
    string Service, string Name, string TraceId, string SpanId, string ParentSpanId,
    Span.Types.SpanKind Kind, double DurationMs, IReadOnlyDictionary<string, string> Attributes, long StartUnixNanos = 0, bool IsError = false)
{
    public bool HasParent => ParentSpanId.Length > 0;
}

/// <summary>
/// A minimal OTLP/HTTP trace receiver (<c>POST /v1/traces</c>, protobuf), the stand-in for the collector in
/// <c>examples/tracing/compose.yaml</c>: every service under test sends its spans here, the test reads them back.
/// </summary>
public sealed class OtlpReceiver : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly List<ReceivedSpan> spans = [];
    private readonly object gate = new();

    private OtlpReceiver(WebApplication app, string endpoint)
    {
        this.app = app;
        Endpoint = endpoint;
    }

    /// <summary>Base address to hand to <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (http, loopback, the port Kestrel picked).</summary>
    public string Endpoint { get; }

    public static async Task<OtlpReceiver> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        OtlpReceiver? receiver = null;
        app.MapPost("/v1/traces", async (HttpRequest request) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body);
            var message = ExportTraceServiceRequest.Parser.ParseFrom(body.ToArray());
            receiver!.Add(message);
            return Results.Bytes(new ExportTraceServiceResponse().ToByteArray(), "application/x-protobuf");
        });
        // The subset of Jaeger's query API Beesharp fetches traces with (brain 0065): the receiver doubles as the trace store the run's environment names.
        app.MapGet("/api/v3/traces/{traceId}", (string traceId) =>
        {
            var spans = receiver!.Snapshot().Where(s => s.TraceId == traceId).ToList();
            return spans.Count == 0
                ? Results.Json(new { error = new { httpCode = 404, message = "No traces found" } }, statusCode: 404)
                : Results.Text(JaegerJson(spans), "application/json");
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver = new OtlpReceiver(app, address.TrimEnd('/'));
        return receiver;
    }

    /// <summary>Jaeger's answer for a trace: <c>{"result":{"resourceSpans":[...]}}</c>, OTLP JSON with hex IDs and nanosecond timestamps as strings.</summary>
    private static string JaegerJson(IReadOnlyList<ReceivedSpan> spans)
    {
        var resourceSpans = spans.GroupBy(s => s.Service).Select(service => new
        {
            resource = new { attributes = new[] { new { key = "service.name", value = new { stringValue = service.Key } } } },
            scopeSpans = new[]
            {
                new
                {
                    scope = new { name = "e2e" },
                    spans = service.Select(s => new
                    {
                        traceId = s.TraceId,
                        spanId = s.SpanId,
                        parentSpanId = s.ParentSpanId,
                        name = s.Name,
                        kind = (int)s.Kind,
                        startTimeUnixNano = s.StartUnixNanos.ToString(),
                        endTimeUnixNano = (s.StartUnixNanos + (long)(s.DurationMs * 1_000_000)).ToString(),
                        attributes = s.Attributes.Select(a => new { key = a.Key, value = new { stringValue = a.Value } }).ToArray(),
                        status = s.IsError ? (object)new { code = 2, message = "error" } : new { },
                    }).ToArray(),
                },
            },
        }).ToArray();
        return System.Text.Json.JsonSerializer.Serialize(new { result = new { resourceSpans } });
    }

    /// <summary>A copy of everything received so far.</summary>
    public IReadOnlyList<ReceivedSpan> Snapshot()
    {
        lock (gate) return [.. spans];
    }

    private void Add(ExportTraceServiceRequest message)
    {
        var received = new List<ReceivedSpan>();
        foreach (var resource in message.ResourceSpans)
        {
            var service = resource.Resource?.Attributes.FirstOrDefault(a => a.Key == "service.name")?.Value?.StringValue ?? "(no service.name)";
            foreach (var scope in resource.ScopeSpans)
                foreach (var span in scope.Spans)
                    received.Add(new ReceivedSpan(
                        service,
                        span.Name,
                        Hex(span.TraceId),
                        Hex(span.SpanId),
                        Hex(span.ParentSpanId),
                        span.Kind,
                        (span.EndTimeUnixNano - span.StartTimeUnixNano) / 1_000_000.0,
                        span.Attributes.ToDictionary(a => a.Key, a => Text(a.Value)),
                        (long)span.StartTimeUnixNano,
                        span.Status?.Code == Status.Types.StatusCode.Error));
        }
        lock (gate) spans.AddRange(received);
    }

    private static string Hex(ByteString bytes) => Convert.ToHexString(bytes.Span).ToLowerInvariant();

    private static string Text(AnyValue value) => value.ValueCase switch
    {
        AnyValue.ValueOneofCase.StringValue => value.StringValue,
        AnyValue.ValueOneofCase.BoolValue => value.BoolValue ? "true" : "false",
        AnyValue.ValueOneofCase.IntValue => value.IntValue.ToString(),
        AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
