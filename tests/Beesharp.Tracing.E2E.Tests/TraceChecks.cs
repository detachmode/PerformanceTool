using Beesharp.Contracts;
using OpenTelemetry.Proto.Trace.V1;

namespace Beesharp.Tracing.E2E.Tests;

/// <summary>
/// What a bee's exported trace has to look like (brain 0063), checked against the spans the receiver got:
/// beesharp actor root → step spans → call spans → shop-api server span → DB spans, and below Pay shop-api's HTTP client span → payment-api.
/// Each problem is one sentence naming what is missing.
/// </summary>
public static class TraceChecks
{
    public static List<string> Check(StoredBeeDetail bee, IReadOnlyList<ReceivedSpan> received)
    {
        var problems = new List<string>();
        void Problem(string text) => problems.Add($"bee {bee.Sequence + 1} (trace {bee.TraceId}): {text}");

        var spans = received.Where(s => s.TraceId == bee.TraceId).ToList();
        if (spans.Count == 0) { Problem("no span was exported with this trace ID"); return problems; }
        var byId = spans.GroupBy(s => s.SpanId).ToDictionary(g => g.Key, g => g.First());

        var services = spans.Select(s => s.Service).Distinct().Order().ToList();
        if (!services.SequenceEqual(TracingStack.Services))
            Problem($"the trace has spans from [{string.Join(", ", services)}], expected exactly [{string.Join(", ", TracingStack.Services)}]");

        foreach (var orphan in spans.Where(s => s.HasParent && !byId.ContainsKey(s.ParentSpanId)))
            Problem($"{orphan.Service} span '{orphan.Name}' has parent {orphan.ParentSpanId}, which was never exported");

        // One actor root, from Beesharp.
        var roots = spans.Where(s => !s.HasParent).ToList();
        if (roots.Count != 1) Problem($"expected exactly one root span, found {roots.Count}: [{string.Join(", ", roots.Select(r => $"{r.Service}/{r.Name}"))}]");
        var root = roots.FirstOrDefault(r => r.Service == "beesharp");
        if (root is null) { Problem("the root span is not from service 'beesharp'"); return problems; }

        // Step spans: Beesharp spans directly below the root, named after the scenario's steps, in visit order.
        var callSpanIds = bee.Calls.Select(c => c.SpanId).ToHashSet();
        var steps = spans.Where(s => s.Service == "beesharp" && s.ParentSpanId == root.SpanId).ToList();
        var stepNames = steps.Select(s => s.Name).ToList();
        if (!stepNames.OrderBy(n => n).SequenceEqual(TracingStack.StepSpanNames.OrderBy(n => n)))
            Problem($"step spans below the root are [{string.Join(", ", stepNames)}], expected [{string.Join(", ", TracingStack.StepSpanNames)}]");
        var stepIds = steps.Select(s => s.SpanId).ToHashSet();

        // Call spans: one per call stored by Beesharp (same span ID), each below a step, each the parent of one shop-api server span.
        var beesharpSpans = spans.Count(s => s.Service == "beesharp");
        if (beesharpSpans != 1 + steps.Count + bee.Calls.Count)
            Problem($"{beesharpSpans} beesharp spans exported, expected root + {steps.Count} steps + {bee.Calls.Count} calls = {1 + steps.Count + bee.Calls.Count}");
        foreach (var call in bee.Calls)
        {
            var what = $"call {call.Method} {call.Url} (span {call.SpanId})";
            if (call.SpanId is null || !byId.TryGetValue(call.SpanId, out var callSpan) || callSpan.Service != "beesharp") { Problem($"{what}: no beesharp span with this span ID was exported"); continue; }
            if (!stepIds.Contains(callSpan.ParentSpanId)) Problem($"{what}: its span is not a child of a step span");

            var servers = spans.Where(s => s.Service == "shop-api" && s.Kind == Span.Types.SpanKind.Server && s.ParentSpanId == call.SpanId).ToList();
            if (servers.Count != 1) { Problem($"{what}: expected one shop-api server span with the call span as parent, found {servers.Count}"); continue; }
            var server = servers[0];

            if (!spans.Any(s => s.Service == "shop-api" && s.ParentSpanId == server.SpanId && s.Attributes.ContainsKey("db.system.name")))
                Problem($"{what}: the shop-api server span '{server.Name}' has no database span");

            if (call.Url?.Contains("/pay", StringComparison.Ordinal) == true)
            {
                var clients = spans.Where(s => s.Service == "shop-api" && s.Kind == Span.Types.SpanKind.Client && s.ParentSpanId == server.SpanId && !s.Attributes.ContainsKey("db.system.name")).ToList();
                if (clients.Count != 1) { Problem($"{what}: expected one shop-api HTTP client span below '{server.Name}', found {clients.Count}"); continue; }
                if (!spans.Any(s => s.Service == "payment-api" && s.Kind == Span.Types.SpanKind.Server && s.ParentSpanId == clients[0].SpanId))
                    Problem($"{what}: no payment-api server span below shop-api's HTTP client span");
            }
        }
        if (!bee.Calls.Any(c => c.Url?.Contains("/pay", StringComparison.Ordinal) == true)) Problem("the bee made no call to /pay, so the payment-api part of the trace cannot be checked");
        return problems;
    }
}
