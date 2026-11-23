using Beesharp.Client.Runs;
using Beesharp.Client.Data;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>Scenario steps (brain 0056, UI draft): current step, failed step, counts per step and the step filter.</summary>
public sealed class ScenarioStepsTests
{
    private static readonly ScenarioSteps Checkout = new(["Log in", "Search", "Add to cart", "Pay"]);

    private static FakeCall Call(ErrorDictionary errors, int index, int step, double offset, double duration, string? error = null, bool skipped = false) =>
        new(index, "call " + index, offset, duration, error is null ? 200 : 500, error is null ? ErrorDictionary.None : errors.Intern(error), skipped, 0, Errors: errors, Step: step);

    /// <summary>Four steps with two calls each; when <paramref name="failAt"/> is a call index, that call fails and later ones are skipped.</summary>
    private static List<FakeCall> Story(ErrorDictionary errors, int failAt = -1)
    {
        var list = new List<FakeCall>();
        for (var i = 0; i < 8; i++)
        {
            var skipped = failAt >= 0 && i > failAt;
            list.Add(Call(errors, i, i / 2, i * 100, skipped ? 0 : 90, i == failAt ? "HTTP 422 Payment declined" : null, skipped));
        }
        return list;
    }

    [Fact]
    public void Position_reads_step_n_of_m_and_the_name()
    {
        Assert.Equal("Step 2 of 4 · Search", Checkout.Position(1));
        Assert.Equal("", ScenarioSteps.None.Name(0));
    }

    [Fact]
    public void Current_step_follows_the_calls_that_have_started()
    {
        var calls = Story(new ErrorDictionary());
        Assert.Equal(0, StepLogic.CurrentStep(calls, 0));
        Assert.Equal(0, StepLogic.CurrentStep(calls, 150));
        Assert.Equal(1, StepLogic.CurrentStep(calls, 200));
        Assert.Equal(3, StepLogic.CurrentStep(calls, 10_000));
    }

    [Fact]
    public void Failed_step_is_the_step_of_the_first_failed_call()
    {
        var errors = new ErrorDictionary();
        Assert.Equal(-1, StepLogic.FailedStep(Story(errors)));
        Assert.Equal(3, StepLogic.FailedStep(Story(errors, failAt: 6)));
        Assert.Equal(1, StepLogic.FailedStep(Story(errors, failAt: 2)));
    }

    [Fact]
    public void A_failed_bee_has_done_steps_then_the_failed_one_then_steps_not_reached()
    {
        var errors = new ErrorDictionary();
        var steps = StepLogic.ForBee(Checkout, Story(errors, failAt: 2));
        Assert.Equal([StepState.Done, StepState.Failed, StepState.Pending, StepState.Pending], steps.Select(s => s.State));
        Assert.Equal("HTTP 422 Payment declined", steps[1].Error);
        Assert.Null(steps[0].Error);
        Assert.Equal(0, steps[2].DurationMs);   // never ran
    }

    [Fact]
    public void A_bee_in_flight_has_done_steps_the_current_one_and_steps_not_reached_and_has_not_failed_yet()
    {
        var errors = new ErrorDictionary();
        var calls = Story(errors, failAt: 6);   // it will fail at Pay, but at 250 ms it is only in Search
        var steps = StepLogic.ForRunning(Checkout, calls, 250);
        Assert.Equal([StepState.Done, StepState.Current, StepState.Pending, StepState.Pending], steps.Select(s => s.State));
        Assert.All(steps, s => Assert.Null(s.Error));
        Assert.Single(steps[1].Calls);   // only the calls that have started
    }

    [Fact]
    public void Step_time_runs_from_the_first_call_start_to_the_last_call_end()
    {
        var steps = StepLogic.ForBee(Checkout, Story(new ErrorDictionary()));
        Assert.Equal(190, steps[0].DurationMs);   // calls at 0 and 100, 90 ms each
    }

    [Fact]
    public void In_flight_counts_per_step()
    {
        var errors = new ErrorDictionary();
        var calls = Story(errors);
        RunningBee Bee(int seq, double startedAt) => new(seq, 0, startedAt, calls);
        // elapsed 10 s: a bee started at 9.75 s is 250 ms in (Search), 9.0 s is 1000 ms in (Pay), 9.95 s is 50 ms in (Log in)
        var counts = StepLogic.InFlightCounts(Checkout, [Bee(0, 9.75), Bee(1, 9.0), Bee(2, 9.95), Bee(3, 9.81)], 10);
        Assert.Equal([2, 1, 0, 1], counts);
    }

    [Fact]
    public void Store_counts_failed_bees_per_step_and_names_the_most_failed()
    {
        var store = new RunStore(2);
        void Fail(int seq, int entry, int step)
        {
            var record = new BeeRecord(seq, entry, seq, 100, 0, store.Errors.Intern("boom"), store.Errors, FailedStep: step);
            store.Complete(record, new BeeDetail(seq, entry, [], () => []));
        }
        Fail(0, 0, 3); Fail(1, 0, 3); Fail(2, 0, 1); Fail(3, 1, 0);
        store.Complete(new BeeRecord(4, 0, 4, 100, 0, ErrorDictionary.None, store.Errors), new BeeDetail(4, 0, [], () => []));

        Assert.Equal([0, 1, 0, 2], store.FailedPerStep(0));
        Assert.Equal([1], store.FailedPerStep(1));
        Assert.Equal((3, 2), StepLogic.MostFailed(store.FailedPerStep(0)));
        Assert.Null(StepLogic.MostFailed([0, 0]));
        Assert.Null(StepLogic.MostFailed([]));
    }

    [Fact]
    public void Filter_keeps_only_bees_that_failed_at_the_step_and_in_flight_bees_in_the_step()
    {
        var errors = new ErrorDictionary();
        var bees = new List<BeeRecord>
        {
            new(0, 0, 0, 100, 0, errors.Intern("x"), errors, FailedStep: 3),
            new(1, 0, 1, 100, 0, errors.Intern("x"), errors, FailedStep: 1),
            new(2, 0, 2, 100, 0, ErrorDictionary.None, errors),
            new(3, 0, 3, 100, 0, errors.Intern("x"), errors, FailedStep: 3),
        };
        Assert.Equal([0, 3], StepLogic.FilterFinished(bees, bees.Count, new StepFilter(3, false)).Select(b => b.Sequence));
        Assert.Same(bees, StepLogic.FilterFinished(bees, bees.Count, null));
        Assert.Empty(StepLogic.FilterFinished(bees, bees.Count, new StepFilter(3, true)));   // an in-flight filter lists no finished bees

        var calls = Story(errors);
        var running = new List<RunningBee> { new(10, 0, 9.75, calls), new(11, 0, 9.0, calls) };
        Assert.Equal([10], StepLogic.FilterRunning(running, new StepFilter(1, true), 10).Select(r => r.Sequence));
        Assert.Empty(StepLogic.FilterRunning(running, new StepFilter(1, false), 10));   // a failed-at filter has no bee in flight
        Assert.Equal(2, StepLogic.FilterRunning(running, null, 10).Count);
    }

    [Fact]
    public void Fake_run_declares_steps_for_some_scenarios_and_not_for_others_and_failures_cluster_at_steps()
    {
        var source = new FakeRunSource(new ScenarioCatalog([new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]));
        var s = source.Current;
        Assert.True(s.StepsOf(1).Any);                   // Guest checkout
        Assert.False(s.StepsOf(2).Any);                  // Browse categories: no steps, looks as before
        Assert.False(s.StepsOf(4).Any);
        Assert.Equal("Pay", s.StepsOf(1).Name(3));
        // every call of a scenario with steps belongs to a declared step; the failed bees failed at one of them
        var failed = Enumerable.Range(0, s.Store.Bees(1).Count).Select(i => s.Store.Bees(1)[i]).Where(b => b.Failed && !b.Canceled).ToList();
        Assert.NotEmpty(failed);
        Assert.All(failed, b => Assert.Equal(3, b.FailedStep));   // Guest checkout fails at Pay
        Assert.Equal(failed.Count, s.Store.FailedPerStep(1)[3] - s.Store.Bees(1).Count(b => b.Canceled && b.FailedStep == 3));
        // a scenario without steps records no failed step
        Assert.All(s.Store.Bees(2), b => Assert.Equal(-1, b.FailedStep));
        source.Dispose();
    }

    // A step that runs 5 times in a row (brain 0056): "Open product" is step 2 of 5 declared steps, one call per repetition here.
    private static readonly ScenarioSteps WithRepeats = new(["Log in", "Search", "Open product", "Add to cart", "Pay"], [1, 1, 5, 1, 1]);

    private static List<FakeCall> RepeatStory(ErrorDictionary errors, int failRep = -1)
    {
        var list = new List<FakeCall> { Call(errors, 0, 0, 0, 90), Call(errors, 1, 1, 100, 90) };
        for (var r = 0; r < 5; r++)
        {
            var skipped = failRep >= 0 && r > failRep;
            list.Add(new FakeCall(2 + r, "open " + r, 200 + r * 100, skipped ? 0 : 90, 200, r == failRep ? errors.Intern("HTTP 503") : ErrorDictionary.None, skipped, 0, Errors: errors, Step: 2, Rep: r));
        }
        return list;
    }

    [Fact]
    public void Repeated_step_shows_once_with_its_times_and_the_repetition_in_flight()
    {
        Assert.Equal(5, WithRepeats.Times(2));
        Assert.Equal(1, WithRepeats.Times(0));
        Assert.Equal("Step 3 of 5 · Open product (2 of 5)", WithRepeats.Position(2, 1));
        Assert.Equal("Step 2 of 5 · Search", WithRepeats.Position(1, 0));   // a step that runs once shows no count
        var calls = RepeatStory(new ErrorDictionary());
        Assert.Equal("Step 3 of 5 · Open product (2 of 5)", StepLogic.PositionOf(WithRepeats, calls, 350));
        var steps = StepLogic.ForRunning(WithRepeats, calls, 350);
        Assert.Equal(5, steps.Count);   // declared steps, not 9
        Assert.Equal(1, steps[2].CurrentRep);
        Assert.Equal(StepState.Current, steps[2].State);
        Assert.True(steps[2].Repeats);
    }

    [Fact]
    public void Failure_on_a_repetition_is_found_and_counts_the_bee_once()
    {
        var errors = new ErrorDictionary();
        var calls = RepeatStory(errors, failRep: 2);
        Assert.Equal(2, StepLogic.FailedStep(calls));
        Assert.Equal(2, StepLogic.FailedRep(calls));
        var steps = StepLogic.ForBee(WithRepeats, calls);
        Assert.Equal(StepState.Failed, steps[2].State);
        Assert.Equal(2, steps[2].FailedRep);
        Assert.Equal([StepState.Done, StepState.Done, StepState.Failed, StepState.Pending, StepState.Pending], steps.Select(x => x.State));
        Assert.Equal("3rd", ScenarioSteps.Ordinal(3));
        Assert.Equal("11th", ScenarioSteps.Ordinal(11));
        Assert.Equal("22nd", ScenarioSteps.Ordinal(22));
    }

    [Fact]
    public void Fake_checkout_repeats_open_product_five_times_and_fails_on_a_later_repetition()
    {
        var source = new FakeRunSource(new ScenarioCatalog([new HttpGetScenario(), new HttpPostJsonScenario(), new CustomRequestScenario(), new UserJourneyScenario()]));
        var s = source.Current;
        Assert.Equal(5, s.StepsOf(0).Count);
        Assert.Equal(5, s.StepsOf(0).Times(2));
        var openFailed = s.Store.Bees(0).Where(b => b.Failed && b.FailedStep == 2).ToList();
        Assert.NotEmpty(openFailed);
        Assert.All(openFailed, b => Assert.Equal(2, StepLogic.FailedRep(s.Store.Detail(b.Sequence)?.Calls ?? [new FakeCall(0, "x", 0, 1, null, 0, false, 0, Step: 2, Rep: 2)])));
        source.Dispose();
    }

    [Fact]
    public void Dynamic_repeats_without_upfront_total_show_ordinal_and_actual_count()
    {
        var steps = new ScenarioSteps(["Log in", "Open product", "Pay"]);
        Assert.Equal("Step 2 of 3 · Open product · 2nd time", steps.Position(1, 1));
        Assert.Equal("Step 2 of 3 · Open product", steps.Position(1, 0));

        var err = new ErrorDictionary();
        var calls = new List<FakeCall>
        {
            new(0, "login", 0, 20, 200, ErrorDictionary.None, false, 0, Errors: err, Step: 0, Rep: 0),
            new(1, "open-1", 20, 20, 200, ErrorDictionary.None, false, 0, Errors: err, Step: 1, Rep: 0),
            new(2, "open-2", 40, 20, 200, ErrorDictionary.None, false, 0, Errors: err, Step: 1, Rep: 1),
            new(3, "open-3", 60, 20, 500, err.Intern("404"), false, 0, Errors: err, Step: 1, Rep: 2),
        };
        var views = StepLogic.ForBee(steps, calls, failedStep: 1, failedRep: 2, error: "404");
        Assert.False(views[1].HasKnownTotal);
        Assert.True(views[1].Repeats);
        Assert.Equal(3, views[1].Times);
        Assert.Equal(StepState.Failed, views[1].State);
        Assert.Equal(2, views[1].FailedRep);

        // Live mode running bee without streamed calls uses CurrentStep / CurrentRep directly.
        var liveRunning = new RunningBee(9, 0, 1.0, [], CurrentStep: 1, CurrentRep: 2);
        Assert.Equal("Step 2 of 3 · Open product · 3rd time", StepLogic.PositionOf(steps, liveRunning, 500));
        var runningViews = StepLogic.ForRunning(steps, liveRunning, 500);
        Assert.Equal(StepState.Done, runningViews[0].State);
        Assert.Equal(StepState.Current, runningViews[1].State);
        Assert.Equal(2, runningViews[1].CurrentRep);
        Assert.Equal(StepState.Pending, runningViews[2].State);

        // Chronological step visits render each visit in execution order with its own data, plus unreached steps at the bottom.
        var visits = new[]
        {
            new Beesharp.Services.RecordedStepVisit(0, 0, null),
            new Beesharp.Services.RecordedStepVisit(1, 0, "{\"productId\":\"SKU-001\"}"),
            new Beesharp.Services.RecordedStepVisit(1, 1, "{\"productId\":\"SKU-002\"}"),
            new Beesharp.Services.RecordedStepVisit(1, 2, "{\"productId\":\"SKU-003\"}"),
        };
        var chrono = StepLogic.ForBee(steps, calls, failedStep: 1, failedRep: 2, error: "404", stepVisits: visits);
        Assert.Equal(5, chrono.Count); // 4 visits + 1 unreached ("Pay")
        Assert.Equal(["Log in", "Open product", "Open product", "Open product", "Pay"], chrono.Select(v => v.Name).ToArray());
        Assert.Equal([StepState.Done, StepState.Done, StepState.Done, StepState.Failed, StepState.Pending], chrono.Select(v => v.State).ToArray());
        Assert.Equal("productId: SKU-001", StepLogic.FormatStepData(chrono[1].RepData![0]));
        Assert.Equal("productId: SKU-002", StepLogic.FormatStepData(chrono[2].RepData![0]));
        Assert.Equal(3, chrono[3].VisitNumber);
    }
}
