using System.Globalization;

namespace Beesharp.Contracts;

/// <summary>
/// The one trace aggregation engine (brain 0065), used identically by the run storages (server) and the CLI's bundle readers (like <see cref="CallAnalytics"/>):
/// the span tree with self times, the flat bounded span list of one trace, and the hotspots per endpoint (where the time of slow calls went).
/// Pure functions over spans and call facts; no I/O.
/// </summary>
public static class TraceAnalytics
{
    /// <summary>The service name of the pseudo span for time that no service span accounts for.</summary>
    public const string OutsideService = "(outside services)";

    /// <summary>The span name of the pseudo span for time that no service span accounts for.</summary>
    public const string OutsideName = "network, queueing or client time";

    private static readonly string[] KeyAttributeNames =
    [
        "db.query.text", "db.statement", "db.system.name", "db.system", "http.request.method", "http.method", "http.route", "url.full", "http.url",
        "server.address", "peer.service", "rpc.method", "messaging.destination.name", "exception.message",
    ];

    private sealed class Node(TraceSpanDto span)
    {
        public TraceSpanDto Span { get; } = span;
        public List<Node> Children { get; } = [];
        public double SelfMs { get; set; }
        public int Depth { get; set; }
    }

    private sealed class TraceIndex
    {
        public Dictionary<string, Node> BySpanId { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<Node>> ByParent { get; } = new(StringComparer.Ordinal);
        public List<Node> Roots { get; } = [];
    }

    private static TraceIndex Index(IEnumerable<TraceSpanDto> spans)
    {
        var index = new TraceIndex();
        foreach (var span in spans)
            if (!index.BySpanId.ContainsKey(span.SpanId))
                index.BySpanId[span.SpanId] = new Node(span);

        foreach (var node in index.BySpanId.Values)
        {
            var parent = node.Span.ParentSpanId;
            if (!string.IsNullOrEmpty(parent) && !string.Equals(parent, node.Span.SpanId, StringComparison.Ordinal))
            {
                if (!index.ByParent.TryGetValue(parent, out var siblings)) index.ByParent[parent] = siblings = [];
                siblings.Add(node);
                if (index.BySpanId.TryGetValue(parent, out var parentNode))
                {
                    parentNode.Children.Add(node);
                    continue;
                }
            }
            index.Roots.Add(node);
        }

        static int Order(Node a, Node b)
        {
            var c = a.Span.StartUnixNanos.CompareTo(b.Span.StartUnixNanos);
            return c != 0 ? c : string.CompareOrdinal(a.Span.SpanId, b.Span.SpanId);
        }
        foreach (var node in index.BySpanId.Values)
        {
            node.Children.Sort(Order);
            node.SelfMs = SelfOf(node);
        }
        index.Roots.Sort(Order);
        foreach (var list in index.ByParent.Values) list.Sort(Order);

        // A cycle (corrupt data) never reaches a root: treat the first unreached span of each cycle as a root so no span is lost.
        var reached = new HashSet<Node>();
        void Mark(Node n)
        {
            var stack = new Stack<Node>();
            stack.Push(n);
            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (!reached.Add(current)) continue;
                foreach (var child in current.Children) stack.Push(child);
            }
        }
        foreach (var root in index.Roots) Mark(root);
        foreach (var node in index.BySpanId.Values.OrderBy(n => n.Span.StartUnixNanos).ThenBy(n => n.Span.SpanId, StringComparer.Ordinal))
            if (!reached.Contains(node))
            {
                index.Roots.Add(node);
                Mark(node);
            }

        return index;
    }

    /// <summary>The span's duration minus the time its children cover (overlapping children count once, parts outside the span are ignored).</summary>
    private static double SelfOf(Node node)
    {
        if (node.Children.Count == 0) return node.Span.DurationMs;
        var covered = UnionMs(node.Children.Select(c => (c.Span.StartUnixNanos, c.Span.EndUnixNanos)), node.Span.StartUnixNanos, node.Span.EndUnixNanos);
        return Math.Max(0, node.Span.DurationMs - covered);
    }

    /// <summary>Length in milliseconds of the union of the intervals, clipped to [clipStart, clipEnd].</summary>
    private static double UnionMs(IEnumerable<(long Start, long End)> intervals, long clipStart, long clipEnd)
    {
        var sorted = intervals
            .Select(i => (Start: Math.Max(i.Start, clipStart), End: Math.Min(i.End, clipEnd)))
            .Where(i => i.End > i.Start)
            .OrderBy(i => i.Start)
            .ToList();
        long total = 0, curStart = 0, curEnd = 0;
        var open = false;
        foreach (var (start, end) in sorted)
        {
            if (!open) { curStart = start; curEnd = end; open = true; }
            else if (start <= curEnd) curEnd = Math.Max(curEnd, end);
            else { total += curEnd - curStart; curStart = start; curEnd = end; }
        }
        if (open) total += curEnd - curStart;
        return total / 1_000_000d;
    }

    private static void SetDepths(IEnumerable<Node> roots, int rootDepth)
    {
        var stack = new Stack<Node>();
        foreach (var root in roots.Reverse())
        {
            root.Depth = rootDepth;
            stack.Push(root);
        }
        var seen = new HashSet<Node>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                node.Children[i].Depth = node.Depth + 1;
                stack.Push(node.Children[i]);
            }
        }
    }

    private static IEnumerable<Node> PreOrder(IEnumerable<Node> roots)
    {
        var stack = new Stack<Node>();
        foreach (var root in roots.Reverse()) stack.Push(root);
        var seen = new HashSet<Node>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
        }
    }

    /// <summary>The HTTP status code of a span, from <c>http.response.status_code</c> or <c>http.status_code</c>.</summary>
    /// <param name="attributes">The span's attributes.</param>
    /// <returns>The status code, or <see langword="null"/>.</returns>
    public static int? HttpStatusOf(IReadOnlyDictionary<string, string>? attributes)
    {
        if (attributes is null) return null;
        foreach (var key in new[] { "http.response.status_code", "http.status_code" })
            if (attributes.TryGetValue(key, out var raw) && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
                return code;
        return null;
    }

    /// <summary>The attributes worth showing in a line of text: query text, HTTP method and route, peer.</summary>
    /// <param name="attributes">All stored attributes of a span.</param>
    /// <param name="max">Maximum number of attributes.</param>
    /// <returns>The key attributes, in a stable order; empty when none.</returns>
    public static IReadOnlyDictionary<string, string> KeyAttributes(IReadOnlyDictionary<string, string>? attributes, int max = 4)
    {
        var result = new Dictionary<string, string>();
        if (attributes is null) return result;
        foreach (var name in KeyAttributeNames)
        {
            if (result.Count >= max) break;
            if (attributes.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)) result[name] = value;
        }
        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // One trace as a flat, bounded list
    // ---------------------------------------------------------------------------------------------------------------------------------

    /// <summary>What <c>runs trace</c> was asked for: one bee (zero-based sequence) or one trace ID.</summary>
    /// <param name="Sequence">Zero-based bee sequence, or <see langword="null"/> when a trace ID was given.</param>
    /// <param name="TraceId">The trace ID in lower case, or <see langword="null"/> when a bee was given.</param>
    public sealed record TraceKey(int? Sequence, string? TraceId);

    /// <summary>Parses a bee (<c>5</c>, <c>B5</c>, 1-based) or a trace ID (32 hex characters).</summary>
    /// <param name="raw">The text the user gave.</param>
    /// <returns>The key, or <see langword="null"/> when it is neither.</returns>
    public static TraceKey? ParseKey(string? raw)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 32 && text.All(Uri.IsHexDigit)) return new TraceKey(null, text.ToLowerInvariant());
        var digits = text.Length > 0 && (text[0] is 'B' or 'b') ? text[1..] : text;
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 1 ? new TraceKey(n - 1, null) : null;
    }

    /// <summary>One call of a bee, as <c>runs trace --call</c> needs it.</summary>
    /// <param name="Number">1-based position of the call within the bee.</param>
    /// <param name="SpanId">The call's span ID.</param>
    /// <param name="DurationMs">The call's duration.</param>
    /// <param name="Endpoint">The call's endpoint.</param>
    public sealed record CallTraceInfo(int Number, string? SpanId, double DurationMs, string Endpoint);

    /// <summary>A bee as <c>runs trace</c> needs it.</summary>
    /// <param name="Sequence">Zero-based bee sequence.</param>
    /// <param name="TraceId">The bee's trace ID; <see langword="null"/> for a run made before tracing.</param>
    /// <param name="DurationMs">The bee's duration.</param>
    /// <param name="Calls">The bee's calls in order.</param>
    public sealed record BeeTraceInfo(int Sequence, string? TraceId, double DurationMs, IReadOnlyList<CallTraceInfo> Calls);

    /// <summary>
    /// Composes the answer of <c>runs trace</c> from what a run holds: the bee, the run's fetch records and the stored spans of the bee's trace.
    /// A trace that is not stored (not fetched, not found, store problems) is a <see cref="TraceViewDto"/> with <see cref="TraceViewDto.Found"/> false and the reason.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="bee">The bee the trace belongs to.</param>
    /// <param name="call">Optional 1-based call number: only the spans below that call.</param>
    /// <param name="maxSpans">Maximum number of spans in the answer.</param>
    /// <param name="fetches">All fetch records of the run.</param>
    /// <param name="spans">The stored spans of the bee's trace.</param>
    /// <returns>The view.</returns>
    /// <exception cref="ArgumentException">The call number does not exist in the bee.</exception>
    public static TraceViewDto ComposeView(string runId, BeeTraceInfo bee, int? call, int maxSpans, IReadOnlyList<TraceFetchDto> fetches, IReadOnlyList<TraceSpanDto> spans)
    {
        CallTraceInfo? callInfo = null;
        if (call is { } n)
        {
            callInfo = bee.Calls.FirstOrDefault(c => c.Number == n)
                ?? throw new ArgumentException($"Bee B{bee.Sequence + 1} has {bee.Calls.Count} call(s); there is no call {n}.");
        }
        if (string.IsNullOrEmpty(bee.TraceId))
            return NotFoundView(runId, "", TraceFetchReasons.NoTraceId, bee.Sequence, call);

        var record = fetches.FirstOrDefault(f => string.Equals(f.TraceId, bee.TraceId, StringComparison.Ordinal));
        if (record is null)
            return NotFoundView(runId, bee.TraceId, fetches.Count == 0 ? TraceFetchReasons.NotConfigured : TraceFetchReasons.NotSelected, bee.Sequence, call);
        if (record.Status != TraceFetchStatus.Found || spans.Count == 0)
            return NotFoundView(runId, bee.TraceId, record.Reason ?? TraceFetchReasons.NotInStore, bee.Sequence, call, record);

        return BuildView(runId, bee.TraceId, spans, record, bee.Sequence, bee.DurationMs, call, callInfo?.SpanId, callInfo?.DurationMs, callInfo?.Endpoint, maxSpans);
    }

    /// <summary>The answer for a trace that is not stored: why.</summary>
    /// <param name="runId">The run.</param>
    /// <param name="traceId">The trace ID (may be empty when the bee has none).</param>
    /// <param name="reason">A code from <see cref="TraceFetchReasons"/>.</param>
    /// <param name="sequence">Zero-based bee sequence, when known.</param>
    /// <param name="call">Requested 1-based call number, when any.</param>
    /// <param name="fetch">The fetch record, when there is one.</param>
    /// <returns>A <see cref="TraceViewDto"/> with <see cref="TraceViewDto.Found"/> false.</returns>
    public static TraceViewDto NotFoundView(string runId, string traceId, string reason, int? sequence, int? call = null, TraceFetchDto? fetch = null) =>
        new(runId, traceId, false, reason, TraceFetchReasons.Describe(reason), sequence, sequence is { } s ? $"B{s + 1}" : null, call, null,
            0, "bee", 0, 0, 0, [], fetch?.SelectedFor ?? [], fetch?.FetchedUtc, null, []);

    /// <summary>
    /// Builds the flat span list of one trace (depth, service, name, duration, self time, share, error, key attributes), or of the part below one call.
    /// When more than <paramref name="maxSpans"/> spans qualify, it keeps the spans with the largest self time plus their ancestors, still in tree order.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="traceId">The trace ID.</param>
    /// <param name="spans">All stored spans of the trace.</param>
    /// <param name="fetch">The trace's fetch record, if any.</param>
    /// <param name="sequence">Zero-based bee sequence, when known.</param>
    /// <param name="beeDurationMs">The bee's duration, used as the reference of the share; the extent of the spans when <see langword="null"/>.</param>
    /// <param name="call">1-based call number when limited to one call.</param>
    /// <param name="callSpanId">The call's span ID (with <paramref name="call"/>).</param>
    /// <param name="callDurationMs">The call's duration (the reference of the share with <paramref name="call"/>).</param>
    /// <param name="callEndpoint">The call's endpoint (with <paramref name="call"/>).</param>
    /// <param name="maxSpans">Maximum number of spans to return (at least 1).</param>
    /// <returns>The span list with totals.</returns>
    public static TraceViewDto BuildView(
        string runId, string traceId, IReadOnlyList<TraceSpanDto> spans, TraceFetchDto? fetch, int? sequence, double? beeDurationMs,
        int? call = null, string? callSpanId = null, double? callDurationMs = null, string? callEndpoint = null, int maxSpans = 50)
    {
        maxSpans = Math.Max(1, maxSpans);
        var index = Index(spans);

        List<Node> roots;
        if (call is not null && !string.IsNullOrEmpty(callSpanId))
        {
            if (index.BySpanId.TryGetValue(callSpanId, out var callNode)) roots = [callNode];
            else roots = index.ByParent.TryGetValue(callSpanId, out var below) ? [.. below] : [];
        }
        else roots = index.Roots;

        SetDepths(roots, 0);
        var all = PreOrder(roots).ToList();

        var first = all.Count == 0 ? 0 : all.Min(n => n.Span.StartUnixNanos);
        var extentMs = all.Count == 0 ? 0 : (all.Max(n => n.Span.EndUnixNanos) - first) / 1_000_000d;
        var kind = call is not null ? "call" : "bee";
        var reference = call is not null && callDurationMs is > 0 ? callDurationMs.Value
            : call is null && beeDurationMs is > 0 ? beeDurationMs.Value
            : Math.Max(extentMs, 0.001);

        var chosen = all;
        if (all.Count > maxSpans)
        {
            var keep = new HashSet<Node>();
            var parentOf = new Dictionary<Node, Node>();
            foreach (var n in all) foreach (var c in n.Children) parentOf[c] = n;
            foreach (var n in all.OrderByDescending(n => n.SelfMs).ThenBy(n => n.Span.StartUnixNanos))
            {
                var chain = new List<Node>();
                for (Node? cursor = n; cursor is not null && !keep.Contains(cursor); cursor = parentOf.GetValueOrDefault(cursor))
                    chain.Add(cursor);
                if (keep.Count + chain.Count > maxSpans) continue;
                foreach (var c in chain) keep.Add(c);
                if (keep.Count >= maxSpans) break;
            }
            chosen = all.Where(keep.Contains).ToList();
        }

        var views = chosen.Select(n => new TraceSpanViewDto(
            n.Depth,
            n.Span.SpanId,
            n.Span.Service,
            n.Span.Name,
            n.Span.Kind,
            (n.Span.StartUnixNanos - first) / 1_000_000d,
            n.Span.DurationMs,
            n.SelfMs,
            Math.Clamp(n.SelfMs / reference, 0, 1),
            n.Span.IsError,
            n.Span.StatusMessage,
            HttpStatusOf(n.Span.Attributes),
            n.Span.Attributes)).ToList();

        // Depth of a kept span whose ancestors were dropped stays as in the full tree; that keeps nesting readable.
        var notes = new List<string>();
        if (fetch?.Truncated == true) notes.Add("The trace had more spans than the per-trace cap and was cut when it was fetched.");
        if (views.Count < all.Count) notes.Add($"{all.Count - views.Count} span(s) with little self time are not shown; raise --max-spans to see more.");
        if (call is not null && all.Count == 0) notes.Add("No spans below this call: the service did not join the trace (not instrumented, sampled out) or its spans have not arrived.");

        return new TraceViewDto(
            runId, traceId, true, null, null, sequence, sequence is { } s ? $"B{s + 1}" : null,
            call, callEndpoint, reference, kind, all.Count, views.Count,
            all.Count(n => n.Span.IsError),
            all.Select(n => n.Span.Service).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
            fetch?.SelectedFor ?? [], fetch?.FetchedUtc,
            notes.Count == 0 ? null : string.Join(' ', notes),
            views);
    }

    // ---------------------------------------------------------------------------------------------------------------------------------
    // Hotspots
    // ---------------------------------------------------------------------------------------------------------------------------------

    private readonly record struct SpanKey(string Service, string Name);

    private sealed class CallProfile
    {
        public required CallAnalytics.ClassifiedCall Classified { get; init; }
        public required Dictionary<SpanKey, double> SelfByKey { get; init; }
        public required Dictionary<SpanKey, TraceSpanDto> ExampleByKey { get; init; }
        public required SpanKey Top { get; init; }
        public required double TopSelfMs { get; init; }
        public double DurationMs => Classified.Call.DurationMs;
    }

    /// <summary>
    /// For each endpoint: of the slow calls that have a fetched trace, the span (service + name) with the largest self time per call, how many slow calls each
    /// span explains, its typical self time and share of the call, example trace IDs, compared with the same span in the endpoint's typical calls.
    /// A call's spans are those below its own span (the child spans of the call's span ID, so it works whether or not Beesharp's own spans were exported);
    /// time no service span covers becomes the pseudo span <see cref="OutsideService"/>.
    /// </summary>
    /// <param name="runId">The run.</param>
    /// <param name="calls">All calls of the run.</param>
    /// <param name="spans">The stored spans of all fetched traces.</param>
    /// <param name="fetches">The run's fetch records.</param>
    /// <param name="query">Filters and limits.</param>
    /// <returns>The hotspots per endpoint and the coverage of the fetch.</returns>
    public static HotspotsResultDto BuildHotspots(
        string runId, IReadOnlyList<CallFact> calls, IReadOnlyList<TraceSpanDto> spans, IReadOnlyList<TraceFetchDto> fetches, HotspotsQuery query)
    {
        var coverage = fetches.Count == 0 ? TraceFetchSummaryDto.NotConfigured : TraceFetchSummaryDto.From(fetches);
        if (spans.Count == 0)
        {
            var note = fetches.Count == 0
                ? "No traces are stored with this run: " + TraceFetchReasons.Describe(TraceFetchReasons.NotConfigured) + "."
                : "None of the selected traces was found in the trace store: " + coverage.Line + ".";
            return new HotspotsResultDto(runId, coverage, 0, [], note);
        }

        var classified = CallAnalytics.Classify(calls);
        var traces = new Dictionary<string, TraceIndex>(StringComparer.Ordinal);
        foreach (var group in spans.GroupBy(s => s.TraceId, StringComparer.Ordinal))
            traces[group.Key] = Index(group);

        var profiles = new List<CallProfile>();
        foreach (var cc in classified)
        {
            var c = cc.Call;
            if (c.Error is not null || string.IsNullOrEmpty(c.TraceId) || string.IsNullOrEmpty(c.SpanId) || c.DurationMs <= 0) continue;
            if (!traces.TryGetValue(c.TraceId, out var index)) continue;
            profiles.Add(Profile(cc, index));
        }

        var summaries = CallAnalytics.BuildEndpoints(calls).Endpoints
            .GroupBy(e => e.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var epFilter = query.Endpoint?.Trim() ?? "";
        var endpoints = new List<EndpointHotspotsDto>();
        foreach (var group in profiles.GroupBy(p => p.Classified.Endpoint, StringComparer.OrdinalIgnoreCase))
        {
            if (epFilter.Length > 0 && !group.Key.Contains(epFilter, StringComparison.OrdinalIgnoreCase)) continue;
            var slow = group.Where(p => p.Classified.IsSlow).ToList();
            var typical = group.Where(p => !p.Classified.IsSlow).ToList();
            if (query.SlowOnly && slow.Count == 0) continue;

            summaries.TryGetValue(group.Key, out var summary);
            var basisSlow = slow.Count > 0;
            var analysed = basisSlow ? slow : typical;
            var hotspots = Aggregate(analysed, basisSlow ? typical : null, query.Top);
            endpoints.Add(new EndpointHotspotsDto(
                group.Key,
                summary?.TotalCount ?? group.Count(),
                summary?.SlowCount ?? 0,
                slow.Count,
                typical.Count,
                summary?.TypicalMs ?? Median(typical.Select(p => p.DurationMs).ToList()),
                summary?.SlowThresholdMs,
                basisSlow ? Median(slow.Select(p => p.DurationMs).ToList()) : null,
                basisSlow ? "slow" : "typical",
                hotspots));
        }

        var ordered = endpoints
            .OrderByDescending(e => e.SlowCallsWithTrace)
            .ThenByDescending(e => e.SlowCalls)
            .ThenByDescending(e => e.TypicalMs)
            .ThenBy(e => e.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var limit = Math.Clamp(query.Limit, 1, 1000);
        string? hint = null;
        if (ordered.Count == 0)
            hint = query.SlowOnly
                ? "No endpoint has a slow call with a fetched trace."
                : epFilter.Length > 0 ? $"No endpoint containing '{epFilter}' has a call with a fetched trace." : "No call has a fetched trace.";
        return new HotspotsResultDto(runId, coverage, ordered.Count, ordered.Take(limit).ToList(), hint);
    }

    private static CallProfile Profile(CallAnalytics.ClassifiedCall cc, TraceIndex index)
    {
        var call = cc.Call;
        var roots = index.ByParent.TryGetValue(call.SpanId!, out var below) ? below : [];
        var self = new Dictionary<SpanKey, double>();
        var example = new Dictionary<SpanKey, (TraceSpanDto Span, double Self)>();
        var stack = new Stack<Node>(roots.AsEnumerable().Reverse());
        var seen = new HashSet<Node>();
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!seen.Add(node)) continue;
            var key = new SpanKey(node.Span.Service, node.Span.Name);
            self[key] = self.GetValueOrDefault(key) + node.SelfMs;
            if (!example.TryGetValue(key, out var best) || node.SelfMs > best.Self) example[key] = (node.Span, node.SelfMs);
            foreach (var child in node.Children) stack.Push(child);
        }

        var covered = roots.Count == 0 ? 0 : UnionMs(roots.Select(r => (r.Span.StartUnixNanos, r.Span.EndUnixNanos)), long.MinValue, long.MaxValue);
        var outside = Math.Max(0, call.DurationMs - Math.Min(call.DurationMs, covered));
        var outsideKey = new SpanKey(OutsideService, OutsideName);
        if (outside > 0) self[outsideKey] = outside;

        var top = self.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.Service, StringComparer.Ordinal).ThenBy(kv => kv.Key.Name, StringComparer.Ordinal).First();
        return new CallProfile
        {
            Classified = cc,
            SelfByKey = self,
            ExampleByKey = example.ToDictionary(kv => kv.Key, kv => kv.Value.Span),
            Top = top.Key,
            TopSelfMs = top.Value,
        };
    }

    private static IReadOnlyList<HotspotDto> Aggregate(List<CallProfile> analysed, List<CallProfile>? baseline, int top)
    {
        var result = new List<HotspotDto>();
        foreach (var byKey in analysed.GroupBy(p => p.Top))
        {
            var members = byKey.ToList();
            var selfs = members.Select(p => p.TopSelfMs).ToList();
            var shares = members.Select(p => p.TopSelfMs / p.DurationMs).ToList();
            double? baselineMs = baseline is { Count: > 0 }
                ? Median(baseline.Select(p => p.SelfByKey.GetValueOrDefault(byKey.Key)).ToList())
                : null;
            var slowestFirst = members.OrderByDescending(p => p.TopSelfMs).ToList();
            var exampleSpan = slowestFirst.Select(p => p.ExampleByKey.GetValueOrDefault(byKey.Key)).FirstOrDefault(s => s is not null);
            var medianSelf = Median(selfs);
            result.Add(new HotspotDto(
                byKey.Key.Service,
                byKey.Key.Name,
                members.Count,
                members.Count / (double)analysed.Count,
                medianSelf,
                Median(shares),
                baselineMs,
                baselineMs is > 0 ? medianSelf / baselineMs : null,
                slowestFirst.Select(p => p.Classified.Call.TraceId!).Distinct(StringComparer.Ordinal).Take(3).ToList(),
                exampleSpan?.Attributes is { Count: > 0 } attrs ? KeyAttributes(attrs) is { Count: > 0 } key ? key : null : null,
                byKey.Key.Service == OutsideService));
        }

        return result
            .OrderByDescending(h => h.Explains)
            .ThenByDescending(h => h.MedianSelfMs)
            .ThenBy(h => h.Service, StringComparer.Ordinal)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .Take(Math.Max(1, top))
            .ToList();
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return values[(values.Count - 1) / 2];
    }
}
