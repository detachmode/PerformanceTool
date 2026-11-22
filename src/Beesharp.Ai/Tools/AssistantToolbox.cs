using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.AI;
using Beesharp.Ai.Contracts;
using Beesharp.Services.Scenarios;

namespace Beesharp.Ai;

/// <summary>
/// The tools of the assistant for one turn (brain 0047, 0048, 0049). Every public method marked <see cref="AssistantToolAttribute"/> becomes an
/// <see cref="AIFunction"/>: its name and <see cref="DescriptionAttribute"/>s are what the model reads, so they use the tool's own language
/// (scenario, scenario entry, bee, call, run, environment, preset, preflight). Entries and bees are numbered from 1, like the UI shows them.
/// A tool answers with a small, bounded object and tells the <see cref="TurnRecorder"/> what to show the user (a chip or a card).
/// <para>To add a tool: add a method with <c>[AssistantTool("name")]</c> and a <c>[Description]</c> in the file of its area, bound its output, call
/// <see cref="TurnRecorder.Read"/> or <see cref="TurnRecorder.Change"/> with a one-line summary. Nothing else to register.</para>
/// </summary>
public sealed partial class AssistantToolbox
{
    private readonly IPlanWorkspacePort plan;
    private readonly IRunPort run;
    private readonly IRunHistoryPort history;
    private readonly IEnvironmentPort environments;
    private readonly IScriptPort scripts;
    private readonly ScenarioCatalog catalog;
    private readonly TurnRecorder rec;

    public AssistantToolbox(
        IPlanWorkspacePort plan,
        IRunPort run,
        IRunHistoryPort history,
        IEnvironmentPort environments,
        IScriptPort scripts,
        ScenarioCatalog catalog,
        TurnRecorder recorder)
    {
        this.plan = plan;
        this.run = run;
        this.history = history;
        this.environments = environments;
        this.scripts = scripts;
        this.catalog = catalog;
        rec = recorder;
    }

    /// <summary>All tools as <see cref="AIFunction"/>s, bound to this toolbox (and so to this turn's recorder).</summary>
    public IReadOnlyList<AIFunction> Build() =>
        typeof(AssistantToolbox)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => (Method: m, Attr: m.GetCustomAttribute<AssistantToolAttribute>()))
            .Where(x => x.Attr is not null)
            .OrderBy(x => x.Attr!.Name, StringComparer.Ordinal)
            .Select(x => (AIFunction)AIFunctionFactory.Create(x.Method, this, new AIFunctionFactoryOptions { Name = x.Attr!.Name }))
            .ToList();

    // ---------- helpers ----------

    private static int ToIndex(int number, string what) =>
        number >= 1 ? number - 1 : throw new ArgumentException($"{what} numbers start at 1.");

    private async Task<string> RunIdAsync(string? runId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(runId)) return runId.Trim();
        return await history.LatestRunIdAsync(ct) ?? throw new InvalidOperationException("There is no run yet.");
    }

    private static BeeOutcomeFilter ParseOutcome(string? outcome) => outcome?.Trim().ToLowerInvariant() switch
    {
        null or "" or "all" => BeeOutcomeFilter.All,
        "ok" or "good" or "successful" => BeeOutcomeFilter.Ok,
        "slow" => BeeOutcomeFilter.Slow,
        "failed" or "fail" or "error" => BeeOutcomeFilter.Failed,
        _ => throw new ArgumentException("outcome must be all, ok, slow or failed."),
    };

    private static readonly string[] LogLevels = ["Trace", "Debug", "Information", "Warning", "Error"];

    private static IReadOnlyList<string>? ParseLevels(IReadOnlyList<string>? levels)
    {
        if (levels is not { Count: > 0 }) return null;
        return levels.Select(l => LogLevels.FirstOrDefault(x => x.StartsWith(l.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown log level '{l}'. Use Trace, Debug, Information, Warning or Error.")).Distinct().ToList();
    }

    private ChipLinkDto? RunLink(string? runId = null, int? entry = null, int? bee = null) =>
        new("run", new ViewQueryDto(RunId: runId, Entry: entry, Bee: bee));
}
