using DuckDB.NET.Data;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Storage;

namespace Beesharp.Storage.Tests;

public class ScenarioStepsStorageTests
{
    [Fact]
    public async Task DuckDb_persists_steps_failed_step_and_call_step_rep()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-steps-{Guid.NewGuid():N}.duckdb");
        try
        {
            await using var storage = new DuckDbRunStorage(dbPath);
            var steps = new IReadOnlyList<StepInfo>[] { [new StepInfo("Log in"), new StepInfo("Pay")] };
            var plan = new LoadPlan { DurationSeconds = 10 };
            storage.EnqueueRunStarted("r-steps", DateTimeOffset.UtcNow, plan, steps);
            storage.EnqueueBeeCompleted(
                "r-steps",
                new ExecutionResult(
                    0, 0, 0.5, 250, 500, 128, "declined",
                    [new RecordedCall("login", 0, 50, 200, 64, null, Step: 0, Rep: 0), new RecordedCall("pay", 60, 190, 500, 64, "declined", Step: 1, Rep: 2)],
                    [new RecordedLog(0.6, "Error", "card declined", null, 1, Step: 1)],
                    Canceled: false,
                    FailedStep: 1,
                    FailedRep: 2),
                beeOutcome: 2,
                slowCalls: 0);
            storage.EnqueueRunFinished("r-steps", DateTimeOffset.UtcNow, "Finished", 1.2, null);
            await storage.FlushAsync();

            var run = await storage.GetRunAsync("r-steps");
            Assert.NotNull(run);
            Assert.Equal(["Log in", "Pay"], run!.Steps![0].Select(s => s.Name).ToArray());
            Assert.Equal(1, run.Bees[0].FailedStep);
            Assert.Equal(2, run.Bees[0].FailedRep);

            var detail = await storage.GetBeeDetailAsync("r-steps", 0);
            Assert.NotNull(detail);
            Assert.Equal(1, detail!.FailedStep);
            Assert.Equal(2, detail.FailedRep);
            Assert.Equal((1, 2), (detail.Calls[1].Step, detail.Calls[1].Rep));
            Assert.Equal(1, detail.Logs[0].Step);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");
        }
    }

    [Fact]
    public async Task DuckDb_migrates_v1_schema_cleanly()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"beesharp-v1-{Guid.NewGuid():N}.duckdb");
        try
        {
            // Seed a v1 schema database (without step columns or run_steps table).
            using (var conn = new DuckDBConnection($"Data Source={dbPath}"))
            {
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE schema_meta (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL);
                    INSERT INTO schema_meta VALUES ('format', 'performancetool-run-db'), ('schema_version', '1');
                    CREATE TABLE runs (run_id VARCHAR PRIMARY KEY, started_utc VARCHAR NOT NULL, finished_utc VARCHAR, state VARCHAR NOT NULL, elapsed_seconds DOUBLE NOT NULL, plan_json VARCHAR NOT NULL, error VARCHAR);
                    CREATE TABLE bees (run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, bee_name VARCHAR NOT NULL, entry INTEGER NOT NULL, started_at DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR, outcome INTEGER NOT NULL, is_failed BOOLEAN NOT NULL, is_slow BOOLEAN NOT NULL, slow_calls INTEGER NOT NULL, call_count INTEGER NOT NULL, log_count INTEGER NOT NULL);
                    CREATE TABLE calls (id BIGINT NOT NULL, run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, call_index INTEGER NOT NULL, name VARCHAR NOT NULL, offset_ms DOUBLE NOT NULL, duration_ms DOUBLE NOT NULL, status_code INTEGER, bytes BIGINT NOT NULL, error VARCHAR);
                    CREATE TABLE logs (id BIGINT NOT NULL, run_id VARCHAR NOT NULL, sequence INTEGER NOT NULL, bee_name VARCHAR NOT NULL, entry INTEGER NOT NULL, at_seconds DOUBLE NOT NULL, level_rank INTEGER NOT NULL, level VARCHAR NOT NULL, message VARCHAR NOT NULL, exception VARCHAR, call_index INTEGER, bee_outcome INTEGER NOT NULL, is_failed BOOLEAN NOT NULL, is_slow BOOLEAN NOT NULL);
                    INSERT INTO runs VALUES ('old-run', '2026-11-01T00:00:00+00:00', NULL, 'Finished', 5.0, '{"scenarios":[]}', NULL);
                    INSERT INTO bees VALUES ('old-run', 0, 'B1', 0, 0.1, 42.0, 200, 10, NULL, 0, FALSE, FALSE, 0, 0, 0);
                    """;
                cmd.ExecuteNonQuery();
            }

            await using var storage = new DuckDbRunStorage(dbPath);
            var old = await storage.GetRunAsync("old-run");
            Assert.NotNull(old);
            Assert.Null(old!.Steps);
            Assert.Equal(-1, old.Bees[0].FailedStep);
            Assert.Equal(-1, old.Bees[0].FailedRep);
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
            if (File.Exists(dbPath + ".wal")) File.Delete(dbPath + ".wal");
        }
    }
}
