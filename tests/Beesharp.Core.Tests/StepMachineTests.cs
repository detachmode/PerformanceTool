using System.ComponentModel.DataAnnotations;
using System.Net;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Core.Tests;

public class StepMachineTests
{
    public sealed record FlowParams(int OpenCount = 3, bool LoopBackOnce = false, int FailAtOpenIndex = -1);

    [Display(Name = "Sign in")]
    public sealed record SignInStep;
    public sealed record Search(string Token);
    public sealed record OpenProduct(string Token, int Index, bool Looped = false);
    public sealed record Pay(string Token);

    public union FlowStep(SignInStep, Search, OpenProduct, Pay);

    private sealed class TestFlowScenario : Scenario<FlowParams, FlowStep>
    {
        public override string Id => "test-flow";
        public override string Name => "Test flow";
        public override string Description => "Union step machine test.";

        protected override FlowStep StartStep(FlowParams p) => new SignInStep();

        protected override async Task<FlowStep?> RunStepAsync(FlowStep step, FlowParams p, ScenarioContext ctx, CancellationToken ct) =>
            step switch
            {
                SignInStep => await DoSignIn(ctx),
                Search s => await DoSearch(s, ctx),
                OpenProduct o => await DoOpen(o, p, ctx),
                Pay pay => await DoPay(pay, ctx),
            };

        private static async Task<FlowStep?> DoSignIn(ScenarioContext ctx)
        {
            await ctx.MeasureCallAsync("POST /login", () => Task.CompletedTask);
            ctx.Log("logged in");
            return new Search("tok-1");
        }

        private static async Task<FlowStep?> DoSearch(Search s, ScenarioContext ctx)
        {
            await ctx.MeasureCallAsync("GET /search", () => Task.CompletedTask);
            return new OpenProduct(s.Token, 0);
        }

        private static async Task<FlowStep?> DoOpen(OpenProduct o, FlowParams p, ScenarioContext ctx)
        {
            await ctx.MeasureCallAsync($"GET /products/{o.Index}", () =>
            {
                if (o.Index == p.FailAtOpenIndex)
                    throw new InvalidOperationException("Not found");
                return Task.CompletedTask;
            });
            if (p.LoopBackOnce && !o.Looped && o.Index == 0)
                return new Search(o.Token);
            return o.Index + 1 < p.OpenCount
                ? o with { Index = o.Index + 1, Looped = true }
                : new Pay(o.Token);
        }

        private static async Task<FlowStep?> DoPay(Pay pay, ScenarioContext ctx)
        {
            await ctx.MeasureCallAsync("POST /pay", () => Task.CompletedTask);
            return null;
        }
    }

    [Fact]
    public void Union_reflection_extracts_ordered_steps_and_display_names()
    {
        var compiled = StepUnionReflection.Inspect<FlowStep>();
        Assert.Equal(["Sign in", "Search", "Open product", "Pay"], compiled.Steps.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Step_machine_records_step_and_rep_for_calls_and_logs()
    {
        var scenario = new TestFlowScenario();
        var calls = new List<RecordedCall>();
        var logs = new List<RecordedLog>();
        using var http = new HttpClient();
        var ctx = new ScenarioContext(http, callSink: calls.Add, structuredLogSink: logs.Add);

        var outcome = await ((IScenario)scenario).ExecuteAsync(new FlowParams(OpenCount: 3), ctx, CancellationToken.None);

        Assert.True(outcome.Success);
        Assert.Equal(6, calls.Count);
        Assert.Equal([(0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (3, 0)], calls.Select(c => (c.Step, c.Rep)).ToArray());
        // Automatic step-execution log statement for every step visit, plus the user's "logged in" log in step 0.
        Assert.Equal(7, logs.Count);
        Assert.StartsWith("─── ▶ Sign in ───", logs[0].Message);
        Assert.Equal(0, logs[0].Step);
        Assert.Equal("logged in", logs[1].Message);
        Assert.Equal(0, logs[1].Step);
        Assert.StartsWith("─── ▶ Search (token: tok-1) ───", logs[2].Message);
        Assert.Equal(1, logs[2].Step);
        Assert.StartsWith("─── ▶ Open product (token: tok-1 · index: 0 · looped: false) ───", logs[3].Message);
        Assert.Equal((2, 0), (logs[3].Step, logs[3].Rep));
        Assert.StartsWith("─── ▶ Open product #2 (token: tok-1 · index: 1 · looped: true) ───", logs[4].Message);
        Assert.Equal((2, 1), (logs[4].Step, logs[4].Rep));
        Assert.StartsWith("─── ▶ Open product #3 (token: tok-1 · index: 2 · looped: true) ───", logs[5].Message);
        Assert.Equal((2, 2), (logs[5].Step, logs[5].Rep));
        Assert.StartsWith("─── ▶ Pay (token: tok-1) ───", logs[6].Message);
        Assert.Equal(3, logs[6].Step);
    }

    [Fact]
    public async Task Step_machine_failure_leaves_context_at_failed_step_and_rep()
    {
        var scenario = new TestFlowScenario();
        var calls = new List<RecordedCall>();
        var visits = new List<RecordedStepVisit>();
        using var http = new HttpClient();
        var ctx = new ScenarioContext(http, callSink: calls.Add, stepVisitSink: visits.Add);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ((IScenario)scenario).ExecuteAsync(new FlowParams(OpenCount: 3, FailAtOpenIndex: 2), ctx, CancellationToken.None));

        Assert.Equal(2, ctx.CurrentStep);
        Assert.Equal(2, ctx.CurrentRep);
        Assert.Equal(2, calls.Last().Step);
        Assert.Equal(2, calls.Last().Rep);
        Assert.Equal("Not found", calls.Last().Error);
    }

    [Fact]
    public async Task Recording_http_handler_tags_calls_with_current_step_and_rep()
    {
        var calls = new List<RecordedCall>();
        using var inner = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var recording = new RecordingHttpHandler(inner);
        using var http = new HttpClient(recording);
        var ctx = new ScenarioContext(http, callSink: calls.Add);

        var host = new ScenarioScriptHost();
        host.DefineSteps<FlowStep>(
            "Http steps",
            start: () => new SignInStep(),
            run: async (step, c) => step switch
            {
                SignInStep => await StepCall(c, "https://example.test/login", new Search("t")),
                Search => await StepCall(c, "https://example.test/search", null),
                OpenProduct => null,
                Pay => null,
            });
        var scn = host.BuildAll()[0];

        using (ctx.Activate())
            await scn.ExecuteAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None);

        Assert.Equal(2, calls.Count);
        Assert.Equal("GET /login", calls[0].Name);
        Assert.Equal(0, calls[0].Step);
        Assert.Equal("GET /search", calls[1].Name);
        Assert.Equal(1, calls[1].Step);

        static async Task<FlowStep?> StepCall(ScenarioContext c, string url, FlowStep? next)
        {
            await c.Http.GetAsync(url, c.CancellationToken);
            return next;
        }
    }

    [Fact]
    public async Task Yield_step_machine_runs_multiple_yielded_steps_depth_first_with_retries()
    {
        var calls = new List<RecordedCall>();
        var visits = new List<RecordedStepVisit>();
        using var http = new HttpClient();
        var ctx = new ScenarioContext(http, callSink: calls.Add, stepVisitSink: visits.Add);

        var host = new ScenarioScriptHost();
        host.DefineSteps<FlowStep>(
            "Yield steps",
            start: () => new SignInStep(),
            run: (step, c) => step switch
            {
                SignInStep => YieldOne(c, "login", new Search("t")),
                Search s => YieldSearch(c, s.Token),
                OpenProduct o => YieldOpen(c, o),
                Pay => YieldDone(c, "pay"),
            });
        var scn = host.BuildAll()[0];

        var outcome = await scn.ExecuteAsync(new EmptyScenarioParameters(), ctx, CancellationToken.None);

        Assert.True(outcome.Success);
        // Open(0) retries itself immediately (depth-first) before Open(1) and Pay!
        Assert.Equal(["login", "search", "open-0", "open-0-retry", "open-1", "pay"], calls.Select(c => c.Name).ToArray());
        Assert.Equal([(0, 0), (1, 0), (2, 0), (2, 1), (2, 2), (3, 0)], calls.Select(c => (c.Step, c.Rep)).ToArray());
        // Step record fields are captured per visit (null for parameterless SignInStep).
        Assert.Equal(6, visits.Count);
        Assert.Null(visits[0].Data);
        Assert.Contains("\"token\":\"t\"", visits[1].Data);
        Assert.Contains("\"index\":1", visits[4].Data);

        static async IAsyncEnumerable<FlowStep> YieldOne(ScenarioContext c, string name, FlowStep next)
        {
            await c.MeasureCallAsync(name, () => Task.CompletedTask);
            yield return next;
        }

        static async IAsyncEnumerable<FlowStep> YieldSearch(ScenarioContext c, string token)
        {
            await c.MeasureCallAsync("search", () => Task.CompletedTask);
            yield return new OpenProduct(token, 0, Looped: false);
            yield return new OpenProduct(token, 1, Looped: true);
            yield return new Pay(token);
        }

        static async IAsyncEnumerable<FlowStep> YieldOpen(ScenarioContext c, OpenProduct o)
        {
            await c.MeasureCallAsync(o.Looped && o.Index == 0 ? "open-0-retry" : $"open-{o.Index}", () => Task.CompletedTask);
            if (o.Index == 0 && !o.Looped)
                yield return o with { Looped = true };
        }

        static async IAsyncEnumerable<FlowStep> YieldDone(ScenarioContext c, string name)
        {
            await c.MeasureCallAsync(name, () => Task.CompletedTask);
            yield break;
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
