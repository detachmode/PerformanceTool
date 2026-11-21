using System.Text.Json.Nodes;
using PerformanceTool.Ai.Contracts;
using PerformanceTool.Services;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Ai;

/// <summary>Marks a method of <see cref="AssistantToolbox"/> as a tool the model can call. The name is what the model sees.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AssistantToolAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>Limits and small helpers shared by the tools: every tool answer is bounded so long runs never flood the model's context.</summary>
internal static class Bounds
{
    public const int DefaultList = 20;
    public const int MaxList = 100;
    public const int MaxLogMessage = 300;
    public const int MaxScriptChars = 20_000;
    public const int MaxTimelineBees = 60;
    public const int CardLogs = 6;
    public const int CardBees = 10;

    public static int Limit(int? limit, int fallback = DefaultList) => Math.Clamp(limit ?? fallback, 1, MaxList);
    public static int Offset(int? offset) => Math.Max(0, offset ?? 0);

    public static string Cut(string? text, int max = MaxLogMessage) =>
        text is null ? "" : text.Length <= max ? text : text[..max] + "…";
}

internal static class SecretMask
{
    private static readonly string[] SecretWords = ["token", "secret", "password", "passwd", "pwd", "key", "auth", "credential", "bearer", "cookie", "session"];
    public const string Dots = "••••";

    public static bool LooksSecret(string key) =>
        SecretWords.Any(w => key.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Secret-looking values show only their first two characters; everything else is shown as is.</summary>
    public static string Mask(string key, string value) =>
        !LooksSecret(key) || value.Length == 0 ? value : value.Length <= 8 ? Dots : value[..2] + Dots;

    public static bool IsMasked(string value) => value.Contains('•');
}

internal static class PlanFormat
{
    public static string Label(ScenarioRunConfig c) => string.IsNullOrWhiteSpace(c.Label) ? c.ScenarioId : c.Label;

    public static string Rate(double perMinute) => perMinute.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The plan as the model reads it. Entries are numbered from 1, secret parameter values are left out.</summary>
    public static object View(PlanState state, ScenarioCatalog catalog)
    {
        var problems = state.Plan.Validate(catalog);
        return new
        {
            preset = state.PresetName,
            unsavedChanges = state.HasUnsavedChanges,
            durationSeconds = state.Plan.DurationSeconds,
            maxInFlight = state.Plan.MaxConcurrency,
            timeoutSeconds = state.Plan.TimeoutSeconds,
            preflightScriptId = state.Plan.PreflightScriptId,
            entries = state.Plan.Scenarios.Select((c, i) => EntryView(c, i, catalog)).ToList(),
            problems,
        };
    }

    public static object EntryView(ScenarioRunConfig c, int index, ScenarioCatalog catalog)
    {
        var parameters = (JsonObject)c.Parameters.DeepClone();
        catalog.Find(c.ScenarioId)?.Spec.StripSecrets(parameters);
        return new
        {
            entry = index + 1,
            scenarioId = c.ScenarioId,
            label = Label(c),
            pattern = c.Pattern.ToString().ToLowerInvariant(),
            beesPerMinute = c.Pattern == SpawningPattern.Linear ? c.RatePerMinute : (double?)null,
            beesPerBurst = c.Pattern == SpawningPattern.Bursts ? c.BeesPerBurst : (int?)null,
            burstsPerMinute = c.Pattern == SpawningPattern.Bursts ? c.BurstsPerMinute : (double?)null,
            effectiveBeesPerMinute = c.EffectiveRatePerMinute(),
            parameters,
        };
    }

    /// <summary>A copy of the plan without literal secret parameter values (so a diff never shows them).</summary>
    public static LoadPlan Stripped(LoadPlan plan, ScenarioCatalog catalog)
    {
        var copy = plan.Clone();
        foreach (var c in copy.Scenarios) catalog.Find(c.ScenarioId)?.Spec.StripSecrets(c.Parameters);
        return copy;
    }

    /// <summary>A positional diff of two plans, for the plan-changes card.</summary>
    public static List<ViewPlanChangeDto> Diff(LoadPlan before, LoadPlan after)
    {
        var result = new List<ViewPlanChangeDto>();
        var settings = new List<string>();
        if (before.DurationSeconds != after.DurationSeconds) settings.Add($"duration {before.DurationSeconds} → {after.DurationSeconds} s");
        if (before.MaxConcurrency != after.MaxConcurrency) settings.Add($"max in flight {before.MaxConcurrency} → {after.MaxConcurrency}");
        if (before.TimeoutSeconds != after.TimeoutSeconds) settings.Add($"timeout {before.TimeoutSeconds} → {after.TimeoutSeconds} s");
        if (before.PreflightScriptId != after.PreflightScriptId) settings.Add($"preflight {before.PreflightScriptId ?? "none"} → {after.PreflightScriptId ?? "none"}");
        if (settings.Count > 0) result.Add(new ViewPlanChangeDto("changed", 0, "Plan settings", settings));

        for (var i = 0; i < Math.Max(before.Scenarios.Count, after.Scenarios.Count); i++)
        {
            if (i >= before.Scenarios.Count)
            {
                var a = after.Scenarios[i];
                result.Add(new ViewPlanChangeDto("added", i + 1, Label(a), [$"{Rate(a.EffectiveRatePerMinute())} bees/min"]));
            }
            else if (i >= after.Scenarios.Count)
                result.Add(new ViewPlanChangeDto("removed", i + 1, Label(before.Scenarios[i]), []));
            else
            {
                var b = before.Scenarios[i];
                var a = after.Scenarios[i];
                var details = new List<string>();
                if (b.Label != a.Label) details.Add($"label “{b.Label}” → “{a.Label}”");
                if (b.Pattern != a.Pattern) details.Add($"pattern {b.Pattern.ToString().ToLowerInvariant()} → {a.Pattern.ToString().ToLowerInvariant()}");
                if (b.EffectiveRatePerMinute() != a.EffectiveRatePerMinute())
                    details.Add($"rate {Rate(b.EffectiveRatePerMinute())} → {Rate(a.EffectiveRatePerMinute())} bees/min");
                if (b.Pattern == SpawningPattern.Bursts && a.Pattern == SpawningPattern.Bursts && (b.BeesPerBurst != a.BeesPerBurst || b.BurstsPerMinute != a.BurstsPerMinute))
                    details.Add($"burst {b.BeesPerBurst} × {Rate(b.BurstsPerMinute)}/min → {a.BeesPerBurst} × {Rate(a.BurstsPerMinute)}/min");
                foreach (var key in b.Parameters.Select(p => p.Key).Union(a.Parameters.Select(p => p.Key)).Distinct())
                {
                    var bv = b.Parameters[key]?.ToJsonString();
                    var av = a.Parameters[key]?.ToJsonString();
                    if (bv != av) details.Add($"{key}: {Bounds.Cut(bv ?? "none", 60)} → {Bounds.Cut(av ?? "none", 60)}");
                }
                if (details.Count > 0) result.Add(new ViewPlanChangeDto("changed", i + 1, Label(a), details));
            }
        }
        return result;
    }
}
