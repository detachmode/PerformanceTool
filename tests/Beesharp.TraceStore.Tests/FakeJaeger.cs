using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Beesharp.TraceStore.Tests;

/// <summary>
/// The subset of Jaeger 2.23's query API Beesharp uses, as measured against the real thing (brain 0065): <c>GET /api/v3/traces/{traceId}</c> answers
/// <c>{"result":{"resourceSpans":[...]}}</c> (OTLP JSON, hex IDs, nanosecond timestamps as strings, kind and status code as numbers) or
/// <c>404 {"error":{"httpCode":404,"message":"No traces found"}}</c>; <c>400</c> for a malformed ID. Counts every request.
/// </summary>
public sealed class FakeJaeger : HttpMessageHandler
{
    private readonly Dictionary<string, string> bodies = [];
    private readonly Lock sync = new();

    /// <summary>Every request as it arrived.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>When set, every request fails like a store that is not running (connection refused).</summary>
    public bool Down { get; set; }

    /// <summary>When set, every request is answered with this status (for example 401).</summary>
    public HttpStatusCode? Status { get; set; }

    /// <summary>Per trace: how many requests answer "not found" before the trace shows up (spans arrive late).</summary>
    public Dictionary<string, int> MissesBefore { get; } = [];

    /// <summary>The time of every request, to check the rate limit.</summary>
    public List<long> Ticks { get; } = [];

    public int RequestCount { get { lock (sync) return Requests.Count; } }

    public int RequestsFor(string traceId) { lock (sync) return Requests.Count(r => r.RequestUri!.AbsolutePath.EndsWith("/" + traceId, StringComparison.Ordinal)); }

    public void AddTrace(string traceId, params TraceSpanDto[] spans)
    {
        lock (sync) bodies[traceId] = Body(spans);
    }

    /// <summary>Jaeger streams the answer in chunks (one JSON value per chunk): the body can hold several values.</summary>
    public void AddChunkedTrace(string traceId, params TraceSpanDto[][] chunks)
    {
        lock (sync) bodies[traceId] = string.Join('\n', chunks.Select(Body));
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body;
        string id;
        lock (sync)
        {
            Requests.Add(request);
            Ticks.Add(System.Diagnostics.Stopwatch.GetTimestamp());
            if (Down) throw new HttpRequestException("Connection refused", new System.Net.Sockets.SocketException(61));
            if (Status is { } status) return Task.FromResult(new HttpResponseMessage(status));
            id = request.RequestUri!.AbsolutePath.Split('/').Last();
            if (id.Length != 32) return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"error":{"httpCode":400,"message":"malformed parameter trace_id: invalid length for TraceID"}}"""));
            if (MissesBefore.TryGetValue(id, out var misses) && misses > 0)
            {
                MissesBefore[id] = misses - 1;
                return Task.FromResult(Json(HttpStatusCode.NotFound, """{"error":{"httpCode":404,"message":"No traces found"}}"""));
            }
            bodies.TryGetValue(id, out body);
        }
        return Task.FromResult(body is null
            ? Json(HttpStatusCode.NotFound, """{"error":{"httpCode":404,"message":"No traces found"}}""")
            : Json(HttpStatusCode.OK, body));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The OTLP JSON of a set of spans, grouped by service like Jaeger does.</summary>
    public static string Body(IEnumerable<TraceSpanDto> spans)
    {
        var resourceSpans = new JsonArray();
        foreach (var service in spans.GroupBy(s => s.Service))
        {
            var jsonSpans = new JsonArray();
            foreach (var s in service)
            {
                var attrs = new JsonArray();
                foreach (var (k, v) in s.Attributes ?? new Dictionary<string, string>())
                    attrs.Add(new JsonObject { ["key"] = k, ["value"] = new JsonObject { ["stringValue"] = v } });
                attrs.Add(new JsonObject { ["key"] = "@jaeger@warnings", ["value"] = new JsonObject { ["arrayValue"] = new JsonObject { ["values"] = new JsonArray(new JsonObject { ["stringValue"] = "clock skew" }) } } });
                var span = new JsonObject
                {
                    ["traceId"] = s.TraceId,
                    ["spanId"] = s.SpanId,
                    ["name"] = s.Name,
                    ["kind"] = s.Kind switch { "server" => 2, "client" => 3, "producer" => 4, "consumer" => 5, _ => 1 },
                    ["startTimeUnixNano"] = s.StartUnixNanos.ToString(),
                    ["endTimeUnixNano"] = s.EndUnixNanos.ToString(),
                    ["attributes"] = attrs,
                    ["status"] = s.IsError ? new JsonObject { ["code"] = 2, ["message"] = s.StatusMessage } : new JsonObject(),
                };
                if (s.ParentSpanId is not null) span["parentSpanId"] = s.ParentSpanId;
                jsonSpans.Add(span);
            }
            resourceSpans.Add(new JsonObject
            {
                ["resource"] = new JsonObject
                {
                    ["attributes"] = new JsonArray(
                        new JsonObject { ["key"] = "service.name", ["value"] = new JsonObject { ["stringValue"] = service.Key } },
                        new JsonObject { ["key"] = "service.instance.id", ["value"] = new JsonObject { ["stringValue"] = "abc" } }),
                },
                ["scopeSpans"] = new JsonArray(new JsonObject { ["scope"] = new JsonObject { ["name"] = "test" }, ["spans"] = jsonSpans }),
            });
        }
        return new JsonObject { ["result"] = new JsonObject { ["resourceSpans"] = resourceSpans } }.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }
}
