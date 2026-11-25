using System.Text;
using Beesharp.Services;

namespace Beesharp.Cli.Runs;

public sealed record CompareEntrySnapshot(
    int? Entry,
    string ScenarioId,
    string Label,
    int TotalBees,
    int OkBees,
    int FailedBees,
    int CanceledBees,
    int SlowBees,
    double ErrorRatePercent,
    double SlowRatePercent,
    double? MinMs,
    double? P50Ms,
    double? P95Ms,
    double? MaxMs,
    double? AvgMs);

public sealed record CompareMetricsDiff(
    int TotalBeesDelta,
    int OkBeesDelta,
    int FailedBeesDelta,
    int CanceledBeesDelta,
    int SlowBeesDelta,
    double ErrorRateDeltaPp,
    double SlowRateDeltaPp,
    double? MinMsDelta,
    double? P50MsDelta,
    double? P50PctChange,
    double? P95MsDelta,
    double? P95PctChange,
    double? MaxMsDelta,
    double? MaxPctChange,
    double? AvgMsDelta,
    double? AvgPctChange);

public sealed record CompareScenarioDiffDto(
    string Status,
    string ScenarioId,
    string Label,
    int? EntryA,
    int? EntryB,
    CompareEntrySnapshot? Baseline,
    CompareEntrySnapshot? Candidate,
    CompareMetricsDiff? Diff);

public sealed record CompareRunMetaDto(
    string RunId,
    string State,
    DateTimeOffset StartedUtc,
    double ElapsedSeconds,
    string? Error,
    CompareEntrySnapshot Overall);

public sealed record RunCompareReport(
    CompareRunMetaDto Baseline,
    CompareRunMetaDto Candidate,
    CompareMetricsDiff OverallDiff,
    IReadOnlyList<CompareScenarioDiffDto> Scenarios)
{
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run comparison: {Baseline.RunId} (baseline A) -> {Candidate.RunId} (candidate B)");
        sb.AppendLine($"  Baseline A   {Baseline.State}, {RunSummary.Duration(Baseline.ElapsedSeconds)}, {Baseline.Overall.TotalBees} bees");
        sb.AppendLine($"  Candidate B  {Candidate.State}, {RunSummary.Duration(Candidate.ElapsedSeconds)}, {Candidate.Overall.TotalBees} bees");
        sb.AppendLine();

        sb.AppendLine("  Overall");
        WriteSection(sb, Baseline.Overall, Candidate.Overall, OverallDiff);

        if (Scenarios.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("  Per scenario");
            foreach (var s in Scenarios)
            {
                var heading = s.Status switch
                {
                    "added" => $"    #{s.EntryB} {s.Label} ({s.ScenarioId}) [added in B]",
                    "removed" => $"    #{s.EntryA} {s.Label} ({s.ScenarioId}) [removed in B]",
                    _ when s.EntryA == s.EntryB => $"    #{s.EntryA} {s.Label} ({s.ScenarioId})",
                    _ => $"    #{s.EntryA} -> #{s.EntryB} {s.Label} ({s.ScenarioId})",
                };
                sb.AppendLine(heading);
                if (s.Baseline is { } a && s.Candidate is { } b && s.Diff is { } d)
                {
                    WriteSection(sb, a, b, d, indent: "      ");
                }
                else if (s.Baseline is { } onlyA)
                {
                    sb.AppendLine($"      A only: {onlyA.TotalBees} bees, {onlyA.FailedBees} failed ({onlyA.ErrorRatePercent:0.0}%), {onlyA.SlowBees} slow, p50 {RunSummary.Ms(onlyA.P50Ms)}, p95 {RunSummary.Ms(onlyA.P95Ms)}, max {RunSummary.Ms(onlyA.MaxMs)}");
                }
                else if (s.Candidate is { } onlyB)
                {
                    sb.AppendLine($"      B only: {onlyB.TotalBees} bees, {onlyB.FailedBees} failed ({onlyB.ErrorRatePercent:0.0}%), {onlyB.SlowBees} slow, p50 {RunSummary.Ms(onlyB.P50Ms)}, p95 {RunSummary.Ms(onlyB.P95Ms)}, max {RunSummary.Ms(onlyB.MaxMs)}");
                }
            }
        }

        return sb.ToString();
    }

    private static void WriteSection(StringBuilder sb, CompareEntrySnapshot a, CompareEntrySnapshot b, CompareMetricsDiff d, string indent = "    ")
    {
        sb.AppendLine($"{indent}{"Metric",-12}  {"Baseline A",12}  {"Candidate B",12}  {"Delta",22}");
        sb.AppendLine($"{indent}{"Bees",-12}  {a.TotalBees,12}  {b.TotalBees,12}  {SignedInt(d.TotalBeesDelta),22}");
        sb.AppendLine($"{indent}{"Failed",-12}  {a.FailedBees,12}  {b.FailedBees,12}  {SignedInt(d.FailedBeesDelta),22}");
        sb.AppendLine($"{indent}{"Slow",-12}  {a.SlowBees,12}  {b.SlowBees,12}  {SignedInt(d.SlowBeesDelta),22}");
        sb.AppendLine($"{indent}{"Error rate",-12}  {Pct(a.ErrorRatePercent),12}  {Pct(b.ErrorRatePercent),12}  {SignedPp(d.ErrorRateDeltaPp),22}");
        sb.AppendLine($"{indent}{"Slow rate",-12}  {Pct(a.SlowRatePercent),12}  {Pct(b.SlowRatePercent),12}  {SignedPp(d.SlowRateDeltaPp),22}");
        sb.AppendLine($"{indent}{"p50",-12}  {RunSummary.Ms(a.P50Ms),12}  {RunSummary.Ms(b.P50Ms),12}  {MsDelta(d.P50MsDelta, d.P50PctChange),22}");
        sb.AppendLine($"{indent}{"p95",-12}  {RunSummary.Ms(a.P95Ms),12}  {RunSummary.Ms(b.P95Ms),12}  {MsDelta(d.P95MsDelta, d.P95PctChange),22}");
        sb.AppendLine($"{indent}{"max",-12}  {RunSummary.Ms(a.MaxMs),12}  {RunSummary.Ms(b.MaxMs),12}  {MsDelta(d.MaxMsDelta, d.MaxPctChange),22}");
    }

    private static string Pct(double p) => $"{p:0.0}%";
    private static string SignedInt(int v) => v > 0 ? $"+{v}" : v.ToString();
    private static string SignedPp(double v) => v > 0 ? $"+{v:0.0} pp" : $"{v:0.0} pp";

    private static string MsDelta(double? deltaMs, double? pctChange)
    {
        if (deltaMs is null) return "-";
        var sign = deltaMs.Value > 0 ? "+" : deltaMs.Value < 0 ? "-" : "";
        var abs = Math.Abs(deltaMs.Value);
        var formatted = abs >= 1000 ? $"{abs / 1000:0.00} s" : $"{abs:0.#} ms";
        var pct = pctChange is { } p ? $" ({(p > 0 ? "+" : "")}{p:0.0}%)" : "";
        return $"{sign}{formatted}{pct}";
    }
}

internal static class RunsComparer
{
    public static RunCompareReport Compare(RunSummaryReport baseline, RunSummaryReport candidate)
    {
        var overallA = Snapshot(null, "", "All", baseline.TotalBees, baseline.OkBees, baseline.FailedBees, baseline.CanceledBees, baseline.SlowBees,
            baseline.MinMs, baseline.P50Ms, baseline.P95Ms, baseline.MaxMs, baseline.AvgMs);
        var overallB = Snapshot(null, "", "All", candidate.TotalBees, candidate.OkBees, candidate.FailedBees, candidate.CanceledBees, candidate.SlowBees,
            candidate.MinMs, candidate.P50Ms, candidate.P95Ms, candidate.MaxMs, candidate.AvgMs);

        var pairs = MatchEntries(baseline.Entries, candidate.Entries);
        return new RunCompareReport(
            new CompareRunMetaDto(baseline.RunId, baseline.State, baseline.StartedUtc, baseline.ElapsedSeconds, baseline.Error, overallA),
            new CompareRunMetaDto(candidate.RunId, candidate.State, candidate.StartedUtc, candidate.ElapsedSeconds, candidate.Error, overallB),
            ComputeDiff(overallA, overallB),
            pairs);
    }

    private static List<CompareScenarioDiffDto> MatchEntries(
        IReadOnlyList<RunSummaryEntryDto> baselineEntries,
        IReadOnlyList<RunSummaryEntryDto> candidateEntries)
    {
        var result = new List<CompareScenarioDiffDto>();
        var usedB = new bool[candidateEntries.Count];
        var matchedA = new bool[baselineEntries.Count];
        var paired = new List<(int IndexA, int IndexB)>();

        // 1. Exact (scenarioId, label) match in order.
        for (var i = 0; i < baselineEntries.Count; i++)
        {
            var a = baselineEntries[i];
            for (var j = 0; j < candidateEntries.Count; j++)
            {
                if (usedB[j]) continue;
                var b = candidateEntries[j];
                if (string.Equals(a.ScenarioId, b.ScenarioId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(a.Label, b.Label, StringComparison.OrdinalIgnoreCase))
                {
                    matchedA[i] = true;
                    usedB[j] = true;
                    paired.Add((i, j));
                    break;
                }
            }
        }

        // 2. Fallback: same scenarioId in order (e.g. label was edited).
        for (var i = 0; i < baselineEntries.Count; i++)
        {
            if (matchedA[i]) continue;
            var a = baselineEntries[i];
            for (var j = 0; j < candidateEntries.Count; j++)
            {
                if (usedB[j]) continue;
                var b = candidateEntries[j];
                if (string.Equals(a.ScenarioId, b.ScenarioId, StringComparison.OrdinalIgnoreCase))
                {
                    matchedA[i] = true;
                    usedB[j] = true;
                    paired.Add((i, j));
                    break;
                }
            }
        }

        paired.Sort((x, y) => x.IndexA.CompareTo(y.IndexA));
        foreach (var (ia, ib) in paired)
        {
            var a = baselineEntries[ia];
            var b = candidateEntries[ib];
            var snapA = Snapshot(a);
            var snapB = Snapshot(b);
            result.Add(new CompareScenarioDiffDto(
                "matched",
                b.ScenarioId,
                b.Label,
                a.Entry,
                b.Entry,
                snapA,
                snapB,
                ComputeDiff(snapA, snapB)));
        }

        for (var i = 0; i < baselineEntries.Count; i++)
        {
            if (matchedA[i]) continue;
            var a = baselineEntries[i];
            result.Add(new CompareScenarioDiffDto("removed", a.ScenarioId, a.Label, a.Entry, null, Snapshot(a), null, null));
        }

        for (var j = 0; j < candidateEntries.Count; j++)
        {
            if (usedB[j]) continue;
            var b = candidateEntries[j];
            result.Add(new CompareScenarioDiffDto("added", b.ScenarioId, b.Label, null, b.Entry, null, Snapshot(b), null));
        }

        return result;
    }

    private static CompareEntrySnapshot Snapshot(RunSummaryEntryDto e) =>
        Snapshot(e.Entry, e.ScenarioId, e.Label, e.TotalBees, e.OkBees, e.FailedBees, e.CanceledBees, e.SlowBees,
            e.MinMs, e.P50Ms, e.P95Ms, e.MaxMs, e.AvgMs);

    private static CompareEntrySnapshot Snapshot(
        int? entry, string scenarioId, string label,
        int total, int ok, int failed, int canceled, int slow,
        double? minMs, double? p50Ms, double? p95Ms, double? maxMs, double? avgMs)
    {
        var errRate = total > 0 ? Math.Round(failed * 100.0 / total, 2) : 0.0;
        var slowRate = total > 0 ? Math.Round(slow * 100.0 / total, 2) : 0.0;
        return new CompareEntrySnapshot(entry, scenarioId, label, total, ok, failed, canceled, slow, errRate, slowRate, minMs, p50Ms, p95Ms, maxMs, avgMs);
    }

    private static CompareMetricsDiff ComputeDiff(CompareEntrySnapshot a, CompareEntrySnapshot b) => new(
        b.TotalBees - a.TotalBees,
        b.OkBees - a.OkBees,
        b.FailedBees - a.FailedBees,
        b.CanceledBees - a.CanceledBees,
        b.SlowBees - a.SlowBees,
        Math.Round(b.ErrorRatePercent - a.ErrorRatePercent, 2),
        Math.Round(b.SlowRatePercent - a.SlowRatePercent, 2),
        Delta(a.MinMs, b.MinMs),
        Delta(a.P50Ms, b.P50Ms),
        PctChange(a.P50Ms, b.P50Ms),
        Delta(a.P95Ms, b.P95Ms),
        PctChange(a.P95Ms, b.P95Ms),
        Delta(a.MaxMs, b.MaxMs),
        PctChange(a.MaxMs, b.MaxMs),
        Delta(a.AvgMs, b.AvgMs),
        PctChange(a.AvgMs, b.AvgMs));

    private static double? Delta(double? a, double? b) =>
        a is { } av && b is { } bv ? Math.Round(bv - av, 2) : null;

    private static double? PctChange(double? a, double? b) =>
        a is > 0 && b is { } bv ? Math.Round((bv - a.Value) * 100.0 / a.Value, 2) : null;
}
