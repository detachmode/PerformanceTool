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
    Span.Types.SpanKind Kind, double DurationMs, IReadOnlyDictionary<string, string> Attributes)
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
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        receiver = new OtlpReceiver(app, address.TrimEnd('/'));
        return receiver;
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
                        span.Attributes.ToDictionary(a => a.Key, a => Text(a.Value))));
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
