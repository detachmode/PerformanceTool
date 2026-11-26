using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Beesharp.Services.TraceStore;

/// <summary>
/// Fetches traces from Jaeger v2 (brain 0065): <c>GET /api/v3/traces/{traceId}</c> on the query port (16686), which answers OTLP JSON
/// (<c>{"result":{"resourceSpans":[...]}}</c>) with every span of the trace across all services, or <c>404 {"error":{"httpCode":404,"message":"No traces found"}}</c>.
/// One request per trace: the multi-ID endpoint of Jaeger v1 (<c>/api/traces?traceID=a&amp;traceID=b</c>) does not exist in Jaeger 2.23, and the v3 search cannot
/// filter by a list of trace IDs. Verified against Jaeger 2.23 (see docs/brain/0065).
/// </summary>
public sealed class JaegerTraceStore : ITraceStore, IDisposable
{
    private const int MaxAttributes = 40;
    private const int MaxAttributeLength = 2000;

    private readonly HttpClient http;

    /// <summary>Creates the store for a Jaeger query URL.</summary>
    /// <param name="settings">The URL and optional token.</param>
    /// <param name="handler">The HTTP handler (tests pass a fake Jaeger); a default handler with a 5 s connect timeout when <see langword="null"/>.</param>
    public JaegerTraceStore(TraceStoreSettings settings, HttpMessageHandler? handler = null)
    {
        Url = settings.Url.TrimEnd('/');
        http = new HttpClient(handler ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }, disposeHandler: true)
        {
            BaseAddress = new Uri(Url + "/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrEmpty(settings.Token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Token);
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>The query URL.</summary>
    public string Url { get; }

    /// <inheritdoc />
    public string Kind => TraceStoreKinds.Jaeger;

    /// <inheritdoc />
    public string Description => $"Jaeger at {Url}";

    /// <inheritdoc />
    public async Task<TraceLookup> GetTraceAsync(string traceId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var path = $"api/v3/traces/{Uri.EscapeDataString(traceId)}?start_time={Rfc3339(from)}&end_time={Rfc3339(to)}";
        try
        {
            using var response = await http.GetAsync(path, HttpCompletionOption.ResponseContentRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return TraceLookup.Missing;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return TraceLookup.Failed($"{Description} refused the request (HTTP {(int)response.StatusCode}); check TraceStore.Token");
            if (!response.IsSuccessStatusCode)
                return TraceLookup.Failed($"{Description} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

            var body = await response.Content.ReadAsByteArrayAsync(ct);
            var spans = Parse(body, out var error);
            if (error is not null) return error.Value.Code == 404 ? TraceLookup.Missing : TraceLookup.Failed($"{Description} answered: {error.Value.Message}");
            return spans is null ? TraceLookup.Failed($"{Description} answered something that is not OTLP JSON")
                : spans.Count == 0 ? TraceLookup.Missing
                : new TraceLookup(TraceLookupOutcome.Found, spans);
        }
        catch (HttpRequestException ex)
        {
            return TraceLookup.Unreachable($"{Description} could not be reached: {ex.InnerException?.Message ?? ex.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return TraceLookup.Unreachable($"{Description} did not answer in time");
        }
        catch (JsonException ex)
        {
            return TraceLookup.Failed($"{Description} answered unreadable JSON: {ex.Message}");
        }
    }

    private static string Rfc3339(DateTimeOffset time) =>
        Uri.EscapeDataString(time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));

    /// <summary>
    /// Reads the answer: one or more JSON values (the gRPC gateway streams chunks), each <c>{"result":{"resourceSpans":[...]}}</c> or <c>{"error":{...}}</c>.
    /// Returns the spans (null when nothing looked like OTLP), or sets <paramref name="error"/> for an error answer.
    /// </summary>
    internal static List<TraceSpanDto>? Parse(ReadOnlySpan<byte> body, out (int Code, string Message)? error)
    {
        error = null;
        List<TraceSpanDto>? spans = null;
        var seen = new HashSet<(string, string)>();
        var reader = new Utf8JsonReader(body, new JsonReaderOptions { AllowMultipleValues = true });
        while (reader.Read())
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
            {
                var code = err.TryGetProperty("httpCode", out var c) && c.TryGetInt32(out var n) ? n : 0;
                error = (code, err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "error");
                return null;
            }
            if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("resourceSpans", out var resourceSpans) || resourceSpans.ValueKind != JsonValueKind.Array)
                continue;

            spans ??= [];
            foreach (var resource in resourceSpans.EnumerateArray())
            {
                var service = ServiceName(resource);
                if (!resource.TryGetProperty("scopeSpans", out var scopes) || scopes.ValueKind != JsonValueKind.Array) continue;
                foreach (var scope in scopes.EnumerateArray())
                {
                    if (!scope.TryGetProperty("spans", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var span in list.EnumerateArray())
                        if (ReadSpan(span, service) is { } dto && seen.Add((dto.TraceId, dto.SpanId)))
                            spans.Add(dto);
                }
            }
        }
        return spans;
    }

    private static string ServiceName(JsonElement resource)
    {
        if (resource.TryGetProperty("resource", out var res) && res.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
            foreach (var a in attrs.EnumerateArray())
                if (a.TryGetProperty("key", out var k) && k.GetString() == "service.name" && a.TryGetProperty("value", out var v)
                    && v.TryGetProperty("stringValue", out var s) && s.GetString() is { Length: > 0 } name)
                    return name;
        return "unknown";
    }

    private static TraceSpanDto? ReadSpan(JsonElement span, string service)
    {
        var traceId = Id(Str(span, "traceId"), 16);
        var spanId = Id(Str(span, "spanId"), 8);
        if (traceId is null || spanId is null) return null;
        var parent = Id(Str(span, "parentSpanId"), 8);
        var start = Nanos(span, "startTimeUnixNano");
        var end = Nanos(span, "endTimeUnixNano");
        var duration = end > start ? (end - start) / 1_000_000d : 0;

        var isError = false;
        string? statusMessage = null;
        if (span.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.Object)
        {
            if (status.TryGetProperty("code", out var code))
                isError = code.ValueKind == JsonValueKind.Number ? code.TryGetInt32(out var cn) && cn == 2
                    : code.ValueKind == JsonValueKind.String && code.GetString() is { } cs && (cs.Contains("ERROR", StringComparison.OrdinalIgnoreCase) || cs == "2");
            if (status.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.String && msg.GetString() is { Length: > 0 } text)
                statusMessage = Truncate(text);
        }

        return new TraceSpanDto(
            traceId, spanId, parent, service, Str(span, "name") ?? "", SpanKind(span), start, duration, isError, statusMessage, Attributes(span));
    }

    private static string? Str(JsonElement el, string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Nanos(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var n) ? n : 0;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string? SpanKind(JsonElement span)
    {
        if (!span.TryGetProperty("kind", out var k)) return null;
        var number = k.ValueKind == JsonValueKind.Number && k.TryGetInt32(out var n) ? n : -1;
        var name = k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        return (number, name) switch
        {
            (1, _) => "internal",
            (2, _) => "server",
            (3, _) => "client",
            (4, _) => "producer",
            (5, _) => "consumer",
            (_, { } s) when s.Contains("INTERNAL", StringComparison.OrdinalIgnoreCase) => "internal",
            (_, { } s) when s.Contains("SERVER", StringComparison.OrdinalIgnoreCase) => "server",
            (_, { } s) when s.Contains("CLIENT", StringComparison.OrdinalIgnoreCase) => "client",
            (_, { } s) when s.Contains("PRODUCER", StringComparison.OrdinalIgnoreCase) => "producer",
            (_, { } s) when s.Contains("CONSUMER", StringComparison.OrdinalIgnoreCase) => "consumer",
            _ => null,
        };
    }

    /// <summary>A trace or span ID as lower-case hex: OTLP JSON uses hex; a base64 value is converted; empty means no parent.</summary>
    private static string? Id(string? raw, int bytes)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var hexLength = bytes * 2;
        if (raw.Length == hexLength && raw.All(Uri.IsHexDigit))
            return raw.All(c => c == '0') ? null : raw.ToLowerInvariant();
        try
        {
            var decoded = Convert.FromBase64String(raw);
            if (decoded.Length == bytes && decoded.Any(b => b != 0)) return Convert.ToHexStringLower(decoded);
        }
        catch (FormatException) { }
        return null;
    }

    private static IReadOnlyDictionary<string, string>? Attributes(JsonElement span)
    {
        if (!span.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Array) return null;
        var all = new List<KeyValuePair<string, string>>();
        foreach (var a in attrs.EnumerateArray())
        {
            if (!a.TryGetProperty("key", out var k) || k.GetString() is not { Length: > 0 } key || key.StartsWith("@jaeger@", StringComparison.Ordinal)) continue;
            if (!a.TryGetProperty("value", out var v) || Text(v) is not { } text) continue;
            all.Add(new(key, Truncate(text)));
        }
        if (all.Count == 0) return null;
        var result = new Dictionary<string, string>();
        foreach (var kv in all)
            if (result.Count < MaxAttributes) result[kv.Key] = kv.Value;
        return result;
    }

    private static string? Text(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var prop in value.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "stringValue": return prop.Value.GetString();
                case "intValue": return prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() : prop.Value.GetRawText();
                case "doubleValue": return prop.Value.GetRawText();
                case "boolValue": return prop.Value.GetBoolean() ? "true" : "false";
                case "arrayValue":
                    if (prop.Value.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
                        return string.Join(", ", values.EnumerateArray().Select(Text).Where(t => t is not null));
                    return "";
                case "kvlistValue": return prop.Value.GetRawText();
            }
        }
        return null;
    }

    private static string Truncate(string text) => text.Length <= MaxAttributeLength ? text : text[..MaxAttributeLength];

    /// <inheritdoc />
    public void Dispose() => http.Dispose();
}
