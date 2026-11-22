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

**C# 15 unions** (.NET 11 and C# 15 shipped on 2026-11-10; the `union` keyword, exhaustive `switch`) fit the step
result and the bee's progress, e.g. `union StepResult(Passed, Failed, Skipped)`. CI builds with .NET 11, so we can use
them. Limits: the cloud agent environment has only the .NET 10 SDK (its network blocks `builds.dotnet.microsoft.com`,
apt has no .NET 11 yet), so agents cannot compile `union` code until that host is allowed; and confirm in the official
release notes that unions left preview (no `LangVersion=preview` in a released tool).

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
