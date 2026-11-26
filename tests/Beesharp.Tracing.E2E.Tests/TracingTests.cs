using System.Text.RegularExpressions;
using Beesharp.Contracts;
using OpenTelemetry.Proto.Trace.V1;
using Xunit.Abstractions;

namespace Beesharp.Tracing.E2E.Tests;

/// <summary>
/// The tracing example end to end (brain 0063, 0064): a real Beesharp loads the "Shop journey" preset against real shop-api and
/// payment-api processes. Nothing here depends on random behaviour (slow queries, payment failures): those only add checks when they happen.
/// </summary>
public sealed partial class TracingTests(TracingStack stack, ITestOutputHelper output) : IClassFixture<TracingStack>
{
    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex TraceIdPattern();
    [GeneratedRegex("^[0-9a-f]{16}$")] private static partial Regex SpanIdPattern();

    [Fact]
    public void The_run_produced_bees_with_calls()
    {
        Assert.True(stack.Bees.Count >= 3, $"Expected the 8 s run to finish at least 3 bees, got {stack.Bees.Count}.");
        Assert.All(stack.Bees, b => Assert.True(b.Calls.Count == 6, $"bee {b.Sequence + 1} made {b.Calls.Count} calls, expected 6 (Log in, Search, 3 x Open product, Pay)."));
    }

    [Fact]
    public void Every_call_has_a_trace_id_and_a_span_id_and_all_calls_of_a_bee_share_the_trace()
    {
        var problems = new List<string>();
        foreach (var bee in stack.Bees)
        {
            if (bee.TraceId is null || !TraceIdPattern().IsMatch(bee.TraceId)) problems.Add($"bee {bee.Sequence + 1}: trace ID '{bee.TraceId}' is not 32 lower-case hex characters");
            foreach (var call in bee.Calls)
            {
                if (call.TraceId is null || !TraceIdPattern().IsMatch(call.TraceId)) problems.Add($"bee {bee.Sequence + 1}, {call.Url}: trace ID '{call.TraceId}' is not 32 hex characters");
                if (call.SpanId is null || !SpanIdPattern().IsMatch(call.SpanId)) problems.Add($"bee {bee.Sequence + 1}, {call.Url}: span ID '{call.SpanId}' is not 16 hex characters");
                if (call.TraceId != bee.TraceId) problems.Add($"bee {bee.Sequence + 1}, {call.Url}: call trace ID {call.TraceId} differs from the bee's {bee.TraceId}");
            }
            var duplicates = bee.Calls.GroupBy(c => c.SpanId).Where(g => g.Count() > 1).Select(g => g.Key);
            foreach (var id in duplicates) problems.Add($"bee {bee.Sequence + 1}: span ID {id} is used by more than one call");
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    [Fact]
    public void Different_bees_have_different_traces()
    {
        var duplicated = stack.Bees.GroupBy(b => b.TraceId).Where(g => g.Count() > 1)
            .Select(g => $"trace {g.Key} is shared by bees {string.Join(", ", g.Select(b => b.Sequence + 1))}").ToList();
        Assert.True(duplicated.Count == 0, string.Join('\n', duplicated));
    }

    [Fact]
    public void Each_bees_exported_trace_is_one_tree_from_beesharp_through_shop_api_to_payment_api()
    {
        var problems = stack.TraceProblems.Values.SelectMany(p => p).ToList();
        Assert.True(stack.TraceProblems.Count == stack.Bees.Count && problems.Count == 0,
            $"{problems.Count} problem(s) in the traces of {stack.Bees.Count} bees, after waiting for the spans:\n{string.Join('\n', problems.Take(30))}");
        var first = stack.Bees[0];
        output.WriteLine($"{stack.Bees.Count} bees, all traces complete. Example: bee {first.Sequence + 1}, trace {first.TraceId}");
        foreach (var span in stack.Receiver.Snapshot().Where(s => s.TraceId == first.TraceId))
            output.WriteLine($"  {span.Service,-12} {span.Kind,-8} {span.Name} ({span.SpanId[..6]} <- {(span.HasParent ? span.ParentSpanId[..6] : "root")})");
    }

    [Fact]
    public void A_slow_database_query_explains_a_slow_call()
    {
        var spans = stack.Receiver.Snapshot();
        var slowQueries = spans.Where(s => s.Service == "shop-api" && s.Attributes.GetValueOrDefault("demo.slow_query") == "true").ToList();
        if (slowQueries.Count == 0)
        {
            output.WriteLine("No slow query happened in this run (5-10 % of the queries are slow), so there is nothing to check. Not a failure.");
            return;
        }
        var problems = new List<string>();
        foreach (var query in slowQueries)
        {
            var server = spans.FirstOrDefault(s => s.SpanId == query.ParentSpanId && s.Kind == Span.Types.SpanKind.Server);
            var callSpanId = server?.ParentSpanId;
            var call = stack.Bees.SelectMany(b => b.Calls).FirstOrDefault(c => c.SpanId == callSpanId);
            if (call is null) { problems.Add($"slow query '{query.Name}' ({query.SpanId}) does not lead up to a stored Beesharp call"); continue; }
            if (query.DurationMs < 400) problems.Add($"slow query '{query.Name}' took {query.DurationMs:0} ms, expected at least 400 ms");
            if (call.DurationMs < query.DurationMs) problems.Add($"the call {call.Url} took {call.DurationMs:0} ms, less than its database span ({query.DurationMs:0} ms)");
        }
        Assert.True(problems.Count == 0, string.Join('\n', problems));
        output.WriteLine($"{slowQueries.Count} slow quer{(slowQueries.Count == 1 ? "y" : "ies")} found, each below a call that is at least as slow.");
    }
}
