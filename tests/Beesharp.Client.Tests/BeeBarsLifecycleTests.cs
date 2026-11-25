using Beesharp.Client.Chart;
using Beesharp.Client.Runs;
using Beesharp.Services;

namespace Beesharp.Client.Tests;

/// <summary>
/// Stories for <see cref="BeeBars"/>: grouping large runs into time buckets, appending live bees in flight,
/// resolving bees in place while the chart is frozen for aiming (brain 0026), drilling into a bucket,
/// and stepping forward/backward through failed or slow bees (brain 0036).
/// </summary>
public sealed class BeeBarsLifecycleTests
{
    [Fact]
    public void Large_runs_bucket_bees_by_time_and_bucket_drill_down_returns_matching_bees()
    {
        var errors = new ErrorDictionary();
        var errId = errors.Intern("HTTP 500");

        // Generate 80 bees spread across 40 seconds, with one failure and one slow bee.
        var bees = new List<BeeRecord>();
        for (var i = 0; i < 80; i++)
        {
            var isFail = i == 25;
            var isSlow = i == 60;
            bees.Add(new BeeRecord(
                Sequence: i,
                Entry: 0,
                StartedAt: i * 0.5,
                DurationMs: isSlow ? 900 : isFail ? 400 : 120,
                SlowCalls: isSlow ? 1 : 0,
                ErrorId: isFail ? errId : 0,
                Errors: errors,
                RatioCode: isSlow ? SlowCallRule.Encode(3.0) : SlowCallRule.Encode(1.2)));
        }

        // Requesting at most 20 bars forces time bucketing (bucketWidth > 0).
        var bars = BeeBars.Build(bees, bees.Count, BeeShow.All, out var total, out var bucketWidth, maxBars: 20);
        Assert.Equal(80, total);
        Assert.True(bucketWidth > 0);
        Assert.True(bars.Count <= 25);
        Assert.Equal(80, bars.Sum(b => b.Count));
        Assert.Equal(1, bars.Sum(b => b.Failed));
        Assert.Equal(1, bars.Sum(b => b.Slow));
        Assert.Equal(900, bars.Max(b => b.MaxMs));

        // Drilling into the bucket containing bee #25 (started at 12.5s) returns its bees.
        var bucketStart = BeeBars.BucketOf(12.5, bucketWidth);
        var inBucket = BeeBars.BucketBees(bees, bees.Count, BeeShow.All, bucketStart, bucketWidth);
        Assert.Contains(inBucket, b => b.Sequence == 25);
        Assert.Single(BeeBars.BucketBees(bees, bees.Count, BeeShow.Failed, bucketStart, bucketWidth));
    }

    [Fact]
    public void Frozen_chart_resolves_running_bees_in_place_for_both_single_bars_and_time_buckets()
    {
        var errors = new ErrorDictionary();
        var errId = errors.Intern("timeout");

        // --- Story A: Single-bar mode (bucketWidth == 0) ---
        var bees = new List<BeeRecord>
        {
            new(0, 0, 1.0, 100, 0, 0, errors),
            new(1, 0, 2.0, 110, 0, 0, errors),
        };
        var runningAtFreeze = new List<RunningBee>
        {
            new(2, 0, 3.0, []),
            new(3, 0, 3.5, []),
        };

        var bars = BeeBars.Build(bees, bees.Count, BeeShow.All, out _, out var singleWidth, maxBars: 50);
        Assert.Equal(0, singleWidth);
        var withLive = BeeBars.WithRunning(bars, runningAtFreeze, singleWidth, elapsed: 4.0);
        Assert.Equal(4, withLive.Count);
        Assert.True(withLive[2].IsRunningBee);
        Assert.Equal(2, withLive[2].Sequence);

        // Bee #2 finishes with an error while the user is aiming; ResolveFinished updates index 2 in place.
        bees.Add(new BeeRecord(2, 0, 3.0, 850, 0, errId, errors));
        var resolvedSingle = BeeBars.ResolveFinished(withLive, runningAtFreeze, bees, frozenCount: 2, nowCount: 3, singleWidth);
        Assert.Equal(4, resolvedSingle.Count);
        Assert.False(resolvedSingle[2].IsRunningBee);
        Assert.Equal(1, resolvedSingle[2].Failed);
        Assert.Equal(850, resolvedSingle[2].MaxMs);
        Assert.True(resolvedSingle[3].IsRunningBee); // bee #3 is still flying

        // --- Story B: Bucketed mode (bucketWidth > 0) ---
        var bucketBees = new List<BeeRecord>();
        for (var i = 0; i < 40; i++)
            bucketBees.Add(new BeeRecord(i, 0, i * 1.0, 100, 0, 0, errors));
        var bucketBars = BeeBars.Build(bucketBees, bucketBees.Count, BeeShow.All, out _, out var bw, maxBars: 10);
        Assert.True(bw > 0);

        var flyingInBucket = new List<RunningBee> { new(40, 0, 39.5, []) };
        var bucketWithLive = BeeBars.WithRunning(bucketBars, flyingInBucket, bw, elapsed: 41.0, gridFirst: bucketBars[0].BucketStart);
        Assert.Equal(1, bucketWithLive.Sum(b => b.Running));

        // Bee #40 finishes slow; ResolveFinished moves it from Running -> Slow inside its bucket.
        bucketBees.Add(new BeeRecord(40, 0, 39.5, 620, SlowCalls: 1, ErrorId: 0, Errors: errors, RatioCode: SlowCallRule.Encode(2.5)));
        var resolvedBucket = BeeBars.ResolveFinished(bucketWithLive, flyingInBucket, bucketBees, frozenCount: 40, nowCount: 41, bw, gridFirst: bucketBars[0].BucketStart);
        Assert.Equal(0, resolvedBucket.Sum(b => b.Running));
        Assert.Equal(1, resolvedBucket.Sum(b => b.Slow));
        Assert.Equal(41, resolvedBucket.Sum(b => b.Count));
    }

    [Fact]
    public void Stepping_through_failed_bees_navigates_forward_backward_and_wraps_around()
    {
        var errors = new ErrorDictionary();
        var errId = errors.Intern("boom");

        Assert.Null(BeeBars.Step([], 0, BeeOutcome.Failed, null, +1));

        var bees = new List<BeeRecord>
        {
            new(10, 0, 1, 100, 0, 0, errors),
            new(11, 0, 2, 100, 0, errId, errors),
            new(12, 0, 3, 100, 0, 0, errors),
            new(13, 0, 4, 100, 0, errId, errors),
        };

        // From null: forward picks the first failed (#11); backward picks the last failed (#13).
        Assert.Equal(11, BeeBars.Step(bees, bees.Count, BeeOutcome.Failed, null, +1)!.Value.Sequence);
        Assert.Equal(13, BeeBars.Step(bees, bees.Count, BeeOutcome.Failed, null, -1)!.Value.Sequence);

        // Stepping forward from #11 -> #13, then wrapping around -> #11.
        Assert.Equal(13, BeeBars.Step(bees, bees.Count, BeeOutcome.Failed, 11, +1)!.Value.Sequence);
        Assert.Equal(11, BeeBars.Step(bees, bees.Count, BeeOutcome.Failed, 13, +1)!.Value.Sequence);

        // Asking for an outcome that never occurred returns null.
        Assert.Null(BeeBars.Step(bees, bees.Count, BeeOutcome.Slow, 11, +1));
    }
}
