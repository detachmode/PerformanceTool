using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DuckDB.NET.Data;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Services.Storage;

/// <summary>
/// High-throughput DuckDB columnar run storage (brain 0046): drains a <see cref="Channel{T}"/> via a dedicated
/// background writer using DuckDB's native <see cref="DuckDBAppender"/> for bulk ingestion (tens to hundreds of
/// thousands of log rows/second), while serving fast indexed/columnar filtered queries and native
/// Parquet / DuckDB / CSV / JSON run exports.
/// </summary>
public sealed class DuckDbRunStorage : IRunStorage
{
    /// <summary>
    /// Current schema version recorded in the <c>schema_meta</c> table of the DuckDB database.
    /// </summary>
    public const int CurrentSchemaVersion = 7;

    private const int MaxBatchSize = 2000;
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    private readonly DuckDBConnection conn;
    private readonly Lock dbLock = new();
    private readonly Channel<WriteOp> queue;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task writerTask;
    private long nextCallId;
    private long nextLogId;

    /// <summary>
    /// Gets the storage provider name (<c>"DuckDB"</c>).
    /// </summary>
    public string ProviderName => "DuckDB";

    /// <summary>
    /// Gets the file path (or DuckDB data source string) of the underlying DuckDB database.
    /// </summary>
    public string DatabasePath { get; }

    private abstract record WriteOp;
    private sealed record StartRunOp(string RunId, DateTimeOffset StartedUtc, string PlanJson, string? StepsJson) : WriteOp;
    private sealed record BeeOp(string RunId, ExecutionResult Result, int BeeOutcome, int SlowCalls) : WriteOp;
    private sealed record HostLogOp(string RunId, double AtSeconds, string Level, string Message, string? Exception) : WriteOp;
    private sealed record FinishRunOp(string RunId, DateTimeOffset FinishedUtc, string State, double ElapsedSeconds, string? Error) : WriteOp;
    private sealed record FlushOp(TaskCompletionSource Done) : WriteOp;

    /// <summary>
    /// Opens or creates a DuckDB database at <paramref name="dbPath"/>, initializes the schema,
    /// and starts the background ingestion writer loop.
    /// </summary>
    /// <param name="dbPath">Path to the DuckDB database file.</param>
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

                CREATE TABLE IF NOT EXISTS run_runtime (
                    run_id VARCHAR PRIMARY KEY,
                    json VARCHAR NOT NULL
                );

                CREATE TABLE IF NOT EXISTS run_steps (
                    run_id VARCHAR PRIMARY KEY,
                    json VARCHAR NOT NULL
                );

                CREATE TABLE IF NOT EXISTS run_programs (
                    run_id VARCHAR NOT NULL,
                    kind VARCHAR NOT NULL,
                    json VARCHAR NOT NULL,
                    PRIMARY KEY (run_id, kind)
                );

                CREATE TABLE IF NOT EXISTS bees (
                    run_id VARCHAR NOT NULL,
                    sequence INTEGER NOT NULL,
                    bee_name VARCHAR NOT NULL,
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
                    log_count INTEGER NOT NULL,
                    failed_step INTEGER NOT NULL DEFAULT -1,
                    failed_rep INTEGER NOT NULL DEFAULT -1,
                    step_visits_json VARCHAR,
                    trace_id VARCHAR
                );
                ALTER TABLE bees ADD COLUMN IF NOT EXISTS failed_step INTEGER DEFAULT -1;
                ALTER TABLE bees ADD COLUMN IF NOT EXISTS failed_rep INTEGER DEFAULT -1;
                ALTER TABLE bees ADD COLUMN IF NOT EXISTS step_visits_json VARCHAR;
                ALTER TABLE bees ADD COLUMN IF NOT EXISTS trace_id VARCHAR;
                CREATE INDEX IF NOT EXISTS idx_bees_run_seq ON bees(run_id, sequence);
                CREATE INDEX IF NOT EXISTS idx_bees_run_name ON bees(run_id, bee_name);
                CREATE INDEX IF NOT EXISTS idx_bees_run_failed ON bees(run_id, is_failed);
                CREATE INDEX IF NOT EXISTS idx_bees_run_slow ON bees(run_id, is_slow);
                CREATE INDEX IF NOT EXISTS idx_bees_run_failed_step ON bees(run_id, entry, failed_step);

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
                    error VARCHAR,
                    step INTEGER NOT NULL DEFAULT 0,
                    rep INTEGER NOT NULL DEFAULT 0,
                    endpoint VARCHAR,
                    method VARCHAR,
                    host VARCHAR,
                    url VARCHAR,
                    slow_threshold_ms DOUBLE,
                    ttfb_ms DOUBLE,
                    response_read_ms DOUBLE,
                    headers_json VARCHAR,
                    trace_id VARCHAR,
                    span_id VARCHAR
                );
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS step INTEGER DEFAULT 0;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS rep INTEGER DEFAULT 0;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS endpoint VARCHAR;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS method VARCHAR;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS host VARCHAR;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS url VARCHAR;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS slow_threshold_ms DOUBLE;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS ttfb_ms DOUBLE;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS response_read_ms DOUBLE;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS headers_json VARCHAR;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS trace_id VARCHAR;
                ALTER TABLE calls ADD COLUMN IF NOT EXISTS span_id VARCHAR;
                CREATE INDEX IF NOT EXISTS idx_calls_run_seq ON calls(run_id, sequence);
                CREATE INDEX IF NOT EXISTS idx_calls_run_endpoint ON calls(run_id, endpoint);

                CREATE TABLE IF NOT EXISTS logs (
                    id BIGINT NOT NULL,
                    run_id VARCHAR NOT NULL,
                    sequence INTEGER NOT NULL,
                    bee_name VARCHAR NOT NULL,
                    entry INTEGER NOT NULL,
                    at_seconds DOUBLE NOT NULL,
                    level_rank INTEGER NOT NULL,
                    level VARCHAR NOT NULL,
                    message VARCHAR NOT NULL,
                    exception VARCHAR,
                    call_index INTEGER,
                    bee_outcome INTEGER NOT NULL,
                    is_failed BOOLEAN NOT NULL,
                    is_slow BOOLEAN NOT NULL,
                    step INTEGER NOT NULL DEFAULT 0,
                    rep INTEGER NOT NULL DEFAULT 0
                );
                ALTER TABLE logs ADD COLUMN IF NOT EXISTS step INTEGER DEFAULT 0;
                ALTER TABLE logs ADD COLUMN IF NOT EXISTS rep INTEGER DEFAULT 0;
                CREATE INDEX IF NOT EXISTS idx_logs_run_id ON logs(run_id, id);
                CREATE INDEX IF NOT EXISTS idx_logs_run_seq ON logs(run_id, sequence);
                CREATE INDEX IF NOT EXISTS idx_logs_run_bee_name ON logs(run_id, bee_name);
                CREATE INDEX IF NOT EXISTS idx_logs_run_failed ON logs(run_id, is_failed);
                CREATE INDEX IF NOT EXISTS idx_logs_run_slow ON logs(run_id, is_slow);

                CREATE TABLE IF NOT EXISTS trace_fetches (
                    run_id VARCHAR NOT NULL,
                    trace_id VARCHAR NOT NULL,
                    sequence INTEGER NOT NULL,
                    status VARCHAR NOT NULL,
                    reason VARCHAR,
                    span_count INTEGER NOT NULL,
                    attempts INTEGER NOT NULL,
                    selected_for VARCHAR,
                    fetched_utc VARCHAR,
                    truncated BOOLEAN DEFAULT FALSE
                );
                CREATE INDEX IF NOT EXISTS idx_trace_fetches_run ON trace_fetches(run_id, trace_id);

                CREATE TABLE IF NOT EXISTS trace_spans (
                    run_id VARCHAR NOT NULL,
                    trace_id VARCHAR NOT NULL,
                    span_id VARCHAR NOT NULL,
                    parent_span_id VARCHAR,
                    service VARCHAR NOT NULL,
                    name VARCHAR NOT NULL,
                    kind VARCHAR,
                    start_unix_nano BIGINT NOT NULL,
                    duration_ms DOUBLE NOT NULL,
                    is_error BOOLEAN DEFAULT FALSE,
                    status_message VARCHAR,
                    attributes_json VARCHAR
                );
                CREATE INDEX IF NOT EXISTS idx_trace_spans_run_trace ON trace_spans(run_id, trace_id);

                CREATE TABLE IF NOT EXISTS source_blobs (
                    sha256 VARCHAR PRIMARY KEY,
                    code VARCHAR NOT NULL
                );

                CREATE TABLE IF NOT EXISTS run_sources (
                    run_id VARCHAR NOT NULL,
                    kind VARCHAR NOT NULL,
                    target_id VARCHAR NOT NULL,
                    name VARCHAR NOT NULL,
                    file_name VARCHAR NOT NULL,
                    sha256 VARCHAR NOT NULL,
                    origin VARCHAR NOT NULL,
                    is_compiled BOOLEAN NOT NULL,
                    PRIMARY KEY (run_id, kind, target_id)
                );
                CREATE INDEX IF NOT EXISTS idx_run_sources_run ON run_sources(run_id);
                """;
            cmd.ExecuteNonQuery();

            using var maxCmd = conn.CreateCommand();
            maxCmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM calls; SELECT COALESCE(MAX(id), 0) FROM logs;";
            using var r = maxCmd.ExecuteReader();
            if (r.Read()) nextCallId = r.GetInt64(0);
            if (r.NextResult() && r.Read()) nextLogId = r.GetInt64(0);
        }
    }

    /// <summary>
    /// Enqueues a run-started record containing the run identifier, UTC start timestamp, load plan, and optional scenario step definitions.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="startedUtc">UTC timestamp when the run started.</param>
    /// <param name="plan">The load plan executed by the run.</param>
    /// <param name="steps">Optional per-entry step definitions for step-machine scenarios.</param>
    public void EnqueueRunStarted(string runId, DateTimeOffset startedUtc, LoadPlan plan, IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null) =>
        queue.Writer.TryWrite(new StartRunOp(
            runId,
            startedUtc,
            JsonSerializer.Serialize(plan, JsonOpts),
            steps is not null && steps.Any(s => s.Count > 0) ? JsonSerializer.Serialize(steps, JsonOpts) : null));

    /// <summary>
    /// Enqueues a completed bee execution along with its calls and logs for background columnar ingestion.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="result">Execution result including recorded calls, logs, and step visits.</param>
    /// <param name="beeOutcome">Numeric outcome code for the bee (0 = ok, 1 = slow, 2 = failed).</param>
    /// <param name="slowCalls">Number of slow calls recorded during the bee execution.</param>
    public void EnqueueBeeCompleted(string runId, ExecutionResult result, int beeOutcome, int slowCalls) =>
        queue.Writer.TryWrite(new BeeOp(runId, result, beeOutcome, slowCalls));

    /// <summary>
    /// Enqueues a host-level log line associated with the specified run.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="atSeconds">Elapsed run time in seconds when the log entry was emitted.</param>
    /// <param name="level">Log severity level (e.g., <c>Information</c>, <c>Warning</c>, <c>Error</c>).</param>
    /// <param name="message">Log message text.</param>
    /// <param name="exception">Optional formatted exception details.</param>
    public void EnqueueHostLog(string runId, double atSeconds, string level, string message, string? exception = null) =>
        queue.Writer.TryWrite(new HostLogOp(runId, atSeconds, level, message, exception));

    /// <summary>
    /// Enqueues a run-finished update recording the final run state, elapsed duration, and any terminal error.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="finishedUtc">UTC timestamp when the run finished.</param>
    /// <param name="state">Final run state string (e.g., <c>Finished</c>, <c>Stopped</c>, <c>Failed</c>).</param>
    /// <param name="elapsedSeconds">Total elapsed duration of the run in seconds.</param>
    /// <param name="error">Optional terminal error message if the run failed.</param>
    public void EnqueueRunFinished(string runId, DateTimeOffset finishedUtc, string state, double elapsedSeconds, string? error) =>
        queue.Writer.TryWrite(new FinishRunOp(runId, finishedUtc, state, elapsedSeconds, error));

    /// <summary>
    /// Stores the final runtime data of a run (brain 0052) as JSON: the values as they were at the end, no history. Written once, after the shutdown program.
    /// A second call for the same run replaces the first.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="json">Serialized runtime data snapshot JSON.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>A completed task once the runtime data has been persisted.</returns>
    public Task SaveRuntimeDataAsync(string runId, string json, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM run_runtime WHERE run_id = $1; INSERT INTO run_runtime(run_id, json) VALUES ($1, $2);";
            cmd.Parameters.Add(new DuckDBParameter(runId));
            cmd.Parameters.Add(new DuckDBParameter(json));
            cmd.ExecuteNonQuery();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the final runtime data of a run as JSON, or <see langword="null"/> when the run kept none.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>A task containing the runtime data JSON string, or <see langword="null"/> if none was saved.</returns>
    public Task<string?> GetRuntimeDataAsync(string runId, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT json FROM run_runtime WHERE run_id = $1;";
            cmd.Parameters.Add(new DuckDBParameter(runId));
            return Task.FromResult(cmd.ExecuteScalar() as string);
        }
    }

    /// <inheritdoc />
    public Task SaveRunSourcesAsync(string runId, IReadOnlyList<RunSourceSnapshotDto> sources, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM run_sources WHERE run_id = $1;";
                del.Parameters.Add(new DuckDBParameter(runId));
                del.ExecuteNonQuery();
            }

            foreach (var s in sources)
            {
                using (var blob = conn.CreateCommand())
                {
                    blob.CommandText = "INSERT INTO source_blobs(sha256, code) VALUES ($1, $2) ON CONFLICT (sha256) DO NOTHING;";
                    blob.Parameters.Add(new DuckDBParameter(s.Sha256));
                    blob.Parameters.Add(new DuckDBParameter(s.Code));
                    blob.ExecuteNonQuery();
                }

                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = """
                        INSERT INTO run_sources(run_id, kind, target_id, name, file_name, sha256, origin, is_compiled)
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8);
                        """;
                    ins.Parameters.Add(new DuckDBParameter(runId));
                    ins.Parameters.Add(new DuckDBParameter(s.Kind));
                    ins.Parameters.Add(new DuckDBParameter(s.TargetId));
                    ins.Parameters.Add(new DuckDBParameter(s.Name));
                    ins.Parameters.Add(new DuckDBParameter(s.FileName));
                    ins.Parameters.Add(new DuckDBParameter(s.Sha256));
                    ins.Parameters.Add(new DuckDBParameter(s.Origin));
                    ins.Parameters.Add(new DuckDBParameter(s.IsCompiled));
                    ins.ExecuteNonQuery();
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<RunSourceSnapshotDto>> GetRunSourcesAsync(string runId, CancellationToken ct = default)
    {
        var list = new List<RunSourceSnapshotDto>();
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT rs.kind, rs.target_id, rs.name, rs.file_name, sb.code, rs.sha256, rs.origin, rs.is_compiled
                FROM run_sources rs
                JOIN source_blobs sb ON sb.sha256 = rs.sha256
                WHERE rs.run_id = $1
                ORDER BY rs.kind, rs.name;
                """;
            cmd.Parameters.Add(new DuckDBParameter(runId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new RunSourceSnapshotDto(
                    r.GetString(0),
                    r.GetString(1),
                    r.GetString(2),
                    r.GetString(3),
                    r.GetString(4),
                    r.GetString(5),
                    r.GetString(6),
                    r.GetBoolean(7)));
            }
        }
        return Task.FromResult<IReadOnlyList<RunSourceSnapshotDto>>(list);
    }

    /// <summary>
    /// Stores a run-level program's result and log lines (<paramref name="kind"/> is <c>"preflight"</c> or <c>"shutdown"</c>)
    /// and writes its log lines into the run's log stream so they are queryable on the Logs page.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="kind">Program kind (<c>"preflight"</c> or <c>"shutdown"</c>).</param>
    /// <param name="program">Program execution summary and captured log lines.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>A completed task once the program run and its logs have been persisted.</returns>
    public Task SaveProgramRunAsync(string runId, string kind, StoredProgramRunDto program, CancellationToken ct = default)
    {
        var (normalized, entry, beeName) = ProgramLogHelper.ResolveKind(kind);
        var json = JsonSerializer.Serialize(program, JsonOpts);
        var isFailed = string.Equals(program.State, "Failed", StringComparison.OrdinalIgnoreCase);
        var outcome = isFailed ? 2 : 0;

        lock (dbLock)
        {
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    DELETE FROM run_programs WHERE run_id = $1 AND kind = $2;
                    INSERT INTO run_programs(run_id, kind, json) VALUES ($1, $2, $3);
                    """;
                cmd.Parameters.Add(new DuckDBParameter(runId));
                cmd.Parameters.Add(new DuckDBParameter(normalized));
                cmd.Parameters.Add(new DuckDBParameter(json));
                cmd.ExecuteNonQuery();
            }

            using (var del = conn.CreateCommand())
            {
                del.CommandText = "DELETE FROM logs WHERE run_id = $1 AND entry = $2;";
                del.Parameters.Add(new DuckDBParameter(runId));
                del.Parameters.Add(new DuckDBParameter(entry));
                del.ExecuteNonQuery();
            }

            if (program.Logs.Count > 0)
            {
                var atSeconds = 0.0;
                if (normalized == "shutdown")
                {
                    using var eCmd = conn.CreateCommand();
                    eCmd.CommandText = "SELECT elapsed_seconds FROM runs WHERE run_id = $1;";
                    eCmd.Parameters.Add(new DuckDBParameter(runId));
                    if (eCmd.ExecuteScalar() is double elapsed) atSeconds = elapsed;
                }

                using var logApp = conn.CreateAppender("logs");
                foreach (var raw in program.Logs)
                {
                    var (level, message) = ProgramLogHelper.ParseLine(raw);
                    var row = logApp.CreateRow();
                    row.AppendValue(Interlocked.Increment(ref nextLogId))
                       .AppendValue(runId)
                       .AppendValue(entry)
                       .AppendValue(beeName)
                       .AppendValue(entry)
                       .AppendValue(atSeconds)
                       .AppendValue(LevelRank(level))
                       .AppendValue(level)
                       .AppendValue(message)
                       .AppendValue((string?)null)
                       .AppendValue((int?)null)
                       .AppendValue(outcome)
                       .AppendValue(isFailed)
                       .AppendValue(false)
                       .AppendValue(0)
                       .AppendValue(0)
                       .EndRow();
                }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits until all currently queued writes are committed to DuckDB.
    /// </summary>
    /// <param name="ct">Cancellation token used while awaiting the flush barrier.</param>
    /// <returns>A task that completes when all preceding queued writes have been flushed.</returns>
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
        var beesToAppend = new List<BeeOp>();
        var hostLogsToAppend = new List<HostLogOp>();

        foreach (var op in batch)
        {
            switch (op)
            {
                case StartRunOp s:
                    FlushBees(beesToAppend);
                    FlushHostLogs(hostLogsToAppend);
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
                    if (s.StepsJson is not null)
                    {
                        using var scmd = conn.CreateCommand();
                        scmd.CommandText = "DELETE FROM run_steps WHERE run_id = $1; INSERT INTO run_steps(run_id, json) VALUES ($1, $2);";
                        scmd.Parameters.Add(new DuckDBParameter(s.RunId));
                        scmd.Parameters.Add(new DuckDBParameter(s.StepsJson));
                        scmd.ExecuteNonQuery();
                    }
                    break;

                case BeeOp a:
                    beesToAppend.Add(a);
                    break;

                case HostLogOp h:
                    hostLogsToAppend.Add(h);
                    break;

                case FinishRunOp f:
                    FlushBees(beesToAppend);
                    FlushHostLogs(hostLogsToAppend);
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

        FlushBees(beesToAppend);
        FlushHostLogs(hostLogsToAppend);
    }

    private void FlushHostLogs(List<HostLogOp> hostLogs)
    {
        if (hostLogs.Count == 0) return;
        int hostEntry = ProgramLogEntry.Host;
        using (var logApp = conn.CreateAppender("logs"))
        {
            foreach (var h in hostLogs)
            {
                var isFailed = string.Equals(h.Level, "Error", StringComparison.OrdinalIgnoreCase);
                var isSlow = string.Equals(h.Level, "Warning", StringComparison.OrdinalIgnoreCase);
                var outcome = isFailed ? 2 : isSlow ? 1 : 0;
                logApp.CreateRow()
                    .AppendValue(Interlocked.Increment(ref nextLogId))
                    .AppendValue(h.RunId)
                    .AppendValue(hostEntry)
                    .AppendValue("Host")
                    .AppendValue(hostEntry)
                    .AppendValue(h.AtSeconds)
                    .AppendValue(LevelRank(h.Level))
                    .AppendValue(h.Level)
                    .AppendValue(h.Message)
                    .AppendValue(h.Exception)
                    .AppendValue((int?)null)
                    .AppendValue(outcome)
                    .AppendValue(isFailed)
                    .AppendValue(isSlow)
                    .AppendValue(0)
                    .AppendValue(0)
                    .EndRow();
            }
        }
        hostLogs.Clear();
    }

    private void FlushBees(List<BeeOp> beesToAppend)
    {
        if (beesToAppend.Count == 0) return;

        using (var beeApp = conn.CreateAppender("bees"))
        {
            foreach (var a in beesToAppend)
            {
                var r = a.Result;
                var row = beeApp.CreateRow();
                row.AppendValue(a.RunId)
                   .AppendValue(r.Sequence)
                   .AppendValue($"B{r.Sequence + 1}")
                   .AppendValue(r.Entry)
                   .AppendValue(r.StartedAtSeconds)
                   .AppendValue(r.ElapsedMs)
                   .AppendValue(r.StatusCode)
                   .AppendValue(r.Bytes)
                   .AppendValue(r.Error)
                   .AppendValue(a.BeeOutcome)
                   .AppendValue(a.BeeOutcome == 2)
                   .AppendValue(a.SlowCalls > 0)
                   .AppendValue(a.SlowCalls)
                   .AppendValue(r.Calls?.Count ?? 0)
                   .AppendValue(r.Logs?.Count ?? 0)
                   .AppendValue(r.FailedStep)
                   .AppendValue(r.FailedRep)
                   .AppendValue(r.StepVisits is { Count: > 0 } ? JsonSerializer.Serialize(r.StepVisits, JsonOpts) : null)
                   .AppendValue(r.TraceId)
                   .EndRow();
            }
        }

        var hasCalls = beesToAppend.Any(a => a.Result.Calls is { Count: > 0 });
        if (hasCalls)
        {
            using var callApp = conn.CreateAppender("calls");
            foreach (var a in beesToAppend)
            {
                if (a.Result.Calls is not { Count: > 0 } calls) continue;
                for (var i = 0; i < calls.Count; i++)
                {
                    var c = calls[i];
                    var endpoint = string.IsNullOrWhiteSpace(c.Endpoint) ? EndpointNormalizer.NormalizeCallName(c.Name) : c.Endpoint;
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
                       .AppendValue(c.Step)
                       .AppendValue(c.Rep)
                       .AppendValue(endpoint)
                       .AppendValue(c.Method)
                       .AppendValue(c.Host)
                       .AppendValue(c.Url)
                       .AppendValue(c.SlowThresholdMs)
                       .AppendValue(c.TtfbMs)
                       .AppendValue(c.ResponseReadMs)
                       .AppendValue(c.Headers is { Count: > 0 } ? JsonSerializer.Serialize(c.Headers, JsonOpts) : null)
                       .AppendValue(c.TraceId)
                       .AppendValue(c.SpanId)
                       .EndRow();
                }
            }
        }

        var hasLogs = beesToAppend.Any(a => a.Result.Logs is { Count: > 0 });
        if (hasLogs)
        {
            using var logApp = conn.CreateAppender("logs");
            foreach (var a in beesToAppend)
            {
                if (a.Result.Logs is not { Count: > 0 } logs) continue;
                var beeName = $"B{a.Result.Sequence + 1}";
                var isFailed = a.BeeOutcome == 2;
                var isSlow = a.SlowCalls > 0;
                for (var i = 0; i < logs.Count; i++)
                {
                    var l = logs[i];
                    var row = logApp.CreateRow();
                    row.AppendValue(Interlocked.Increment(ref nextLogId))
                       .AppendValue(a.RunId)
                       .AppendValue(a.Result.Sequence)
                       .AppendValue(beeName)
                       .AppendValue(a.Result.Entry)
                       .AppendValue(l.AtSeconds)
                       .AppendValue(LevelRank(l.Level))
                       .AppendValue(l.Level)
                       .AppendValue(l.Message)
                       .AppendValue(l.Exception)
                       .AppendValue(l.CallIndex)
                       .AppendValue(a.BeeOutcome)
                       .AppendValue(isFailed)
                       .AppendValue(isSlow)
                       .AppendValue(l.Step)
                       .AppendValue(l.Rep)
                       .EndRow();
                }
            }
        }

        beesToAppend.Clear();
    }

    /// <summary>
    /// Lists stored runs ordered by start timestamp descending.
    /// </summary>
    /// <param name="limit">Maximum number of run summaries to return (clamped between 1 and 500).</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>A list of stored run summaries.</returns>
    public Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT r.run_id, r.started_utc, r.finished_utc, r.state, r.elapsed_seconds, r.error,
                       COALESCE((SELECT COUNT(*) FROM bees a WHERE a.run_id = r.run_id), 0) AS total_bees,
                       COALESCE((SELECT COUNT(*) FROM bees a WHERE a.run_id = r.run_id AND a.is_failed = TRUE), 0) AS failed_bees,
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

    /// <summary>
    /// Loads the full stored run record, including plan, step definitions, bee summaries, and preflight/shutdown program runs.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>The stored run data, or <see langword="null"/> if no run matches <paramref name="runId"/>.</returns>
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

            IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null;
            using (var sCmd = conn.CreateCommand())
            {
                sCmd.CommandText = "SELECT json FROM run_steps WHERE run_id = $1;";
                sCmd.Parameters.Add(new DuckDBParameter(runId));
                if (sCmd.ExecuteScalar() is string stepsJson)
                    steps = JsonSerializer.Deserialize<List<List<StepInfo>>>(stepsJson, JsonOpts);
            }

            var bees = new List<StoredBeeSummary>();
            using (var aCmd = conn.CreateCommand())
            {
                aCmd.CommandText = """
                    SELECT sequence, entry, started_at, duration_ms, status_code, bytes, error, slow_calls,
                           COALESCE(failed_step, -1), COALESCE(failed_rep, -1)
                    FROM bees WHERE run_id = $1 ORDER BY sequence;
                    """;
                aCmd.Parameters.Add(new DuckDBParameter(runId));
                using var ar = aCmd.ExecuteReader();
                while (ar.Read())
                {
                    bees.Add(new StoredBeeSummary(
                        ar.GetInt32(0),
                        ar.GetInt32(1),
                        ar.GetDouble(2),
                        ar.GetDouble(3),
                        ar.IsDBNull(4) ? null : ar.GetInt32(4),
                        ar.GetInt64(5),
                        ar.IsDBNull(6) ? null : ar.GetString(6),
                        ar.GetInt32(7),
                        ar.GetInt32(8),
                        ar.GetInt32(9)));
                }
            }

            StoredProgramRunDto? preflight = null;
            StoredProgramRunDto? shutdownProgram = null;
            using (var pCmd = conn.CreateCommand())
            {
                pCmd.CommandText = "SELECT kind, json FROM run_programs WHERE run_id = $1;";
                pCmd.Parameters.Add(new DuckDBParameter(runId));
                using var pr = pCmd.ExecuteReader();
                while (pr.Read())
                {
                    var pKind = pr.GetString(0);
                    var pJson = pr.GetString(1);
                    if (string.Equals(pKind, "preflight", StringComparison.OrdinalIgnoreCase))
                        preflight = JsonSerializer.Deserialize<StoredProgramRunDto>(pJson, JsonOpts);
                    else if (string.Equals(pKind, "shutdown", StringComparison.OrdinalIgnoreCase))
                        shutdownProgram = JsonSerializer.Deserialize<StoredProgramRunDto>(pJson, JsonOpts);
                }
            }

            return Task.FromResult<StoredRunData?>(new StoredRunData(runId, startedUtc, state, elapsedSeconds, plan, bees, totalLogs, error, steps, preflight, shutdownProgram));
        }
    }

    /// <summary>
    /// Loads detailed execution data for a single bee in a stored run, including its calls, logs, step visits, and entry metadata.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="sequence">Zero-based sequence number of the bee within the run.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>The stored bee detail, or <see langword="null"/> if no matching bee exists.</returns>
    public Task<StoredBeeDetail?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            int entry, failedStep, failedRep;
            double startedAt, durationMs;
            int? statusCode;
            long bytes;
            string? error;
            string? traceId;
            IReadOnlyList<RecordedStepVisit>? stepVisits = null;

            using (var beeCmd = conn.CreateCommand())
            {
                beeCmd.CommandText = """
                    SELECT entry, started_at, duration_ms, status_code, bytes, error,
                           COALESCE(failed_step, -1), COALESCE(failed_rep, -1), step_visits_json, trace_id
                    FROM bees
                    WHERE run_id = $1 AND sequence = $2
                    LIMIT 1;
                    """;
                beeCmd.Parameters.Add(new DuckDBParameter(runId));
                beeCmd.Parameters.Add(new DuckDBParameter(sequence));
                using var reader = beeCmd.ExecuteReader();
                if (!reader.Read()) return Task.FromResult<StoredBeeDetail?>(null);
                entry = reader.GetInt32(0);
                startedAt = reader.GetDouble(1);
                durationMs = reader.GetDouble(2);
                statusCode = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                bytes = reader.GetInt64(4);
                error = reader.IsDBNull(5) ? null : reader.GetString(5);
                failedStep = reader.GetInt32(6);
                failedRep = reader.GetInt32(7);
                if (!reader.IsDBNull(8) && reader.GetString(8) is { Length: > 0 } svJson)
                    stepVisits = JsonSerializer.Deserialize<List<RecordedStepVisit>>(svJson, JsonOpts);
                traceId = reader.IsDBNull(9) ? null : reader.GetString(9);
            }

            var calls = new List<RecordedCall>();
            using (var callCmd = conn.CreateCommand())
            {
                callCmd.CommandText = """
                    SELECT name, offset_ms, duration_ms, status_code, bytes, error,
                           COALESCE(step, 0), COALESCE(rep, 0), endpoint, method, host, url, slow_threshold_ms,
                           ttfb_ms, response_read_ms, headers_json, trace_id, span_id
                    FROM calls
                    WHERE run_id = $1 AND sequence = $2
                    ORDER BY call_index;
                    """;
                callCmd.Parameters.Add(new DuckDBParameter(runId));
                callCmd.Parameters.Add(new DuckDBParameter(sequence));
                using var cr = callCmd.ExecuteReader();
                while (cr.Read())
                {
                    IReadOnlyDictionary<string, string>? headers = null;
                    if (!cr.IsDBNull(15) && cr.GetString(15) is { Length: > 0 } hJson)
                        headers = JsonSerializer.Deserialize<Dictionary<string, string>>(hJson, JsonOpts);
                    calls.Add(new RecordedCall(
                        cr.GetString(0),
                        cr.GetDouble(1),
                        cr.GetDouble(2),
                        cr.IsDBNull(3) ? null : cr.GetInt32(3),
                        cr.GetInt64(4),
                        cr.IsDBNull(5) ? null : cr.GetString(5),
                        cr.GetInt32(6),
                        cr.GetInt32(7),
                        cr.IsDBNull(8) ? null : cr.GetString(8),
                        cr.IsDBNull(9) ? null : cr.GetString(9),
                        cr.IsDBNull(10) ? null : cr.GetString(10),
                        cr.IsDBNull(12) ? null : cr.GetDouble(12),
                        cr.IsDBNull(11) ? null : cr.GetString(11),
                        cr.IsDBNull(13) ? null : cr.GetDouble(13),
                        cr.IsDBNull(14) ? null : cr.GetDouble(14),
                        headers,
                        cr.IsDBNull(16) ? null : cr.GetString(16),
                        cr.IsDBNull(17) ? null : cr.GetString(17)));
                }
            }

            var logs = new List<RecordedLog>();
            using (var logCmd = conn.CreateCommand())
            {
                logCmd.CommandText = """
                    SELECT at_seconds, level, message, exception, call_index, COALESCE(step, 0), COALESCE(rep, 0)
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
                        lr.IsDBNull(4) ? null : lr.GetInt32(4),
                        lr.GetInt32(5),
                        lr.GetInt32(6)));
                }
            }

            string? entryLabel = null;
            using (var pCmd = conn.CreateCommand())
            {
                pCmd.CommandText = "SELECT plan_json FROM runs WHERE run_id = $1 LIMIT 1;";
                pCmd.Parameters.Add(new DuckDBParameter(runId));
                if (pCmd.ExecuteScalar() is string pj && !string.IsNullOrWhiteSpace(pj))
                {
                    var plan = JsonSerializer.Deserialize<LoadPlan>(pj, JsonOpts);
                    if (plan is not null && entry >= 0 && entry < plan.Scenarios.Count)
                    {
                        var cfg = plan.Scenarios[entry];
                        entryLabel = string.IsNullOrWhiteSpace(cfg.Label) ? cfg.ScenarioId : cfg.Label;
                    }
                }
            }

            IReadOnlyList<StepInfo>? entrySteps = null;
            using (var sCmd = conn.CreateCommand())
            {
                sCmd.CommandText = "SELECT json FROM run_steps WHERE run_id = $1 LIMIT 1;";
                sCmd.Parameters.Add(new DuckDBParameter(runId));
                if (sCmd.ExecuteScalar() is string sj && !string.IsNullOrWhiteSpace(sj))
                {
                    var allSteps = JsonSerializer.Deserialize<List<List<StepInfo>>>(sj, JsonOpts);
                    if (allSteps is not null && entry >= 0 && entry < allSteps.Count)
                        entrySteps = allSteps[entry];
                }
            }

            return Task.FromResult<StoredBeeDetail?>(new StoredBeeDetail(
                runId, sequence, entry, startedAt, durationMs, statusCode, bytes, error, calls, logs, failedStep, failedRep, stepVisits, entryLabel, entrySteps, traceId));
        }
    }

    /// <summary>The WHERE clause of a log query; a facet leaves out the filter of its own group (brain 0059).</summary>
    private static (string Where, List<object> Args) BuildLogWhere(string runId, LogQuery query, bool entry, bool step, bool levels)
    {
        var where = new StringBuilder("WHERE run_id = $1");
        var args = new List<object> { runId };
        int NextIdx() => args.Count + 1;

        if (entry && query.Entry is { } e) { where.Append($" AND entry = ${NextIdx()}"); args.Add(e); }
        if (step && query.Step is { } st) { where.Append($" AND COALESCE(step, 0) = ${NextIdx()}"); args.Add(st); }
        if (query.Bee is { } a) { where.Append($" AND sequence = ${NextIdx()}"); args.Add(a); }
        if (!string.IsNullOrWhiteSpace(query.BeeName)) { where.Append($" AND bee_name = ${NextIdx()}"); args.Add(query.BeeName.Trim()); }
        if (query.Outcome is { } o) { where.Append($" AND bee_outcome = ${NextIdx()}"); args.Add(o); }
        if (query.Failed is { } fl) { where.Append($" AND is_failed = ${NextIdx()}"); args.Add(fl); }
        if (query.Slow is { } sl) { where.Append($" AND is_slow = ${NextIdx()}"); args.Add(sl); }
        if (levels && query.Levels is { Count: > 0 } lvls && lvls.Count < 5)
        {
            var ph = new List<string>(lvls.Count);
            foreach (var lv in lvls)
            {
                ph.Add($"${NextIdx()}");
                args.Add(lv);
            }
            where.Append($" AND level IN ({string.Join(",", ph)})");
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var p = $"${NextIdx()}";
            where.Append($" AND (message ILIKE {p} OR COALESCE(exception, '') ILIKE {p} OR bee_name ILIKE {p})");
            args.Add($"%{query.Search.Trim()}%");
        }
        return (where.ToString(), args);
    }

    /// <summary>
    /// Computes the facet counts behind the Logs page filter column (brain 0059) for <paramref name="query"/> (ignoring offset and limit):
    /// total matching rows, counts per log level, counts per scenario entry/program stream, and (when an entry is selected) counts per step.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="query">Filter criteria for the facet calculation.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>Aggregated log facet counts for the specified run and filters.</returns>
    public Task<LogFacets> GetLogFacetsAsync(string runId, LogQuery query, CancellationToken ct = default)
    {
        lock (dbLock)
        {
            long total;
            long preflightCount = 0;
            long shutdownCount = 0;
            long hostCount = 0;
            var levelCounts = new Dictionary<string, long>();
            var entryCounts = new List<long>();
            var stepCounts = new List<LogStepCount>();

            var (allWhere, allArgs) = BuildLogWhere(runId, query, entry: true, step: true, levels: true);
            total = ScalarCount($"SELECT COUNT(*) FROM logs {allWhere};", allArgs);

            var (lvWhere, lvArgs) = BuildLogWhere(runId, query, entry: true, step: true, levels: false);
            Read($"SELECT level, COUNT(*) FROM logs {lvWhere} GROUP BY level;", lvArgs, r => levelCounts[r.GetString(0)] = r.GetInt64(1));

            var (enWhere, enArgs) = BuildLogWhere(runId, query, entry: false, step: false, levels: true);
            Read($"SELECT entry, COUNT(*) FROM logs {enWhere} GROUP BY entry ORDER BY entry;", enArgs, r =>
            {
                var entry = r.GetInt32(0);
                var count = r.GetInt64(1);
                if (entry == ProgramLogEntry.Preflight) preflightCount = count;
                else if (entry == ProgramLogEntry.Shutdown) shutdownCount = count;
                else if (entry == ProgramLogEntry.Host) hostCount = count;
                else if (entry >= 0)
                {
                    while (entryCounts.Count <= entry) entryCounts.Add(0);
                    entryCounts[entry] = count;
                }
            });

            if (query.Entry is >= 0)
            {
                var (stWhere, stArgs) = BuildLogWhere(runId, query, entry: true, step: false, levels: false);
                Read($"SELECT COALESCE(step, 0) AS s, COUNT(*), COUNT(*) FILTER (WHERE level = 'Error') FROM logs {stWhere} GROUP BY s ORDER BY s;", stArgs,
                    r => stepCounts.Add(new LogStepCount(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2))));
            }

            return Task.FromResult(new LogFacets(total, levelCounts, entryCounts, stepCounts, preflightCount, shutdownCount, hostCount));
        }
    }

    private long ScalarCount(string sql, List<object> args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in args) cmd.Parameters.Add(new DuckDBParameter(v));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void Read(string sql, List<object> args, Action<System.Data.Common.DbDataReader> row)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var v in args) cmd.Parameters.Add(new DuckDBParameter(v));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) row(reader);
    }

    /// <summary>
    /// Queries a page of stored log rows for a run matching the filters in <paramref name="query"/>.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="query">Filter, offset, and limit parameters (limit is clamped between 1 and 1000).</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>A paged result containing the total matching count and the requested slice of log rows.</returns>
    public Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(0, query.Offset);

        lock (dbLock)
        {
            var (where, args) = BuildLogWhere(runId, query, entry: true, step: true, levels: true);

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
                    SELECT id, sequence, bee_name, entry, at_seconds, level, message, exception, call_index, bee_outcome, is_failed, is_slow, COALESCE(step, 0)
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
                        reader.GetBoolean(11),
                        reader.GetInt32(12)));
                }
            }

            return Task.FromResult(new LogPageResult(total, offset, limit, items));
        }
    }

    /// <summary>
    /// Aggregates auto-detected endpoint summaries and call counts for a stored run on the Calls analysis page (brain 0060).
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="query">Filtering and grouping options (such as entry, scenario, step, slow/failed flags, and search text).</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>Aggregated endpoint call summaries matching <paramref name="query"/>.</returns>
    public Task<CallEndpointsResult> GetCallEndpointsAsync(string runId, CallEndpointsQuery query, CancellationToken ct = default)
    {
        List<CallFact> facts;
        IReadOnlyList<IReadOnlyList<StepInfo>>? steps = null;
        LoadPlan? plan = null;
        lock (dbLock)
        {
            facts = LoadCallFacts(runId);
            if (query.ByStep)
                steps = LoadRunStepsLocked(runId);
            if (!string.IsNullOrWhiteSpace(query.Scenario))
                plan = LoadRunPlanLocked(runId);
        }
        var scenarioEntries = plan is not null && !string.IsNullOrWhiteSpace(query.Scenario)
            ? CallAnalytics.ResolveScenarioEntries(plan, query.Scenario)
            : null;
        return Task.FromResult(CallAnalytics.BuildEndpoints(
            facts,
            query.Entry,
            query.SlowOnly,
            query.FailedOnly,
            query.MinMs,
            query.Search,
            query.Step,
            query.ByStep,
            steps is null ? null : (e, s) => e >= 0 && e < steps.Count && s >= 0 && s < steps[e].Count ? steps[e][s].Name : null,
            scenarioEntries,
            query.Endpoint));
    }

    /// <summary>
    /// Returns the top-N slowest individual calls in a stored run matching the optional filters in <paramref name="query"/>.
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="query">Limit and filter parameters for selecting the slowest calls.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>The slowest matching call records.</returns>
    public Task<SlowestCallsResult> GetSlowestCallsAsync(string runId, SlowestCallsQuery query, CancellationToken ct = default)
    {
        List<CallFact> facts;
        IReadOnlyList<IReadOnlyList<StepInfo>>? steps;
        LoadPlan? plan;
        lock (dbLock)
        {
            facts = LoadCallFacts(runId);
            steps = LoadRunStepsLocked(runId);
            plan = LoadRunPlanLocked(runId);
        }
        var scenarioEntries = plan is not null && !string.IsNullOrWhiteSpace(query.Scenario)
            ? CallAnalytics.ResolveScenarioEntries(plan, query.Scenario)
            : null;
        return Task.FromResult(CallAnalytics.BuildSlowestCalls(
            facts,
            query.Limit,
            query.Entry,
            scenarioEntries,
            query.Step,
            query.Endpoint,
            query.SlowOnly,
            query.FailedOnly,
            query.MinMs,
            query.Search,
            plan is null ? null : e => e >= 0 && e < plan.Scenarios.Count ? (string.IsNullOrWhiteSpace(plan.Scenarios[e].Label) ? plan.Scenarios[e].ScenarioId : plan.Scenarios[e].Label) : null,
            steps is null ? null : (e, s) => e >= 0 && e < steps.Count && s >= 0 && s < steps[e].Count ? steps[e][s].Name : null));
    }

    private LoadPlan? LoadRunPlanLocked(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT plan_json FROM runs WHERE run_id = $1 LIMIT 1;";
        cmd.Parameters.Add(new DuckDBParameter(runId));
        return cmd.ExecuteScalar() is string pj && !string.IsNullOrWhiteSpace(pj)
            ? JsonSerializer.Deserialize<LoadPlan>(pj, JsonOpts)
            : null;
    }

    private IReadOnlyList<IReadOnlyList<StepInfo>>? LoadRunStepsLocked(string runId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT json FROM run_steps WHERE run_id = $1 LIMIT 1;";
        cmd.Parameters.Add(new DuckDBParameter(runId));
        return cmd.ExecuteScalar() is string sj && !string.IsNullOrWhiteSpace(sj)
            ? JsonSerializer.Deserialize<List<List<StepInfo>>>(sj, JsonOpts)
            : null;
    }

    /// <summary>
    /// Builds the duration-over-time timeline, latency histogram, and paged individual calls for one endpoint in a stored run (brain 0060).
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="query">Endpoint name, time/latency filters, sorting, and paging parameters.</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>The endpoint detail payload, or <see langword="null"/> if the run does not exist.</returns>
    public Task<CallEndpointDetailDto?> GetCallEndpointDetailAsync(string runId, CallDetailQuery query, CancellationToken ct = default)
    {
        List<CallFact> facts;
        double elapsed;
        lock (dbLock)
        {
            using var rCmd = conn.CreateCommand();
            rCmd.CommandText = "SELECT elapsed_seconds FROM runs WHERE run_id = $1;";
            rCmd.Parameters.Add(new DuckDBParameter(runId));
            if (rCmd.ExecuteScalar() is not double el)
                return Task.FromResult<CallEndpointDetailDto?>(null);
            elapsed = el;
            facts = LoadCallFacts(runId);
        }
        return Task.FromResult(CallAnalytics.BuildEndpointDetail(
            facts, query.Endpoint, elapsed, query.Entry, query.Show, query.MinMs, query.MaxMs,
            query.FromSec, query.ToSec, query.Sort, query.Offset, query.Limit, query.MaxPoints));
    }

    private List<CallFact> LoadCallFacts(string runId)
    {
        var facts = new List<CallFact>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.id, c.sequence, b.entry, COALESCE(c.step, 0), COALESCE(c.rep, 0), b.started_at,
                   c.offset_ms, c.duration_ms, c.status_code, c.bytes, c.error, c.name, c.url,
                   c.endpoint, c.method, c.host, c.slow_threshold_ms,
                   c.ttfb_ms, c.response_read_ms, c.headers_json, c.trace_id, c.span_id
            FROM calls c
            JOIN bees b ON c.run_id = b.run_id AND c.sequence = b.sequence
            WHERE c.run_id = $1
            ORDER BY c.id;
            """;
        cmd.Parameters.Add(new DuckDBParameter(runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            IReadOnlyDictionary<string, string>? headers = null;
            if (!r.IsDBNull(19) && r.GetString(19) is { Length: > 0 } hJson)
                headers = JsonSerializer.Deserialize<Dictionary<string, string>>(hJson, JsonOpts);
            facts.Add(new CallFact(
                r.GetInt64(0),
                r.GetInt32(1),
                r.GetInt32(2),
                r.GetInt32(3),
                r.GetInt32(4),
                r.GetDouble(5),
                r.GetDouble(6),
                r.GetDouble(7),
                r.IsDBNull(8) ? null : r.GetInt32(8),
                r.GetInt64(9),
                r.IsDBNull(10) ? null : r.GetString(10),
                r.GetString(11),
                r.IsDBNull(12) ? null : r.GetString(12),
                r.IsDBNull(13) ? null : r.GetString(13),
                r.IsDBNull(14) ? null : r.GetString(14),
                r.IsDBNull(15) ? null : r.GetString(15),
                r.IsDBNull(16) ? null : r.GetDouble(16),
                r.IsDBNull(17) ? null : r.GetDouble(17),
                r.IsDBNull(18) ? null : r.GetDouble(18),
                headers,
                r.IsDBNull(20) ? null : r.GetString(20),
                r.IsDBNull(21) ? null : r.GetString(21)));
        }
        return facts;
    }

    /// <summary>
    /// Stores fetched distributed traces with the run (brain 0065): the fetch record and the spans of each trace; saving a trace again replaces both (a retry).
    /// </summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="traces">The traces to store (a trace that was not found has a record and no spans).</param>
    /// <param name="ct">Cancellation token (unused; operation completes synchronously under the database lock).</param>
    /// <returns>A completed task once the traces are stored.</returns>
    public Task SaveTracesAsync(string runId, IReadOnlyList<StoredTrace> traces, CancellationToken ct = default)
    {
        if (traces.Count == 0) return Task.CompletedTask;
        lock (dbLock) SaveTracesLocked(runId, traces);
        return Task.CompletedTask;
    }

    private void SaveTracesLocked(string runId, IReadOnlyList<StoredTrace> traces)
    {
        {
            foreach (var trace in traces)
            {
                var f = trace.Fetch;
                using (var del = conn.CreateCommand())
                {
                    del.CommandText = "DELETE FROM trace_fetches WHERE run_id = $1 AND trace_id = $2; DELETE FROM trace_spans WHERE run_id = $1 AND trace_id = $2;";
                    del.Parameters.Add(new DuckDBParameter(runId));
                    del.Parameters.Add(new DuckDBParameter(f.TraceId));
                    del.ExecuteNonQuery();
                }

                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = """
                        INSERT INTO trace_fetches(run_id, trace_id, sequence, status, reason, span_count, attempts, selected_for, fetched_utc, truncated)
                        VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10);
                        """;
                    ins.Parameters.Add(new DuckDBParameter(runId));
                    ins.Parameters.Add(new DuckDBParameter(f.TraceId));
                    ins.Parameters.Add(new DuckDBParameter(f.Sequence));
                    ins.Parameters.Add(new DuckDBParameter(f.Status));
                    ins.Parameters.Add(new DuckDBParameter((object?)f.Reason ?? DBNull.Value));
                    ins.Parameters.Add(new DuckDBParameter(f.SpanCount));
                    ins.Parameters.Add(new DuckDBParameter(f.Attempts));
                    ins.Parameters.Add(new DuckDBParameter(JsonSerializer.Serialize(f.SelectedFor, CompactJson)));
                    ins.Parameters.Add(new DuckDBParameter((object?)f.FetchedUtc?.ToString("O") ?? DBNull.Value));
                    ins.Parameters.Add(new DuckDBParameter(f.Truncated));
                    ins.ExecuteNonQuery();
                }

                if (trace.Spans.Count == 0) continue;
                using var app = conn.CreateAppender("trace_spans");
                foreach (var span in trace.Spans)
                {
                    app.CreateRow()
                        .AppendValue(runId)
                        .AppendValue(span.TraceId)
                        .AppendValue(span.SpanId)
                        .AppendValue(span.ParentSpanId)
                        .AppendValue(span.Service)
                        .AppendValue(span.Name)
                        .AppendValue(span.Kind)
                        .AppendValue(span.StartUnixNanos)
                        .AppendValue(span.DurationMs)
                        .AppendValue(span.IsError)
                        .AppendValue(span.StatusMessage)
                        .AppendValue(span.Attributes is { Count: > 0 } ? JsonSerializer.Serialize(span.Attributes, CompactJson) : null)
                        .EndRow();
                }
            }
        }
    }

    /// <summary>The fetch records of a run's traces, ordered by bee sequence then trace ID.</summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="ct">Cancellation token (unused).</param>
    /// <returns>One record per selected trace; empty when none were fetched.</returns>
    public Task<IReadOnlyList<TraceFetchDto>> GetTraceFetchesAsync(string runId, CancellationToken ct = default)
    {
        lock (dbLock)
            return Task.FromResult<IReadOnlyList<TraceFetchDto>>(ReadTraceFetches(runId));
    }

    private List<TraceFetchDto> ReadTraceFetches(string runId)
    {
        var list = new List<TraceFetchDto>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT trace_id, sequence, status, reason, span_count, attempts, selected_for, fetched_utc, COALESCE(truncated, FALSE)
            FROM trace_fetches
            WHERE run_id = $1
            ORDER BY sequence, trace_id;
            """;
        cmd.Parameters.Add(new DuckDBParameter(runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            IReadOnlyList<string> selected = !r.IsDBNull(6) && r.GetString(6) is { Length: > 0 } sj
                ? JsonSerializer.Deserialize<List<string>>(sj, CompactJson) ?? []
                : [];
            list.Add(new TraceFetchDto(
                r.GetString(0), r.GetInt32(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4), r.GetInt32(5),
                selected,
                r.IsDBNull(7) ? null : DateTimeOffset.Parse(r.GetString(7), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind),
                r.GetBoolean(8)));
        }
        return list;
    }

    /// <summary>The stored spans of a run's fetched traces.</summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="traceIds">Only these traces; all traces of the run when <see langword="null"/>.</param>
    /// <param name="ct">Cancellation token (unused).</param>
    /// <returns>The spans ordered by trace ID and start time.</returns>
    public Task<IReadOnlyList<TraceSpanDto>> GetTraceSpansAsync(string runId, IReadOnlyCollection<string>? traceIds = null, CancellationToken ct = default)
    {
        if (traceIds is { Count: 0 }) return Task.FromResult<IReadOnlyList<TraceSpanDto>>([]);
        var spans = new List<TraceSpanDto>();
        lock (dbLock)
        {
            using var cmd = conn.CreateCommand();
            var filter = "";
            cmd.Parameters.Add(new DuckDBParameter(runId));
            if (traceIds is not null)
            {
                filter = " AND trace_id IN (" + string.Join(", ", traceIds.Select((_, i) => "$" + (i + 2))) + ")";
                foreach (var id in traceIds) cmd.Parameters.Add(new DuckDBParameter(id));
            }
            cmd.CommandText = $"""
                SELECT trace_id, span_id, parent_span_id, service, name, kind, start_unix_nano, duration_ms, COALESCE(is_error, FALSE), status_message, attributes_json
                FROM trace_spans
                WHERE run_id = $1{filter}
                ORDER BY trace_id, start_unix_nano, span_id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                IReadOnlyDictionary<string, string>? attrs = !r.IsDBNull(10) && r.GetString(10) is { Length: > 0 } aj
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(aj, CompactJson)
                    : null;
                spans.Add(new TraceSpanDto(
                    r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5), r.GetInt64(6), r.GetDouble(7), r.GetBoolean(8), r.IsDBNull(9) ? null : r.GetString(9), attrs));
            }
        }
        return Task.FromResult<IReadOnlyList<TraceSpanDto>>(spans);
    }

    /// <summary>Every call of a run as an analytics fact.</summary>
    /// <param name="runId">Unique identifier of the run.</param>
    /// <param name="ct">Cancellation token used while flushing pending writes.</param>
    /// <returns>All calls; empty when the run is unknown.</returns>
    public async Task<IReadOnlyList<CallFact>> GetCallFactsAsync(string runId, CancellationToken ct = default)
    {
        await FlushAsync(ct);
        lock (dbLock) return LoadCallFacts(runId);
    }

    /// <summary>
    /// Flushes pending writes and exports a stored run in the requested format (<c>"parquet"</c>, <c>"csv"</c>, <c>"duckdb"</c>, or <c>"json"</c>).
    /// </summary>
    /// <param name="runId">Unique identifier of the run to export.</param>
    /// <param name="format">Export format name (defaults to <c>"parquet"</c> when blank; any unrecognized value exports JSON).</param>
    /// <param name="ct">Cancellation token used while flushing pending writes before export.</param>
    /// <returns>The exported file name, MIME content type, and raw bytes, or <see langword="null"/> if the run does not exist.</returns>
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
                // Parquet is single-table, so bundle all 4 tables (runs, bees, calls, logs) as ZSTD Parquet files in one .zip.
                var tmpDir = Path.Combine(Path.GetTempPath(), $"perftool-pq-{Guid.NewGuid():N}");
                Directory.CreateDirectory(tmpDir);
                try
                {
                    var tables = new (string File, string Sql)[]
                    {
                        ("runs.parquet", $"SELECT * FROM runs WHERE run_id = '{escapedId}'"),
                        ("run_steps.parquet", $"SELECT * FROM run_steps WHERE run_id = '{escapedId}'"),
                        ("bees.parquet", $"SELECT * FROM bees WHERE run_id = '{escapedId}' ORDER BY sequence"),
                        ("calls.parquet", $"SELECT * FROM calls WHERE run_id = '{escapedId}' ORDER BY sequence, call_index"),
                        ("logs.parquet", $"SELECT * FROM logs WHERE run_id = '{escapedId}' ORDER BY id"),
                        ("trace_fetches.parquet", $"SELECT * FROM trace_fetches WHERE run_id = '{escapedId}' ORDER BY sequence, trace_id"),
                        ("trace_spans.parquet", $"SELECT * FROM trace_spans WHERE run_id = '{escapedId}' ORDER BY trace_id, start_unix_nano, span_id"),
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
                                new { file = "bees.parquet", key = "(run_id, sequence)", description = "One row per bee execution (bar in the chart), with outcome, is_failed, is_slow and duration_ms." },
                                new { file = "calls.parquet", key = "(run_id, sequence, call_index)", description = "Individual HTTP requests or measured steps inside each bee; join to bees on (run_id, sequence)." },
                                new { file = "logs.parquet", key = "(run_id, id)", description = "Per-bee ILogger statements; join to bees on (run_id, sequence) and optionally calls on call_index." },
                                new { file = "trace_fetches.parquet", key = "(run_id, trace_id)", description = "One row per distributed trace Beesharp fetched from the trace store (status, reason when missing, span count); join to bees on (run_id, sequence)." },
                                new { file = "trace_spans.parquet", key = "(run_id, trace_id, span_id)", description = "The spans of the fetched traces (service, name, start, duration, attributes); join to calls on (run_id, trace_id, span_id) where the call's span is the parent." },
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
                            CREATE TABLE {alias}.run_steps AS SELECT * FROM run_steps WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.bees AS SELECT * FROM bees WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.calls AS SELECT * FROM calls WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.logs AS SELECT * FROM logs WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.trace_fetches AS SELECT * FROM trace_fetches WHERE run_id = '{escapedId}';
                            CREATE TABLE {alias}.trace_spans AS SELECT * FROM trace_spans WHERE run_id = '{escapedId}';
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

            // Default: JSON bundle with run + steps + bees + calls + logs
            var steps = LoadRunStepsLocked(runId);
            var bees = new List<object>();
            using (var aCmd = conn.CreateCommand())
            {
                aCmd.CommandText = "SELECT sequence, bee_name, entry, started_at, duration_ms, status_code, bytes, error, outcome, is_failed, is_slow, slow_calls, COALESCE(failed_step, -1), COALESCE(failed_rep, -1), step_visits_json, trace_id FROM bees WHERE run_id = $1 ORDER BY sequence;";
                aCmd.Parameters.Add(new DuckDBParameter(runId));
                using var ar = aCmd.ExecuteReader();
                while (ar.Read())
                {
                    IReadOnlyList<RecordedStepVisit>? sv = null;
                    if (!ar.IsDBNull(14) && ar.GetString(14) is { Length: > 0 } svJson)
                        sv = JsonSerializer.Deserialize<List<RecordedStepVisit>>(svJson, JsonOpts);
                    bees.Add(new
                    {
                        sequence = ar.GetInt32(0),
                        beeName = ar.GetString(1),
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
                        failedStep = ar.GetInt32(12),
                        failedRep = ar.GetInt32(13),
                        stepVisits = sv,
                        traceId = ar.IsDBNull(15) ? null : ar.GetString(15),
                    });
                }
            }

            var calls = new List<object>();
            using (var cCmd = conn.CreateCommand())
            {
                cCmd.CommandText = "SELECT sequence, call_index, name, offset_ms, duration_ms, status_code, bytes, error, COALESCE(step, 0), COALESCE(rep, 0), endpoint, method, host, url, slow_threshold_ms, ttfb_ms, response_read_ms, headers_json, trace_id, span_id FROM calls WHERE run_id = $1 ORDER BY sequence, call_index;";
                cCmd.Parameters.Add(new DuckDBParameter(runId));
                using var cr = cCmd.ExecuteReader();
                while (cr.Read())
                {
                    IReadOnlyDictionary<string, string>? headers = null;
                    if (!cr.IsDBNull(17) && cr.GetString(17) is { Length: > 0 } hJson)
                        headers = JsonSerializer.Deserialize<Dictionary<string, string>>(hJson, JsonOpts);
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
                        step = cr.GetInt32(8),
                        rep = cr.GetInt32(9),
                        endpoint = cr.IsDBNull(10) ? null : cr.GetString(10),
                        method = cr.IsDBNull(11) ? null : cr.GetString(11),
                        host = cr.IsDBNull(12) ? null : cr.GetString(12),
                        url = cr.IsDBNull(13) ? null : cr.GetString(13),
                        slowThresholdMs = cr.IsDBNull(14) ? (double?)null : cr.GetDouble(14),
                        ttfbMs = cr.IsDBNull(15) ? (double?)null : cr.GetDouble(15),
                        responseReadMs = cr.IsDBNull(16) ? (double?)null : cr.GetDouble(16),
                        headers,
                        traceId = cr.IsDBNull(18) ? null : cr.GetString(18),
                        spanId = cr.IsDBNull(19) ? null : cr.GetString(19),
                    });
                }
            }

            var logs = new List<object>();
            using (var lCmd = conn.CreateCommand())
            {
                lCmd.CommandText = "SELECT id, sequence, bee_name, entry, at_seconds, level, message, exception, call_index, is_failed, is_slow, COALESCE(step, 0) FROM logs WHERE run_id = $1 ORDER BY id;";
                lCmd.Parameters.Add(new DuckDBParameter(runId));
                using var lr = lCmd.ExecuteReader();
                while (lr.Read())
                {
                    logs.Add(new
                    {
                        id = lr.GetInt64(0),
                        sequence = lr.GetInt32(1),
                        beeName = lr.GetString(2),
                        entry = lr.GetInt32(3),
                        atSeconds = lr.GetDouble(4),
                        level = lr.GetString(5),
                        message = lr.GetString(6),
                        exception = lr.IsDBNull(7) ? null : lr.GetString(7),
                        callIndex = lr.IsDBNull(8) ? (int?)null : lr.GetInt32(8),
                        isFailed = lr.GetBoolean(9),
                        isSlow = lr.GetBoolean(10),
                        step = lr.GetInt32(11),
                    });
                }
            }

            var traceFetches = ReadTraceFetches(runId);
            var traceSpans = new List<TraceSpanDto>();
            using (var tCmd = conn.CreateCommand())
            {
                tCmd.CommandText = "SELECT trace_id, span_id, parent_span_id, service, name, kind, start_unix_nano, duration_ms, COALESCE(is_error, FALSE), status_message, attributes_json FROM trace_spans WHERE run_id = $1 ORDER BY trace_id, start_unix_nano, span_id;";
                tCmd.Parameters.Add(new DuckDBParameter(runId));
                using var tr = tCmd.ExecuteReader();
                while (tr.Read())
                {
                    IReadOnlyDictionary<string, string>? attrs = !tr.IsDBNull(10) && tr.GetString(10) is { Length: > 0 } aj
                        ? JsonSerializer.Deserialize<Dictionary<string, string>>(aj, CompactJson)
                        : null;
                    traceSpans.Add(new TraceSpanDto(
                        tr.GetString(0), tr.GetString(1), tr.IsDBNull(2) ? null : tr.GetString(2), tr.GetString(3), tr.GetString(4),
                        tr.IsDBNull(5) ? null : tr.GetString(5), tr.GetInt64(6), tr.GetDouble(7), tr.GetBoolean(8), tr.IsDBNull(9) ? null : tr.GetString(9), attrs));
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
                steps,
                bees,
                calls,
                logs,
                traceFetches,
                traceSpans,
            };
            return new RunExportResult($"{safeRunId}.json", "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(payload, JsonOpts));
        }
    }

    /// <summary>
    /// Imports a previously exported run (<c>.zip</c> Parquet bundle, <c>.duckdb</c> file, or <c>.json</c> bundle) into this database,
    /// generating a unique suffix if the source run identifier already exists.
    /// </summary>
    /// <param name="fileName">Original upload file name used to determine the export format from its extension.</param>
    /// <param name="content">Readable stream containing the exported run payload.</param>
    /// <param name="ct">Cancellation token for asynchronous stream and JSON parsing operations.</param>
    /// <returns>Summary metadata for the newly imported run.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the file extension is unsupported or the export payload is invalid.</exception>
    public async Task<StoredRunSummary> ImportRunAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await FlushAsync(ct);

        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        if (ext is not (".zip" or ".duckdb" or ".json"))
            throw new InvalidOperationException("Unsupported file type. Please choose a .zip (Parquet bundle), .duckdb, or .json run export.");

        if (ext == ".json")
        {
            using var doc = await JsonDocument.ParseAsync(content, cancellationToken: ct);
            return ImportJsonRun(doc.RootElement);
        }

        var tmpDir = Path.Combine(Path.GetTempPath(), $"perftool-imp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var tmpFile = Path.Combine(tmpDir, "upload" + ext);
            await using (var fs = File.Create(tmpFile))
                await content.CopyToAsync(fs, ct);

            if (ext == ".zip")
            {
                ZipFile.ExtractToDirectory(tmpFile, tmpDir, overwriteFiles: true);
                return ImportParquetDirectory(tmpDir);
            }

            return ImportDuckDbFile(tmpFile);
        }
        finally
        {
            if (Directory.Exists(tmpDir)) Directory.Delete(tmpDir, true);
        }
    }

    private StoredRunSummary ImportParquetDirectory(string dir)
    {
        var runsPath = Path.Combine(dir, "runs.parquet");
        if (!File.Exists(runsPath))
            throw new InvalidOperationException("Invalid Parquet export: runs.parquet is missing.");

        lock (dbLock)
        {
            string sourceRunId;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = $"SELECT run_id FROM read_parquet('{EscapeSql(runsPath)}') LIMIT 1;";
                sourceRunId = cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("runs.parquet does not contain any run.");
            }

            var targetRunId = ResolveImportRunId(sourceRunId);
            var escapedTarget = EscapeSql(targetRunId);

            ExecuteSql($"INSERT INTO runs BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM read_parquet('{EscapeSql(runsPath)}') LIMIT 1;");

            var stepsPath = Path.Combine(dir, "run_steps.parquet");
            if (File.Exists(stepsPath))
                ExecuteSql($"INSERT INTO run_steps BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM read_parquet('{EscapeSql(stepsPath)}');");

            var beesPath = Path.Combine(dir, "bees.parquet");
            if (File.Exists(beesPath))
                ExecuteSql($"INSERT INTO bees BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM read_parquet('{EscapeSql(beesPath)}');");

            var callsPath = Path.Combine(dir, "calls.parquet");
            if (File.Exists(callsPath))
            {
                ExecuteSql($"INSERT INTO calls BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id, {nextCallId} + ROW_NUMBER() OVER () AS id) FROM read_parquet('{EscapeSql(callsPath)}');");
                RefreshMaxCallId();
            }

            var logsPath = Path.Combine(dir, "logs.parquet");
            if (File.Exists(logsPath))
            {
                ExecuteSql($"INSERT INTO logs BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id, {nextLogId} + ROW_NUMBER() OVER () AS id) FROM read_parquet('{EscapeSql(logsPath)}');");
                RefreshMaxLogId();
            }

            var fetchesPath = Path.Combine(dir, "trace_fetches.parquet");
            if (File.Exists(fetchesPath))
                ExecuteSql($"INSERT INTO trace_fetches BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM read_parquet('{EscapeSql(fetchesPath)}');");

            var spansPath = Path.Combine(dir, "trace_spans.parquet");
            if (File.Exists(spansPath))
                ExecuteSql($"INSERT INTO trace_spans BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM read_parquet('{EscapeSql(spansPath)}');");

            return ReadRunSummary(targetRunId);
        }
    }

    private StoredRunSummary ImportDuckDbFile(string filePath)
    {
        var alias = $"imp_{Guid.NewGuid():N}";
        lock (dbLock)
        {
            ExecuteSql($"ATTACH '{EscapeSql(filePath)}' AS {alias} (READ_ONLY);");
            try
            {
                string sourceRunId;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = $"SELECT run_id FROM {alias}.runs LIMIT 1;";
                    sourceRunId = cmd.ExecuteScalar() as string ?? throw new InvalidOperationException("The DuckDB file does not contain any run.");
                }

                var targetRunId = ResolveImportRunId(sourceRunId);
                var escapedTarget = EscapeSql(targetRunId);

                ExecuteSql($"INSERT INTO runs BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM {alias}.runs LIMIT 1;");
                if (TableExists(alias, "run_steps"))
                    ExecuteSql($"INSERT INTO run_steps BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM {alias}.run_steps;");
                if (TableExists(alias, "bees"))
                    ExecuteSql($"INSERT INTO bees BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM {alias}.bees;");
                if (TableExists(alias, "calls"))
                {
                    ExecuteSql($"INSERT INTO calls BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id, {nextCallId} + ROW_NUMBER() OVER () AS id) FROM {alias}.calls;");
                    RefreshMaxCallId();
                }
                if (TableExists(alias, "logs"))
                {
                    ExecuteSql($"INSERT INTO logs BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id, {nextLogId} + ROW_NUMBER() OVER () AS id) FROM {alias}.logs;");
                    RefreshMaxLogId();
                }
                if (TableExists(alias, "trace_fetches"))
                    ExecuteSql($"INSERT INTO trace_fetches BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM {alias}.trace_fetches;");
                if (TableExists(alias, "trace_spans"))
                    ExecuteSql($"INSERT INTO trace_spans BY NAME SELECT * REPLACE ('{escapedTarget}' AS run_id) FROM {alias}.trace_spans;");

                return ReadRunSummary(targetRunId);
            }
            finally
            {
                try { ExecuteSql($"DETACH {alias};"); } catch { }
            }
        }
    }

    private StoredRunSummary ImportJsonRun(JsonElement root)
    {
        if (!root.TryGetProperty("runId", out var runIdEl) || runIdEl.GetString() is not { Length: > 0 } sourceRunId)
            throw new InvalidOperationException("Invalid JSON export: missing runId.");

        var startedUtc = root.TryGetProperty("startedUtc", out var su) && su.GetString() is { } s1 ? s1 : DateTimeOffset.UtcNow.ToString("O");
        var finishedUtc = root.TryGetProperty("finishedUtc", out var fu) && fu.ValueKind == JsonValueKind.String ? fu.GetString() : null;
        var state = root.TryGetProperty("state", out var st) && st.GetString() is { } s2 ? s2 : "Finished";
        var elapsedSeconds = root.TryGetProperty("elapsedSeconds", out var es) && es.TryGetDouble(out var eVal) ? eVal : 0;
        var error = root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String ? errEl.GetString() : null;
        var planJson = root.TryGetProperty("plan", out var planEl) ? planEl.GetRawText() : "{}";

        lock (dbLock)
        {
            var targetRunId = ResolveImportRunId(sourceRunId);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO runs(run_id, started_utc, finished_utc, state, elapsed_seconds, plan_json, error) VALUES ($1, $2, $3, $4, $5, $6, $7);";
                cmd.Parameters.Add(new DuckDBParameter(targetRunId));
                cmd.Parameters.Add(new DuckDBParameter(startedUtc));
                cmd.Parameters.Add(new DuckDBParameter((object?)finishedUtc ?? DBNull.Value));
                cmd.Parameters.Add(new DuckDBParameter(state));
                cmd.Parameters.Add(new DuckDBParameter(elapsedSeconds));
                cmd.Parameters.Add(new DuckDBParameter(planJson));
                cmd.Parameters.Add(new DuckDBParameter((object?)error ?? DBNull.Value));
                cmd.ExecuteNonQuery();
            }

            if (root.TryGetProperty("bees", out var beesEl) && beesEl.ValueKind == JsonValueKind.Array && beesEl.GetArrayLength() > 0)
            {
                using var app = conn.CreateAppender("bees");
                foreach (var b in beesEl.EnumerateArray())
                {
                    var seq = b.TryGetProperty("sequence", out var p0) ? p0.GetInt32() : 0;
                    var name = b.TryGetProperty("beeName", out var p1) ? p1.GetString() ?? $"B{seq + 1}" : $"B{seq + 1}";
                    var entry = b.TryGetProperty("entry", out var p2) ? p2.GetInt32() : 0;
                    var startedAt = b.TryGetProperty("startedAt", out var p3) ? p3.GetDouble() : 0;
                    var durMs = b.TryGetProperty("durationMs", out var p4) ? p4.GetDouble() : 0;
                    var statusCode = b.TryGetProperty("statusCode", out var p5) && p5.ValueKind == JsonValueKind.Number ? p5.GetInt32() : (int?)null;
                    var bytes = b.TryGetProperty("bytes", out var p6) ? p6.GetInt64() : 0L;
                    var bErr = b.TryGetProperty("error", out var p7) && p7.ValueKind == JsonValueKind.String ? p7.GetString() : null;
                    var outcome = b.TryGetProperty("outcome", out var p8) ? p8.GetInt32() : 0;
                    var isFailed = b.TryGetProperty("isFailed", out var p9) && p9.GetBoolean();
                    var isSlow = b.TryGetProperty("isSlow", out var p10) && p10.GetBoolean();
                    var slowCalls = b.TryGetProperty("slowCalls", out var p11) ? p11.GetInt32() : 0;
                    var failedStep = b.TryGetProperty("failedStep", out var p12) ? p12.GetInt32() : -1;
                    var failedRep = b.TryGetProperty("failedRep", out var p13) ? p13.GetInt32() : -1;
                    var beeTraceId = b.TryGetProperty("traceId", out var p14) && p14.ValueKind == JsonValueKind.String ? p14.GetString() : null;
                    app.CreateRow()
                        .AppendValue(targetRunId).AppendValue(seq).AppendValue(name).AppendValue(entry)
                        .AppendValue(startedAt).AppendValue(durMs).AppendValue(statusCode).AppendValue(bytes)
                        .AppendValue(bErr).AppendValue(outcome).AppendValue(isFailed).AppendValue(isSlow)
                        .AppendValue(slowCalls).AppendValue(0).AppendValue(0)
                        .AppendValue(failedStep).AppendValue(failedRep).AppendValue((string?)null)
                        .AppendValue(beeTraceId)
                        .EndRow();
                }
            }

            if (root.TryGetProperty("calls", out var callsEl) && callsEl.ValueKind == JsonValueKind.Array && callsEl.GetArrayLength() > 0)
            {
                using var app = conn.CreateAppender("calls");
                foreach (var c in callsEl.EnumerateArray())
                {
                    var id = ++nextCallId;
                    var seq = c.TryGetProperty("sequence", out var p0) ? p0.GetInt32() : 0;
                    var callIdx = c.TryGetProperty("callIndex", out var p1) ? p1.GetInt32() : 0;
                    var name = c.TryGetProperty("name", out var p2) ? p2.GetString() ?? "" : "";
                    var offsetMs = c.TryGetProperty("offsetMs", out var p3) ? p3.GetDouble() : 0;
                    var durMs = c.TryGetProperty("durationMs", out var p4) ? p4.GetDouble() : 0;
                    var statusCode = c.TryGetProperty("statusCode", out var p5) && p5.ValueKind == JsonValueKind.Number ? p5.GetInt32() : (int?)null;
                    var bytes = c.TryGetProperty("bytes", out var p6) ? p6.GetInt64() : 0L;
                    var cErr = c.TryGetProperty("error", out var p7) && p7.ValueKind == JsonValueKind.String ? p7.GetString() : null;
                    var step = c.TryGetProperty("step", out var p8) ? p8.GetInt32() : 0;
                    var rep = c.TryGetProperty("rep", out var p9) ? p9.GetInt32() : 0;
                    var endpoint = c.TryGetProperty("endpoint", out var p10) && p10.ValueKind == JsonValueKind.String ? p10.GetString() : null;
                    var method = c.TryGetProperty("method", out var p11) && p11.ValueKind == JsonValueKind.String ? p11.GetString() : null;
                    var host = c.TryGetProperty("host", out var p12) && p12.ValueKind == JsonValueKind.String ? p12.GetString() : null;
                    var url = c.TryGetProperty("url", out var p13) && p13.ValueKind == JsonValueKind.String ? p13.GetString() : null;
                    var slowThr = c.TryGetProperty("slowThresholdMs", out var p14) && p14.ValueKind == JsonValueKind.Number ? p14.GetDouble() : (double?)null;
                    var ttfbMs = c.TryGetProperty("ttfbMs", out var p15) && p15.ValueKind == JsonValueKind.Number ? p15.GetDouble() : (double?)null;
                    var respReadMs = c.TryGetProperty("responseReadMs", out var p16) && p16.ValueKind == JsonValueKind.Number ? p16.GetDouble() : (double?)null;
                    var headersJson = c.TryGetProperty("headers", out var p17) && p17.ValueKind == JsonValueKind.Object ? p17.GetRawText() : null;
                    var callTraceId = c.TryGetProperty("traceId", out var p18) && p18.ValueKind == JsonValueKind.String ? p18.GetString() : null;
                    var callSpanId = c.TryGetProperty("spanId", out var p19) && p19.ValueKind == JsonValueKind.String ? p19.GetString() : null;
                    app.CreateRow()
                        .AppendValue(id).AppendValue(targetRunId).AppendValue(seq).AppendValue(callIdx)
                        .AppendValue(name).AppendValue(offsetMs).AppendValue(durMs).AppendValue(statusCode)
                        .AppendValue(bytes).AppendValue(cErr).AppendValue(step).AppendValue(rep)
                        .AppendValue(endpoint).AppendValue(method).AppendValue(host).AppendValue(url).AppendValue(slowThr)
                        .AppendValue(ttfbMs).AppendValue(respReadMs).AppendValue(headersJson)
                        .AppendValue(callTraceId).AppendValue(callSpanId)
                        .EndRow();
                }
            }

            if (root.TryGetProperty("logs", out var logsEl) && logsEl.ValueKind == JsonValueKind.Array && logsEl.GetArrayLength() > 0)
            {
                using var app = conn.CreateAppender("logs");
                foreach (var l in logsEl.EnumerateArray())
                {
                    var id = ++nextLogId;
                    var seq = l.TryGetProperty("sequence", out var p0) ? p0.GetInt32() : 0;
                    var beeName = l.TryGetProperty("beeName", out var p1) ? p1.GetString() ?? "" : "";
                    var entry = l.TryGetProperty("entry", out var p2) ? p2.GetInt32() : 0;
                    var atSec = l.TryGetProperty("atSeconds", out var p3) ? p3.GetDouble() : 0;
                    var level = l.TryGetProperty("level", out var p4) ? p4.GetString() ?? "Information" : "Information";
                    var msg = l.TryGetProperty("message", out var p5) ? p5.GetString() ?? "" : "";
                    var exc = l.TryGetProperty("exception", out var p6) && p6.ValueKind == JsonValueKind.String ? p6.GetString() : null;
                    var callIdx = l.TryGetProperty("callIndex", out var p7) && p7.ValueKind == JsonValueKind.Number ? p7.GetInt32() : (int?)null;
                    var isFailed = l.TryGetProperty("isFailed", out var p8) && p8.GetBoolean();
                    var isSlow = l.TryGetProperty("isSlow", out var p9) && p9.GetBoolean();
                    var step = l.TryGetProperty("step", out var p10) ? p10.GetInt32() : 0;
                    app.CreateRow()
                        .AppendValue(id).AppendValue(targetRunId).AppendValue(seq).AppendValue(beeName)
                        .AppendValue(entry).AppendValue(atSec).AppendValue(LevelRank(level)).AppendValue(level)
                        .AppendValue(msg).AppendValue(exc).AppendValue(callIdx)
                        .AppendValue(isFailed ? 2 : isSlow ? 1 : 0).AppendValue(isFailed).AppendValue(isSlow)
                        .AppendValue(step).AppendValue(0)
                        .EndRow();
                }
            }

            var importedTraces = new List<StoredTrace>();
            if (root.TryGetProperty("traceFetches", out var fetchesEl) && fetchesEl.ValueKind == JsonValueKind.Array)
            {
                var spansByTrace = new Dictionary<string, List<TraceSpanDto>>(StringComparer.Ordinal);
                if (root.TryGetProperty("traceSpans", out var spansEl) && spansEl.ValueKind == JsonValueKind.Array)
                    foreach (var span in spansEl.EnumerateArray().Select(e => e.Deserialize<TraceSpanDto>(CompactJson)).OfType<TraceSpanDto>())
                    {
                        if (!spansByTrace.TryGetValue(span.TraceId, out var list)) spansByTrace[span.TraceId] = list = [];
                        list.Add(span);
                    }
                foreach (var fetch in fetchesEl.EnumerateArray().Select(e => e.Deserialize<TraceFetchDto>(CompactJson)).OfType<TraceFetchDto>())
                    importedTraces.Add(new StoredTrace(fetch, spansByTrace.GetValueOrDefault(fetch.TraceId) ?? []));
            }
            if (importedTraces.Count > 0) SaveTracesLocked(targetRunId, importedTraces);

            return ReadRunSummary(targetRunId);
        }
    }

    private string ResolveImportRunId(string sourceRunId)
    {
        using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM runs WHERE run_id = $1;";
        check.Parameters.Add(new DuckDBParameter(sourceRunId));
        var count = Convert.ToInt64(check.ExecuteScalar());
        return count == 0 ? sourceRunId : $"{sourceRunId}-imp-{Guid.NewGuid().ToString("N")[..6]}";
    }

    private bool TableExists(string schema, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_catalog = $1 AND table_name = $2;";
        cmd.Parameters.Add(new DuckDBParameter(schema));
        cmd.Parameters.Add(new DuckDBParameter(table));
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private void ExecuteSql(string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void RefreshMaxCallId()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM calls;";
        nextCallId = Convert.ToInt64(cmd.ExecuteScalar());
    }

    private void RefreshMaxLogId()
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM logs;";
        nextLogId = Convert.ToInt64(cmd.ExecuteScalar());
    }

    private StoredRunSummary ReadRunSummary(string targetRunId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, r.started_utc, r.finished_utc, r.state, r.elapsed_seconds,
                   (SELECT COUNT(*) FROM bees b WHERE b.run_id = r.run_id),
                   (SELECT COUNT(*) FROM bees b WHERE b.run_id = r.run_id AND b.is_failed),
                   (SELECT COUNT(*) FROM logs l WHERE l.run_id = r.run_id),
                   r.error
            FROM runs r WHERE r.run_id = $1;
            """;
        cmd.Parameters.Add(new DuckDBParameter(targetRunId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException($"Imported run '{targetRunId}' could not be read.");
        return new StoredRunSummary(
            reader.GetString(0),
            DateTimeOffset.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : DateTimeOffset.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.GetDouble(4),
            Convert.ToInt32(reader.GetValue(5)),
            Convert.ToInt32(reader.GetValue(6)),
            Convert.ToInt64(reader.GetValue(7)),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    /// <summary>
    /// Completes the write channel, waits up to 5 seconds for pending batches to flush, cancels the writer loop, and disposes the DuckDB connection.
    /// </summary>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous dispose operation.</returns>
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
