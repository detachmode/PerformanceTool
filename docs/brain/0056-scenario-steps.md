# 0056. Scenario steps: known up front, shown as a stepper

- Status: Proposed (user request: steps known before the run; flying bee shows "step N of M"; a failed bee shows the step it failed at; all bees that failed at one step). Code API and UI details are Claude's proposal.
- Date: 2026-11-22

## What
A scenario can be split into named **steps** (e.g. *Log in → Search → Add to cart → Pay*). Steps sit between the
scenario and its calls: a step groups the calls a bee makes in that part of the story. The scenario **declares its
steps before the run**, so the UI knows "step 2 of 4" for a bee in flight and can show the steps not reached yet.

**Code (proposal, not decided).** Preferred: the steps are an `enum`, and the bee's code stays one linear method
(local variables like a login token flow on naturally):
```csharp
public enum CheckoutStep { [Display(Name = "Log in")] LogIn, Search, [Display(Name = "Add to cart")] AddToCart, Pay }

public sealed class CheckoutScenario : Scenario<CheckoutParameters, CheckoutStep>
{
    protected override async Task<ScenarioOutcome> ExecuteAsync(CheckoutParameters p, ScenarioContext ctx, CancellationToken ct)
    {
        var token = await ctx.Step(CheckoutStep.LogIn, () => LogInAsync(p, ctx, ct));
        var hits  = await ctx.Step(CheckoutStep.Search, () => SearchAsync(token, p.Query, ctx, ct));
        ...
    }
}
```
The enum gives the list and order up front (compile-time checked names, `[Display]` like the parameter editor).
Scripts: `Scenario.Define<P>("Checkout").Steps("Log in", "Search", "Add to cart", "Pay")` and `ctx.Step("Search", ...)`;
an undeclared step name is an error in the test run. A scenario without steps works as today (one implicit step).

Alternative (result types): a typed chain where each step returns a result that feeds the next,
`.Step("Log in", ...).Then("Search", token => ...)`, a failed result stops the bee at that step. More ceremony;
kept as an option for the user to choose.

**C# 15 unions** (ship with .NET 11, the `union` keyword, exhaustive `switch`) fit the step result and the bee's
progress, e.g. `union StepResult(Passed, Failed, Skipped)`. Our agent environment only has the .NET 10 SDK, and the
feature's preview/stable status in .NET 11 must be checked first, so the first version would use a closed record
hierarchy with the same shape and switch to `union` later.

**UI (draft, fake data):** a vertical stepper like Android's "Free up space" screen: numbered circles joined by a
line; a done step gets a check, the current step a spinner, a failed step red, steps not reached greyed out.
- Bee detail: the stepper with each step's time; calls fold out under their step (calm UI, brain 0016).
- Bee in flight: "Step 2 of 4 · Search" wherever the bee is shown.
- Scenario popup: the scenario's steps with counts per step (in flight, failed); a failed count opens the bees that
  failed at that step (brain 0029).

## Why
A scenario is a whole user story (`docs/context.md`); with up to ~50 calls per bee, "which part of the story broke"
is the first question when a bee fails. Steps known before the run make progress and "not reached" visible, which a
list of recorded calls cannot.

## Consequences
- New term **Step** in `docs/context.md`; **Call** no longer means "one step" there.
- Runner and wire format later get a step index per call and per bee (failed step); this draft only uses fake data.
- Open: enum vs chain style; whether steps nest; how a scenario with `Repeat` counts its steps.

## Implementation notes (UI draft V)
Fake data and client only; status stays Proposed. Screenshots: `docs/screenshots/2026-11-22-scenario-steps/`.
- **Data (brain 0021 kept):** each fake call carries a step index (`FakeCall.Step`) and a repetition index (`Rep`); a finished bee keeps one int, `BeeRecord.FailedStep` (-1 = none). `RunStore.FailedPerStep(entry)` counts incrementally. The declared steps are `ScenarioSteps` (`RunSnapshot.StepsOf(entry)`), logic in `Runs/ScenarioSteps.cs` (`StepLogic`: current step and repetition, failed step, per-step counts, `StepFilter`).
- **Fake scenarios:** Checkout (Log in, Search, Open product x5, Add to cart, Pay; failures on the 3rd Open product and at Pay), Guest checkout (5 steps, fails at Pay), Sign up and first order (3 steps); Browse categories and Order history have no steps and look as before.
- **UI:** `Chart/StepList.razor` (vertical stepper, calls folded by default), steps strip in the scenario popup (folded by default, opens with a filter; counts only when above 0; clicking filters the chart, list and live feed; chip in the side head), one quiet "Most failed at X (n)" line on the overview card, "Step N of M" for bees in flight.
- **Repetitions (user feedback):** a step that runs several times shows once with a quiet "x5"; its repetitions fold out like its calls; "Step 3 of 5 - Open product (2 of 5)" for a bee in flight, "failed at Open product, 3rd time" for a failed one. The popup counts bees, not repetitions. **Repetitions are consecutive only in this draft**; whether a bee may loop back to an earlier step is an open question for the user. The repeat count (5) is a constant of the fake profile here (the real one would come from a scenario parameter).
- Colour stays failed = red, slow = amber (a step with a slow call has an amber time), active = blue, done = neutral grey check.
