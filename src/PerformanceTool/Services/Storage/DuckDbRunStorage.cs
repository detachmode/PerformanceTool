using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DuckDB.NET.Data;
using PerformanceTool.Services;

namespace PerformanceTool.Services.Storage;

/// <summary>
/// High-throughput DuckDB columnar run storage (brain 0046): drains a <see cref="Channel{T}"/> via a dedicated
/// background writer using DuckDB's native <see cref="DuckDBAppender"/> for bulk ingestion (tens to hundreds of
/// thousands of log rows/second), while serving fast indexed/columnar filtered queries and native
/// Parquet / DuckDB / CSV / JSON run exports.
/// </summary>
public sealed class DuckDbRunStorage : IRunStorage
{
    public const int CurrentSchemaVersion = 1;

    private const int MaxBatchSize = 2000;
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly DuckDBConnection conn;
    private readonly Lock dbLock = new();
    private readonly Channel<WriteOp> queue;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task writerTask;
    private long nextCallId;
    private long nextLogId;

    public string ProviderName => "DuckDB";
    public string DatabasePath { get; }

    private abstract record WriteOp;
    private sealed record StartRunOp(string RunId, DateTimeOffset StartedUtc, string PlanJson) : WriteOp;
    private sealed record ActorOp(string RunId, ExecutionResult Result, int ActorOutcome, int SlowCalls) : WriteOp;
    private sealed record FinishRunOp(string RunId, DateTimeOffset FinishedUtc, string State, double ElapsedSeconds, string? Error) : WriteOp;
    private sealed record FlushOp(TaskCompletionSource Done) : WriteOp;

    public DuckDbRunStorage(IConfiguration configuration, IWebHostEnvironment env)
        : this(ResolvePath(configuration, env))
    {
    }

    public DuckDbRunStorage(string dbPath)
    {
        DatabasePath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        conn = new DuckDBConnection($"Data Source={dbPath}");
        conn.Open();
        InitializeSchema();

        queue = Channel.CreateUnbounded<WriteOp>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        writerTask = Task.Run(WriterLoopAsync, CancellationToken.None);
    }

    private static string ResolvePath(IConfiguration configuration, IWebHostEnvironment env)
    {
        var configured = configuration["Storage:DuckDbPath"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "data", "perftool.duckdb")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    private void InitializeSchema()
    {
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                CREATE TABLE IF NOT EXISTS schema_meta (
                    key VARCHAR PRIMARY KEY,
                    value VARCHAR NOT NULL
                );
                DELETE FROM schema_meta WHERE key IN ('format', 'schema_version');
                INSERT INTO schema_meta(key, value) VALUES
                    ('format', 'performancetool-run-db'),
                    ('schema_version', '{CurrentSchemaVersion}');

                CREATE TABLE IF NOT EXISTS runs (
                    run_id VARCHAR PRIMARY KEY,
                    started_utc VARCHAR NOT NULL,
                    finished_utc VARCHAR,
                    state VARCHAR NOT NULL,
                    elapsed_seconds DOUBLE NOT NULL,
                    plan_json VARCHAR NOT NULL,
                    error VARCHAR
                );

                CREATE TABLE IF NOT EXISTS actors (
                    run_id VARCHAR NOT NULL,
                    sequence INTEGER NOT NULL,
                    actor_name VARCHAR NOT NULL,
                    entry INTEGER NOT NULL,
                    started_at DOUBLE NOT NULL,
                    duration_ms DOUBLE NOT NULL,
                    status_code INTEGER,
                    bytes BIGINT NOT NULL,
                    error VARCHAR,
                    outcome INTEGER NOT NULL,
                    is_failed BOOLEAN NOT NULL,
                    is_slow BOOLEAN NOT NULL,
                    slow_calls INTEGER NOT NULL,
                    call_count INTEGER NOT NULL,
                    log_count INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_actors_run_seq ON actors(run_id, sequence);
                CREATE INDEX IF NOT EXISTS idx_actors_run_name ON actors(run_id, actor_name);
                CREATE INDEX IF NOT EXISTS idx_actors_run_failed ON actors(run_id, is_failed);
                CREATE INDEX IF NOT EXISTS idx_actors_run_slow ON actors(run_id, is_slow);

                CREATE TABLE IF NOT EXISTS calls (
                    id BIGINT NOT NULL,
                    run_id VARCHAR NOT NULL,
                    sequence INTEGER NOT NULL,
                    call_index INTEGER NOT NULL,
                    name VARCHAR NOT NULL,
                    offset_ms DOUBLE NOT NULL,
                    duration_ms DOUBLE NOT NULL,
                    status_code INTEGER,
                    bytes BIGINT NOT NULL,
                    error VARCHAR
                );
                CREATE INDEX IF NOT EXISTS idx_calls_run_seq ON calls(run_id, sequence);

                CREATE TABLE IF NOT EXISTS logs (
                    id BIGINT NOT NULL,
                    run_id VARCHAR NOT NULL,
                    sequence INTEGER NOT NULL,
                    actor_name VARCHAR NOT NULL,
                    entry INTEGER NOT NULL,
                    at_seconds DOUBLE NOT NULL,
                    level_rank INTEGER NOT NULL,
                    level VARCHAR NOT NULL,
                    message VARCHAR NOT NULL,
                    exception VARCHAR,
                    call_index INTEGER,
                    actor_outcome INTEGER NOT NULL,
                    is_failed BOOLEAN NOT NULL,
                    is_slow BOOLEAN NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_logs_run_id ON logs(run_id, id);
                CREATE INDEX IF NOT EXISTS idx_logs_run_seq ON logs(run_id, sequence);
                CREATE INDEX IF NOT EXISTS idx_logs_run_actor_name ON logs(run_id, actor_name);
                CREATE INDEX IF NOT EXISTS idx_logs_run_failed ON logs(run_id, is_failed);
                CREATE INDEX IF NOT EXISTS idx_logs_run_slow ON logs(run_id, is_slow);
                """;
            cmd.ExecuteNonQuery();

            using var maxCmd = conn.CreateCommand();
            maxCmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM calls; SELECT COALESCE(MAX(id), 0) FROM logs;";
            using var r = maxCmd.ExecuteReader();
            if (r.Read()) nextCallId = r.GetInt64(0);
            if (r.NextResult() && r.Read()) nextLogId = r.GetInt64(0);
        }
    }

    public void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan) =>
        queue.Writer.TryWrite(new StartRunOp(runId, startedUtc, JsonSerializer.Serialize(plan, JsonOpts)));

    public void EnqueueActorCompleted(string runId, ExecutionResult result, int actorOutcome, int slowCalls) =>
        queue.Writer.TryWrite(new ActorOp(runId, result, actorOutcome, slowCalls));

    public void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error) =>
        queue.Writer.TryWrite(new FinishRunOp(runId, finishedUtc, state, elapsedSeconds, error));

    public Task FlushAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.Writer.TryWrite(new FlushOp(tcs)))
            return Task.CompletedTask;
        return tcs.Task.WaitAsync(ct);
    }

    private async Task WriterLoopAsync()
    {
        var batch = new List<WriteOp>(MaxBatchSize);
        var flushes = new List<TaskCompletionSource>();

        try
        {
            while (await queue.Reader.WaitToReadAsync(shutdown.Token))
            {
                batch.Clear();
                flushes.Clear();
                var deadline = Environment.TickCount64 + (long)MaxBatchDelay.TotalMilliseconds;

                while (batch.Count < MaxBatchSize)
                {
                    while (batch.Count < MaxBatchSize && queue.Reader.TryRead(out var op))
                    {
                        if (op is FlushOp f) flushes.Add(f.Done);
                        else batch.Add(op);
                    }
                    if (batch.Count >= MaxBatchSize || flushes.Count > 0) break;
                    var remaining = deadline - Environment.TickCount64;
                    if (remaining <= 0) break;
                    await Task.Delay((int)Math.Min(remaining, 15), shutdown.Token);
                }

                if (batch.Count > 0)
                {
                    lock (dbLock)
                        WriteBatch(batch);
                }

                foreach (var f in flushes) f.TrySetResult();
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var f in flushes) f.TrySetResult();
        }
    }

    private void WriteBatch(List<WriteOp> batch)
    {
        var actorsToAppend = new List<ActorOp>();

        foreach (var op in batch)
        {
            switch (op)
            {
                case StartRunOp s:
                    FlushActors(actorsToAppend);
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = """
                            INSERT OR REPLACE INTO runs(run_id, started_utc, finished_utc, state, elapsed_seconds, plan_json, error)
                            VALUES ($1, $2, NULL, 'Running', 0, $3, NULL);
                            """;
                        cmd.Parameters.Add(new DuckDBParameter(s.RunId));
                        cmd.Parameters.Add(new DuckDBParameter(s.StartedUtc.ToString("O")));
                        cmd.Parameters.Add(new DuckDBParameter(s.PlanJson));
                        cmd.ExecuteNonQuery();
                    }
                    break;

                case ActorOp a:
                    actorsToAppend.Add(a);
                    break;

                case FinishRunOp f:
                    FlushActors(actorsToAppend);
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = """
                            UPDATE runs
                            SET finished_utc = $1, state = $2, elapsed_seconds = $3, error = $4
                            WHERE run_id = $5;
                            """;
                        cmd.Parameters.Add(new DuckDBParameter(f.FinishedUtc.ToString("O")));
                        cmd.Parameters.Add(new DuckDBParameter(f.State));
                        cmd.Parameters.Add(new DuckDBParameter(f.ElapsedSeconds));
                        cmd.Parameters.Add(new DuckDBParameter((object?)f.Error ?? DBNull.Value));
                        cmd.Parameters.Add(new DuckDBParameter(f.RunId));
                        cmd.ExecuteNonQuery();
                    }
                    break;
            }
        }

        FlushActors(actorsToAppend);
    }

    private void FlushActors(List<ActorOp> actorsToAppend)
    {
        if (actorsToAppend.Count == 0) return;

        using (var actorApp = conn.CreateAppender("actors"))
        {
            foreach (var a in actorsToAppend)
            {
                var r = a.Result;
                var row = actorApp.CreateRow();
                row.AppendValue(a.RunId)
                   .AppendValue(r.Sequence)
                   .AppendValue($"Actor #{r.Sequence + 1}")
                   .AppendValue(r.Entry)
                   .AppendValue(r.StartedAtSeconds)
                   .AppendValue(r.ElapsedMs)
                   .AppendValue(r.StatusCode)
                   .AppendValue(r.Bytes)
                   .AppendValue(r.Error)
                   .AppendValue(a.ActorOutcome)
                   .AppendValue(a.ActorOutcome == 2)
                   .AppendValue(a.SlowCalls > 0)
                   .AppendValue(a.SlowCalls)
                   .AppendValue(r.Calls?.Count ?? 0)
                   .AppendValue(r.Logs?.Count ?? 0)
                   .EndRow();
            }
        }

        var hasCalls = actorsToAppend.Any(a => a.Result.Calls is { Count: > 0 });
        if (hasCalls)
        {
            using var callApp = conn.CreateAppender("calls");
            foreach (var a in actorsToAppend)
            {
                if (a.Result.Calls is not { Count: > 0 } calls) continue;
                for (var i = 0; i < calls.Count; i++)
                {
                    var c = calls[i];
                    var row = callApp.CreateRow();
                    row.AppendValue(Interlocked.Increment(ref nextCallId))
                       .AppendValue(a.RunId)
                       .AppendValue(a.Result.Sequence)
                       .AppendValue(i)
                       .AppendValue(c.Name)
                       .AppendValue(c.OffsetMs)
                       .AppendValue(c.DurationMs)
                       .AppendValue(c.StatusCode)
                       .AppendValue(c.Bytes)
                       .AppendValue(c.Error)
                       .EndRow();
                }
            }
        }

        var hasLogs = actorsToAppend.Any(a => a.Result.Logs is { Count: > 0 });
        if (hasLogs)
        {
            using var logApp = conn.CreateAppender("logs");
            foreach (var a in actorsToAppend)
            {
                if (a.Result.Logs is not { Count: > 0 } logs) continue;
                var actorName = $"Actor #{a.Result.Sequence + 1}";
                var isFailed = a.ActorOutcome == 2;
                var isSlow = a.SlowCalls > 0;
                for (var i = 0; i < logs.Count; i++)
                {
                    var l = logs[i];
                    var row = logApp.CreateRow();
                    row.AppendValue(Interlocked.Increment(ref nextLogId))
                       .AppendValue(a.RunId)
                       .AppendValue(a.Result.Sequence)
                       .AppendValue(actorName)
                       .AppendValue(a.Result.Entry)
                       .AppendValue(l.AtSeconds)
                       .AppendValue(LevelRank(l.Level))
                       .AppendValue(l.Level)
                       .AppendValue(l.Message)
                       .AppendValue(l.Exception)
                       .AppendValue(l.CallIndex)
                       .AppendValue(a.ActorOutcome)
                       .AppendValue(isFailed)
                       .AppendValue(isSlow)
                       .EndRow();
                }
            }
        }

        actorsToAppend.Clear();
    }

    public Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.run_id, r.started_utc, r.finished_utc, r.state, r.elapsed_seconds, r.error,
                       COALESCE((SELECT COUNT(*) FROM actors a WHERE a.run_id = r.run_id), 0) AS total_actors,
                       COALESCE((SELECT COUNT(*) FROM actors a WHERE a.run_id = r.run_id AND a.is_failed = TRUE), 0) AS failed_actors,
                       COALESCE((SELECT COUNT(*) FROM logs l WHERE l.run_id = r.run_id), 0) AS total_logs
                FROM runs r
                ORDER BY r.started_utc DESC
                LIMIT $1;
                """;
            cmd.Parameters.Add(new DuckDBParameter(Math.Clamp(limit, 1, 500)));

            var list = new List<StoredRunSummary>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new StoredRunSummary(
                    reader.GetString(0),
                    DateTimeOffset.Parse(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
                    reader.GetString(3),
                    reader.GetDouble(4),
                    Convert.ToInt32(reader.GetValue(6)),
                    Convert.ToInt32(reader.GetValue(7)),
                    Convert.ToInt64(reader.GetValue(8)),
                    reader.IsDBNull(5) ? null : reader.GetString(5)));
            }
            return Task.FromResult<IReadOnlyList<StoredRunSummary>>(list);
        }
    }

    public Task<StoredRunData?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            DateTimeOffset startedUtc;
            string state;
            double elapsedSeconds;
            LoadPlan plan;
            string? error;
            long totalLogs;

            using (var rCmd = conn.CreateCommand())
            {
                rCmd.CommandText = """
                    SELECT started_utc, state, elapsed_seconds, plan_json, error,
                           COALESCE((SELECT COUNT(*) FROM logs l WHERE l.run_id = runs.run_id), 0)
                    FROM runs WHERE run_id = $1;
                    """;
                rCmd.Parameters.Add(new DuckDBParameter(runId));
                using var rr = rCmd.ExecuteReader();
                if (!rr.Read()) return Task.FromResult<StoredRunData?>(null);
                startedUtc = DateTimeOffset.Parse(rr.GetString(0));
                state = rr.GetString(1);
                elapsedSeconds = rr.GetDouble(2);
                plan = JsonSerializer.Deserialize<LoadPlan>(rr.GetString(3), JsonOpts) ?? new LoadPlan();
                error = rr.IsDBNull(4) ? null : rr.GetString(4);
                totalLogs = Convert.ToInt64(rr.GetValue(5));
            }

            var actors = new List<StoredActorSummary>();
            using (var aCmd = conn.CreateCommand())
            {
                aCmd.CommandText = """
                    SELECT sequence, entry, started_at, duration_ms, status_code, bytes, error, slow_calls
                    FROM actors WHERE run_id = $1 ORDER BY sequence;
                    """;
                aCmd.Parameters.Add(new DuckDBParameter(runId));
                using var ar = aCmd.ExecuteReader();
                while (ar.Read())
                {
                    actors.Add(new StoredActorSummary(
                        ar.GetInt32(0),
                        ar.GetInt32(1),
                        ar.GetDouble(2),
                        ar.GetDouble(3),
                        ar.IsDBNull(4) ? null : ar.GetInt32(4),
                        ar.GetInt64(5),
                        ar.IsDBNull(6) ? null : ar.GetString(6),
                        ar.GetInt32(7)));
                }
            }

            return Task.FromResult<StoredRunData?>(new StoredRunData(runId, startedUtc, state, elapsedSeconds, plan, actors, totalLogs, error));
        }
    }

    public Task<StoredActorDetail?> GetActorDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            int entry;
            double startedAt, durationMs;
            int? statusCode;
            long bytes;
            string? error;

            using (var actorCmd = conn.CreateCommand())
            {
                actorCmd.CommandText = """
                    SELECT entry, started_at, duration_ms, status_code, bytes, error
                    FROM actors
                    WHERE run_id = $1 AND sequence = $2
                    LIMIT 1;
                    """;
                actorCmd.Parameters.Add(new DuckDBParameter(runId));
                actorCmd.Parameters.Add(new DuckDBParameter(sequence));
                using var reader = actorCmd.ExecuteReader();
                if (!reader.Read()) return Task.FromResult<StoredActorDetail?>(null);
                entry = reader.GetInt32(0);
                startedAt = reader.GetDouble(1);
                durationMs = reader.GetDouble(2);
                statusCode = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                bytes = reader.GetInt64(4);
                error = reader.IsDBNull(5) ? null : reader.GetString(5);
            }

            var calls = new List<RecordedCall>();
            using (var callCmd = conn.CreateCommand())
            {
                callCmd.CommandText = """
                    SELECT name, offset_ms, duration_ms, status_code, bytes, error
                    FROM calls
                    WHERE run_id = $1 AND sequence = $2
                    ORDER BY call_index;
                    """;
                callCmd.Parameters.Add(new DuckDBParameter(runId));
                callCmd.Parameters.Add(new DuckDBParameter(sequence));
                using var cr = callCmd.ExecuteReader();
                while (cr.Read())
                {
                    calls.Add(new RecordedCall(
                        cr.GetString(0),
                        cr.GetDouble(1),
                        cr.GetDouble(2),
                        cr.IsDBNull(3) ? null : cr.GetInt32(3),
                        cr.GetInt64(4),
                        cr.IsDBNull(5) ? null : cr.GetString(5)));
                }
            }

            var logs = new List<RecordedLog>();
            using (var logCmd = conn.CreateCommand())
            {
                logCmd.CommandText = """
                    SELECT at_seconds, level, message, exception, call_index
                    FROM logs
                    WHERE run_id = $1 AND sequence = $2
                    ORDER BY id;
                    """;
                logCmd.Parameters.Add(new DuckDBParameter(runId));
                logCmd.Parameters.Add(new DuckDBParameter(sequence));
                using var lr = logCmd.ExecuteReader();
                while (lr.Read())
                {
                    logs.Add(new RecordedLog(
                        lr.GetDouble(0),
                        lr.GetString(1),
                        lr.GetString(2),
                        lr.IsDBNull(3) ? null : lr.GetString(3),
                        lr.IsDBNull(4) ? null : lr.GetInt32(4)));
                }
            }

            return Task.FromResult<StoredActorDetail?>(new StoredActorDetail(
                runId, sequence, entry, startedAt, durationMs, statusCode, bytes, error, calls, logs));
        }
    }

    public Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(0, query.Offset);

        lock (dbLock)
        {
            var where = new StringBuilder("WHERE run_id = $1");
            var args = new List<object> { runId };
            int NextIdx() => args.Count + 1;

            if (query.Entry is { } e) { where.Append($" AND entry = ${NextIdx()}"); args.Add(e); }
            if (query.Actor is { } a) { where.Append($" AND sequence = ${NextIdx()}"); args.Add(a); }
            if (!string.IsNullOrWhiteSpace(query.ActorName)) { where.Append($" AND actor_name = ${NextIdx()}"); args.Add(query.ActorName.Trim()); }
            if (query.Outcome is { } o) { where.Append($" AND actor_outcome = ${NextIdx()}"); args.Add(o); }
            if (query.Failed is { } fl) { where.Append($" AND is_failed = ${NextIdx()}"); args.Add(fl); }
            if (query.Slow is { } sl) { where.Append($" AND is_slow = ${NextIdx()}"); args.Add(sl); }
            if (query.Levels is { Count: > 0 } levels && levels.Count < 5)
            {
                var ph = new List<string>(levels.Count);
                foreach (var lv in levels)
                {
                    ph.Add($"${NextIdx()}");
                    args.Add(lv);
                }
                where.Append($" AND level IN ({string.Join(",", ph)})");
            }
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var p = $"${NextIdx()}";
                where.Append($" AND (message ILIKE {p} OR COALESCE(exception, '') ILIKE {p} OR actor_name ILIKE {p})");
                args.Add($"%{query.Search.Trim()}%");
            }

            long total;
            using (var countCmd = conn.CreateCommand())
            {
                countCmd.CommandText = $"SELECT COUNT(*) FROM logs {where};";
                foreach (var v in args) countCmd.Parameters.Add(new DuckDBParameter(v));
                total = Convert.ToInt64(countCmd.ExecuteScalar());
            }

            var items = new List<StoredLogRow>(limit);
            if (total > 0 && offset < total)
            {
                using var pageCmd = conn.CreateCommand();
                var limitIdx = args.Count + 1;
                var offsetIdx = args.Count + 2;
                pageCmd.CommandText = $"""
                    SELECT id, sequence, actor_name, entry, at_seconds, level, message, exception, call_index, actor_outcome, is_failed, is_slow
                    FROM logs {where}
                    ORDER BY id
                    LIMIT ${limitIdx} OFFSET ${offsetIdx};
                    """;
                foreach (var v in args) pageCmd.Parameters.Add(new DuckDBParameter(v));
                pageCmd.Parameters.Add(new DuckDBParameter(limit));
                pageCmd.Parameters.Add(new DuckDBParameter(offset));

                using var reader = pageCmd.ExecuteReader();
                while (reader.Read())
                {
                    items.Add(new StoredLogRow(
                        reader.GetInt64(0),
                        reader.GetInt32(1),
                        reader.GetString(2),
                        reader.GetInt32(3),
                        reader.GetDouble(4),
                        reader.GetString(5),
                        reader.GetString(6),
                        reader.IsDBNull(7) ? null : reader.GetString(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8),
                        reader.GetInt32(9),
                        reader.GetBoolean(10),
                        reader.GetBoolean(11)));
                }
            }

            return Task.FromResult(new LogPageResult(total, offset, limit, items));
        }
    }

    public async Task<RunExportResult?> ExportRunAsync(string runId, string format, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        var fmt = string.IsNullOrWhiteSpace(format) ? "parquet" : format.Trim().ToLowerInvariant();
        var safeRunId = new string(runId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

        lock (dbLock)
        {
            // Confirm run exists
            string? startedUtc, finishedUtc, state, planJson, error;
            double elapsedSeconds;
            using (var rCmd = conn.CreateCommand())
            {
                rCmd.CommandText = "SELECT started_utc, finished_utc, state, elapsed_seconds, plan_json, error FROM runs WHERE run_id = $1;";
                rCmd.Parameters.Add(new DuckDBParameter(runId));
                using var rr = rCmd.ExecuteReader();
                if (!rr.Read()) return null;
                startedUtc = rr.GetString(0);
                finishedUtc = rr.IsDBNull(1) ? null : rr.GetString(1);
                state = rr.GetString(2);
                elapsedSeconds = rr.GetDouble(3);
                planJson = rr.GetString(4);
                error = rr.IsDBNull(5) ? null : rr.GetString(5);
            }

            var escapedId = runId.Replace("'", "''");
            if (fmt == "parquet")
            {
                // Parquet is single-table, so bundle all 4 tables (runs, actors, calls, logs) as ZSTD Parquet files in one .zip.
                var tmpDir = Path.Combine(Path.GetTempPath(), $"perftool-pq-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmpDir);
                try
                {
                    var tables = new (string File, string Sql)[]
                    {
                        ("runs.parquet", $"SELECT * FROM runs WHERE run_id = '{escapedId}'"),
                        ("actors.parquet", $"SELECT * FROM actors WHERE run_id = '{escapedId}' ORDER BY sequence"),
                        ("calls.parquet", $"SELECT * FROM calls WHERE run_id = '{escapedId}' ORDER BY sequence, call_index"),
                        ("logs.parquet", $"SELECT * FROM logs WHERE run_id = '{escapedId}' ORDER BY id"),
                    };

                    using var ms = new MemoryStream();
                    using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var manifest = new
                        {
                            format = "performancetool-run-bundle",
                            schemaVersion = CurrentSchemaVersion,
                            exportedUtc = DateTimeOffset.UtcNow,
                            runId,
                            startedUtc,
                            finishedUtc,
                            state,
                            elapsedSeconds,
                            compression = "zstd",
                            tables = new[]
                            {
                                new { file = "runs.parquet", key = "run_id", description = "Run metadata, duration, state and JSON load plan." },
                                new { file = "actors.parquet", key = "(run_id, sequence)", description = "One row per actor execution (bar in the chart), with outcome, is_failed, is_slow and duration_ms." },
                                new { file = "calls.parquet", key = "(run_id, sequence, call_index)", description = "Individual HTTP requests or measured steps inside each actor; join to actors on (run_id, sequence)." },
                                new { file = "logs.parquet", key = "(run_id, id)", description = "Per-actor ILogger statements; join to actors on (run_id, sequence) and optionally calls on call_index." },
                            },
                        };
                        var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                        using (var mw = manifestEntry.Open())
                            JsonSerializer.Serialize(mw, manifest, JsonOpts);

                        foreach (var (file, sql) in tables)
                        {
                            var filePath = Path.Combine(tmpDir, file);
                            var escapedPath = filePath.Replace("'", "''");
                            using (var cmd = conn.CreateCommand())
                            {
                                cmd.CommandText = $"COPY ({sql}) TO '{escapedPath}' (FORMAT PARQUET, COMPRESSION ZSTD);";
                                cmd.ExecuteNonQuery();
                            }
                            // Parquet files are already ZSTD-compressed, so store without re-compressing.
                            zip.CreateEntryFromFile(filePath, file, CompressionLevel.NoCompression);
                        }
                    }
                    return new RunExportResult($"{safeRunId}-parquet.zip", "application/zip", ms.ToArray());
                }
                finally
                {
                    if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
                }
            }

            if (fmt == "csv")
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"perftool-exp-{Guid.NewGuid():N}.csv");
                try
                {
                    var escapedPath = tmp.Replace("'", "''");
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"COPY (SELECT * FROM logs WHERE run_id = '{escapedId}' ORDER BY id) TO '{escapedPath}' (FORMAT CSV, HEADER);";
                    cmd.ExecuteNonQuery();
                    var bytes = File.ReadAllBytes(tmp);
                    return new RunExportResult($"{safeRunId}-logs.csv", "text/csv; charset=utf-8", bytes);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                }
            }

            if (fmt == "duckdb")
            {
                var tmp = Path.Combine(Path.GetTempPath(), $"perftool-exp-{Guid.NewGuid():N}.duckdb");
                var alias = $"exp_{Guid.NewGuid():N}";
                try
                {
                    var escapedPath = tmp.Replace("'", "''");
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = $"""
                            ATTACH '{escapedPath}' AS {alias};
                            CREATE TABLE {alias}.schema_meta AS SELECT * FROM schema_meta;
                            CREATE TABLE {alias}.runs AS SELECT * FROM runs WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.actors AS SELECT * FROM actors WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.calls AS SELECT * FROM calls WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.logs AS SELECT * FROM logs WHERE run_id = '{escapedId}';
                            DETACH {alias};
                            """;
                        cmd.ExecuteNonQuery();
                    }
                    var bytes = File.ReadAllBytes(tmp);
                    return new RunExportResult($"{safeRunId}.duckdb", "application/octet-stream", bytes);
                }
                finally
                {
                    if (File.Exists(tmp)) File.Delete(tmp);
                    if (File.Exists(tmp + ".wal")) File.Delete(tmp + ".wal");
                }
            }

            // Default: JSON bundle with run + actors + calls + logs
            var actors = new List<object>();
            using (var aCmd = conn.CreateCommand())
            {
                aCmd.CommandText = "SELECT sequence, actor_name, entry, started_at, duration_ms, status_code, bytes, error, outcome, is_failed, is_slow, slow_calls FROM actors WHERE run_id = $1 ORDER BY sequence;";
                aCmd.Parameters.Add(new DuckDBParameter(runId));
                using var ar = aCmd.ExecuteReader();
                while (ar.Read())
                {
                    actors.Add(new
                    {
                        sequence = ar.GetInt32(0),
                        actorName = ar.GetString(1),
                        entry = ar.GetInt32(2),
                        startedAt = ar.GetDouble(3),
                        durationMs = ar.GetDouble(4),
                        statusCode = ar.IsDBNull(5) ? (int?)null : ar.GetInt32(5),
                        bytes = ar.GetInt64(6),
                        error = ar.IsDBNull(7) ? null : ar.GetString(7),
                        outcome = ar.GetInt32(8),
                        isFailed = ar.GetBoolean(9),
                        isSlow = ar.GetBoolean(10),
                        slowCalls = ar.GetInt32(11),
                    });
                }
            }

            var calls = new List<object>();
            using (var cCmd = conn.CreateCommand())
            {
                cCmd.CommandText = "SELECT sequence, call_index, name, offset_ms, duration_ms, status_code, bytes, error FROM calls WHERE run_id = $1 ORDER BY sequence, call_index;";
                cCmd.Parameters.Add(new DuckDBParameter(runId));
                using var cr = cCmd.ExecuteReader();
                while (cr.Read())
                {
                    calls.Add(new
                    {
                        sequence = cr.GetInt32(0),
                        callIndex = cr.GetInt32(1),
                        name = cr.GetString(2),
                        offsetMs = cr.GetDouble(3),
                        durationMs = cr.GetDouble(4),
                        statusCode = cr.IsDBNull(5) ? (int?)null : cr.GetInt32(5),
                        bytes = cr.GetInt64(6),
                        error = cr.IsDBNull(7) ? null : cr.GetString(7),
                    });
                }
            }

            var logs = new List<object>();
            using (var lCmd = conn.CreateCommand())
            {
                lCmd.CommandText = "SELECT id, sequence, actor_name, entry, at_seconds, level, message, exception, call_index, is_failed, is_slow FROM logs WHERE run_id = $1 ORDER BY id;";
                lCmd.Parameters.Add(new DuckDBParameter(runId));
                using var lr = lCmd.ExecuteReader();
                while (lr.Read())
                {
                    logs.Add(new
                    {
                        id = lr.GetInt64(0),
                        sequence = lr.GetInt32(1),
                        actorName = lr.GetString(2),
                        entry = lr.GetInt32(3),
                        atSeconds = lr.GetDouble(4),
                        level = lr.GetString(5),
                        message = lr.GetString(6),
                        exception = lr.IsDBNull(7) ? null : lr.GetString(7),
                        callIndex = lr.IsDBNull(8) ? (int?)null : lr.GetInt32(8),
                        isFailed = lr.GetBoolean(9),
                        isSlow = lr.GetBoolean(10),
                    });
                }
            }

            var payload = new
            {
                schemaVersion = CurrentSchemaVersion,
                runId,
                startedUtc,
                finishedUtc,
                state,
                elapsedSeconds,
                error,
                plan = JsonDocument.Parse(planJson).RootElement,
                actors,
                calls,
                logs,
            };
            return new RunExportResult($"{safeRunId}.json", "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await shutdown.CancelAsync();
        shutdown.Dispose();
        lock (dbLock) conn.Dispose();
    }

    private static int LevelRank(string level) => level switch
    {
        "Trace" => 0,
        "Debug" => 1,
        "Information" => 2,
        "Warning" => 3,
        "Error" => 4,
        _ => 2,
    };
}
