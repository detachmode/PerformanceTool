using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using PerformanceTool.Services;

namespace PerformanceTool.Services.Storage;

/// <summary>
/// High-throughput SQLite run storage (brain 0045): WAL mode + a dedicated background writer draining a
/// <see cref="Channel{T}"/> in batched transactions (up to 2,000 items or 100 ms per commit).
/// Sustains tens of thousands of log rows/second without blocking <see cref="LoadRunner"/> actors,
/// while supporting indexed, server-side filtered &amp; paged queries for the UI.
/// </summary>
public sealed class SqliteRunStorage : IRunStorage
{
    private const int MaxBatchSize = 2000;
    private static readonly TimeSpan MaxBatchDelay = TimeSpan.FromMilliseconds(100);
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly string connectionString;
    private readonly Channel<WriteOp> queue;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task writerTask;

    public string ProviderName => "Sqlite";
    public string DatabasePath { get; }

    private abstract record WriteOp;
    private sealed record StartRunOp(string RunId, DateTimeOffset StartedUtc, string PlanJson) : WriteOp;
    private sealed record ActorOp(string RunId, ExecutionResult Result, int ActorOutcome, int SlowCalls) : WriteOp;
    private sealed record FinishRunOp(string RunId, DateTimeOffset FinishedUtc, string State, double ElapsedSeconds, string? Error) : WriteOp;
    private sealed record FlushOp(TaskCompletionSource Done) : WriteOp;

    public SqliteRunStorage(IConfiguration configuration, IWebHostEnvironment env)
        : this(ResolvePath(configuration, env))
    {
    }

    public SqliteRunStorage(string dbPath)
    {
        DatabasePath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

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
        var configured = configuration["Storage:SqlitePath"];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(env.ContentRootPath, "data", "perftool.db")
            : Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
    }

    private void InitializeSchema()
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -32000;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                started_utc TEXT NOT NULL,
                finished_utc TEXT NULL,
                state TEXT NOT NULL,
                elapsed_seconds REAL NOT NULL DEFAULT 0,
                plan_json TEXT NOT NULL,
                error TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS actors (
                run_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                actor_name TEXT NOT NULL,
                entry INTEGER NOT NULL,
                started_at REAL NOT NULL,
                duration_ms REAL NOT NULL,
                status_code INTEGER NULL,
                bytes INTEGER NOT NULL,
                error TEXT NULL,
                outcome INTEGER NOT NULL,
                is_failed INTEGER NOT NULL,
                is_slow INTEGER NOT NULL,
                slow_calls INTEGER NOT NULL,
                call_count INTEGER NOT NULL,
                log_count INTEGER NOT NULL,
                PRIMARY KEY (run_id, sequence)
            );
            CREATE INDEX IF NOT EXISTS idx_actors_run_entry_seq ON actors(run_id, entry, sequence);
            CREATE INDEX IF NOT EXISTS idx_actors_run_outcome ON actors(run_id, outcome, sequence);
            CREATE INDEX IF NOT EXISTS idx_actors_run_failed ON actors(run_id, is_failed, sequence);
            CREATE INDEX IF NOT EXISTS idx_actors_run_slow ON actors(run_id, is_slow, sequence);
            CREATE INDEX IF NOT EXISTS idx_actors_run_name ON actors(run_id, actor_name);

            CREATE TABLE IF NOT EXISTS calls (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                call_index INTEGER NOT NULL,
                name TEXT NOT NULL,
                offset_ms REAL NOT NULL,
                duration_ms REAL NOT NULL,
                status_code INTEGER NULL,
                bytes INTEGER NOT NULL,
                error TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_calls_run_seq ON calls(run_id, sequence, call_index);

            CREATE TABLE IF NOT EXISTS logs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                actor_name TEXT NOT NULL,
                entry INTEGER NOT NULL,
                at_seconds REAL NOT NULL,
                level_rank INTEGER NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL,
                exception TEXT NULL,
                call_index INTEGER NULL,
                actor_outcome INTEGER NOT NULL,
                is_failed INTEGER NOT NULL,
                is_slow INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_logs_run_id ON logs(run_id, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_entry_id ON logs(run_id, entry, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_seq_id ON logs(run_id, sequence, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_actor_name_id ON logs(run_id, actor_name, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_outcome_id ON logs(run_id, actor_outcome, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_failed_id ON logs(run_id, is_failed, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_slow_id ON logs(run_id, is_slow, id);
            CREATE INDEX IF NOT EXISTS idx_logs_run_level_id ON logs(run_id, level_rank, id);
            """;
        cmd.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA synchronous = NORMAL; PRAGMA temp_store = MEMORY; PRAGMA busy_timeout = 5000;";
        pragma.ExecuteNonQuery();
        return conn;
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
        using var conn = OpenConnection();
        using var startRunCmd = PrepareStartRun(conn);
        using var finishRunCmd = PrepareFinishRun(conn);
        using var insertActorCmd = PrepareInsertActor(conn);
        using var insertCallCmd = PrepareInsertCall(conn);
        using var insertLogCmd = PrepareInsertLog(conn);

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
                    using var tx = conn.BeginTransaction();
                    startRunCmd.Transaction = tx;
                    finishRunCmd.Transaction = tx;
                    insertActorCmd.Transaction = tx;
                    insertCallCmd.Transaction = tx;
                    insertLogCmd.Transaction = tx;

                    foreach (var op in batch)
                    {
                        switch (op)
                        {
                            case StartRunOp s:
                                startRunCmd.Parameters["$id"].Value = s.RunId;
                                startRunCmd.Parameters["$started"].Value = s.StartedUtc.ToString("O");
                                startRunCmd.Parameters["$plan"].Value = s.PlanJson;
                                startRunCmd.ExecuteNonQuery();
                                break;

                            case FinishRunOp f:
                                finishRunCmd.Parameters["$id"].Value = f.RunId;
                                finishRunCmd.Parameters["$finished"].Value = f.FinishedUtc.ToString("O");
                                finishRunCmd.Parameters["$state"].Value = f.State;
                                finishRunCmd.Parameters["$elapsed"].Value = f.ElapsedSeconds;
                                finishRunCmd.Parameters["$error"].Value = (object?)f.Error ?? DBNull.Value;
                                finishRunCmd.ExecuteNonQuery();
                                break;

                            case ActorOp a:
                                var r = a.Result;
                                var actorName = $"Actor #{r.Sequence + 1}";
                                var isFailed = a.ActorOutcome == 2 ? 1 : 0;
                                var isSlow = a.SlowCalls > 0 ? 1 : 0;
                                insertActorCmd.Parameters["$runId"].Value = a.RunId;
                                insertActorCmd.Parameters["$seq"].Value = r.Sequence;
                                insertActorCmd.Parameters["$actorName"].Value = actorName;
                                insertActorCmd.Parameters["$entry"].Value = r.Entry;
                                insertActorCmd.Parameters["$startedAt"].Value = r.StartedAtSeconds;
                                insertActorCmd.Parameters["$dur"].Value = r.ElapsedMs;
                                insertActorCmd.Parameters["$status"].Value = (object?)r.StatusCode ?? DBNull.Value;
                                insertActorCmd.Parameters["$bytes"].Value = r.Bytes;
                                insertActorCmd.Parameters["$err"].Value = (object?)r.Error ?? DBNull.Value;
                                insertActorCmd.Parameters["$outcome"].Value = a.ActorOutcome;
                                insertActorCmd.Parameters["$isFailed"].Value = isFailed;
                                insertActorCmd.Parameters["$isSlow"].Value = isSlow;
                                insertActorCmd.Parameters["$slowCalls"].Value = a.SlowCalls;
                                insertActorCmd.Parameters["$callCount"].Value = r.Calls?.Count ?? 0;
                                insertActorCmd.Parameters["$logCount"].Value = r.Logs?.Count ?? 0;
                                insertActorCmd.ExecuteNonQuery();

                                if (r.Calls is { Count: > 0 } calls)
                                {
                                    for (var i = 0; i < calls.Count; i++)
                                    {
                                        var c = calls[i];
                                        insertCallCmd.Parameters["$runId"].Value = a.RunId;
                                        insertCallCmd.Parameters["$seq"].Value = r.Sequence;
                                        insertCallCmd.Parameters["$idx"].Value = i;
                                        insertCallCmd.Parameters["$name"].Value = c.Name;
                                        insertCallCmd.Parameters["$offset"].Value = c.OffsetMs;
                                        insertCallCmd.Parameters["$dur"].Value = c.DurationMs;
                                        insertCallCmd.Parameters["$status"].Value = (object?)c.StatusCode ?? DBNull.Value;
                                        insertCallCmd.Parameters["$bytes"].Value = c.Bytes;
                                        insertCallCmd.Parameters["$err"].Value = (object?)c.Error ?? DBNull.Value;
                                        insertCallCmd.ExecuteNonQuery();
                                    }
                                }

                                if (r.Logs is { Count: > 0 } logs)
                                {
                                    for (var i = 0; i < logs.Count; i++)
                                    {
                                        var l = logs[i];
                                        insertLogCmd.Parameters["$runId"].Value = a.RunId;
                                        insertLogCmd.Parameters["$seq"].Value = r.Sequence;
                                        insertLogCmd.Parameters["$actorName"].Value = actorName;
                                        insertLogCmd.Parameters["$entry"].Value = r.Entry;
                                        insertLogCmd.Parameters["$at"].Value = l.AtSeconds;
                                        insertLogCmd.Parameters["$rank"].Value = LevelRank(l.Level);
                                        insertLogCmd.Parameters["$level"].Value = l.Level;
                                        insertLogCmd.Parameters["$msg"].Value = l.Message;
                                        insertLogCmd.Parameters["$ex"].Value = (object?)l.Exception ?? DBNull.Value;
                                        insertLogCmd.Parameters["$callIdx"].Value = (object?)l.CallIndex ?? DBNull.Value;
                                        insertLogCmd.Parameters["$outcome"].Value = a.ActorOutcome;
                                        insertLogCmd.Parameters["$isFailed"].Value = isFailed;
                                        insertLogCmd.Parameters["$isSlow"].Value = isSlow;
                                        insertLogCmd.ExecuteNonQuery();
                                    }
                                }
                                break;
                        }
                    }

                    tx.Commit();
                }

                foreach (var f in flushes) f.TrySetResult();
            }
        }
        catch (OperationCanceledException)
        {
            foreach (var f in flushes) f.TrySetResult();
        }
    }

    public Task<IReadOnlyList<StoredRunSummary>> ListRunsAsync(int limit = 50, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT r.run_id, r.started_utc, r.finished_utc, r.state, r.elapsed_seconds, r.error,
                   (SELECT COUNT(*) FROM actors a WHERE a.run_id = r.run_id) AS total_actors,
                   (SELECT COUNT(*) FROM actors a WHERE a.run_id = r.run_id AND a.outcome = 2) AS failed_actors,
                   (SELECT COUNT(*) FROM logs l WHERE l.run_id = r.run_id) AS total_logs
            FROM runs r
            ORDER BY r.started_utc DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

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
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetInt64(8),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return Task.FromResult<IReadOnlyList<StoredRunSummary>>(list);
    }

    public Task<StoredActorDetail?> GetActorDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        using var conn = OpenConnection();
        using var actorCmd = conn.CreateCommand();
        actorCmd.CommandText = """
            SELECT entry, started_at, duration_ms, status_code, bytes, error
            FROM actors
            WHERE run_id = $runId AND sequence = $seq;
            """;
        actorCmd.Parameters.AddWithValue("$runId", runId);
        actorCmd.Parameters.AddWithValue("$seq", sequence);

        int entry;
        double startedAt, durationMs;
        int? statusCode;
        long bytes;
        string? error;
        using (var reader = actorCmd.ExecuteReader())
        {
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
                WHERE run_id = $runId AND sequence = $seq
                ORDER BY call_index;
                """;
            callCmd.Parameters.AddWithValue("$runId", runId);
            callCmd.Parameters.AddWithValue("$seq", sequence);
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
                WHERE run_id = $runId AND sequence = $seq
                ORDER BY id;
                """;
            logCmd.Parameters.AddWithValue("$runId", runId);
            logCmd.Parameters.AddWithValue("$seq", sequence);
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

    public Task<LogPageResult> QueryLogsAsync(string runId, LogQuery query, CancellationToken ct = default)
    {
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var offset = Math.Max(0, query.Offset);

        using var conn = OpenConnection();
        var where = new StringBuilder("WHERE run_id = $runId");
        var parameters = new List<(string Name, object Value)> { ("$runId", runId) };

        if (query.Entry is { } e) { where.Append(" AND entry = $entry"); parameters.Add(("$entry", e)); }
        if (query.Actor is { } a) { where.Append(" AND sequence = $actor"); parameters.Add(("$actor", a)); }
        if (!string.IsNullOrWhiteSpace(query.ActorName)) { where.Append(" AND actor_name = $actorName"); parameters.Add(("$actorName", query.ActorName.Trim())); }
        if (query.Outcome is { } o) { where.Append(" AND actor_outcome = $outcome"); parameters.Add(("$outcome", o)); }
        if (query.Failed is { } fl) { where.Append(" AND is_failed = $failed"); parameters.Add(("$failed", fl ? 1 : 0)); }
        if (query.Slow is { } sl) { where.Append(" AND is_slow = $slow"); parameters.Add(("$slow", sl ? 1 : 0)); }
        if (query.Levels is { Count: > 0 } levels && levels.Count < 5)
        {
            var placeholders = new List<string>(levels.Count);
            for (var i = 0; i < levels.Count; i++)
            {
                var p = $"$lv{i}";
                placeholders.Add(p);
                parameters.Add((p, levels[i]));
            }
            where.Append($" AND level IN ({string.Join(",", placeholders)})");
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            where.Append(" AND (message LIKE $search ESCAPE '\\' OR COALESCE(exception, '') LIKE $search ESCAPE '\\' OR actor_name LIKE $search ESCAPE '\\')");
            var escaped = query.Search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            parameters.Add(("$search", $"%{escaped}%"));
        }

        long total;
        using (var countCmd = conn.CreateCommand())
        {
            countCmd.CommandText = $"SELECT COUNT(*) FROM logs {where};";
            foreach (var (n, v) in parameters) countCmd.Parameters.AddWithValue(n, v);
            total = Convert.ToInt64(countCmd.ExecuteScalar());
        }

        var items = new List<StoredLogRow>(limit);
        if (total > 0 && offset < total)
        {
            using var pageCmd = conn.CreateCommand();
            pageCmd.CommandText = $"""
                SELECT id, sequence, actor_name, entry, at_seconds, level, message, exception, call_index, actor_outcome, is_failed, is_slow
                FROM logs {where}
                ORDER BY id
                LIMIT $limit OFFSET $offset;
                """;
            foreach (var (n, v) in parameters) pageCmd.Parameters.AddWithValue(n, v);
            pageCmd.Parameters.AddWithValue("$limit", limit);
            pageCmd.Parameters.AddWithValue("$offset", offset);

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
                    reader.GetInt32(10) != 0,
                    reader.GetInt32(11) != 0));
            }
        }

        return Task.FromResult(new LogPageResult(total, offset, limit, items));
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        try { await writerTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await shutdown.CancelAsync();
        shutdown.Dispose();
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

    private static SqliteCommand PrepareStartRun(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO runs(run_id, started_utc, state, elapsed_seconds, plan_json) VALUES ($id, $started, 'Running', 0, $plan);";
        cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.Add("$started", SqliteType.Text);
        cmd.Parameters.Add("$plan", SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    private static SqliteCommand PrepareFinishRun(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE runs SET finished_utc = $finished, state = $state, elapsed_seconds = $elapsed, error = $error WHERE run_id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.Add("$finished", SqliteType.Text);
        cmd.Parameters.Add("$state", SqliteType.Text);
        cmd.Parameters.Add("$elapsed", SqliteType.Real);
        cmd.Parameters.Add("$error", SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    private static SqliteCommand PrepareInsertActor(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO actors(run_id, sequence, actor_name, entry, started_at, duration_ms, status_code, bytes, error, outcome, is_failed, is_slow, slow_calls, call_count, log_count)
            VALUES ($runId, $seq, $actorName, $entry, $startedAt, $dur, $status, $bytes, $err, $outcome, $isFailed, $isSlow, $slowCalls, $callCount, $logCount);
            """;
        cmd.Parameters.Add("$runId", SqliteType.Text);
        cmd.Parameters.Add("$seq", SqliteType.Integer);
        cmd.Parameters.Add("$actorName", SqliteType.Text);
        cmd.Parameters.Add("$entry", SqliteType.Integer);
        cmd.Parameters.Add("$startedAt", SqliteType.Real);
        cmd.Parameters.Add("$dur", SqliteType.Real);
        cmd.Parameters.Add("$status", SqliteType.Integer);
        cmd.Parameters.Add("$bytes", SqliteType.Integer);
        cmd.Parameters.Add("$err", SqliteType.Text);
        cmd.Parameters.Add("$outcome", SqliteType.Integer);
        cmd.Parameters.Add("$isFailed", SqliteType.Integer);
        cmd.Parameters.Add("$isSlow", SqliteType.Integer);
        cmd.Parameters.Add("$slowCalls", SqliteType.Integer);
        cmd.Parameters.Add("$callCount", SqliteType.Integer);
        cmd.Parameters.Add("$logCount", SqliteType.Integer);
        cmd.Prepare();
        return cmd;
    }

    private static SqliteCommand PrepareInsertCall(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO calls(run_id, sequence, call_index, name, offset_ms, duration_ms, status_code, bytes, error)
            VALUES ($runId, $seq, $idx, $name, $offset, $dur, $status, $bytes, $err);
            """;
        cmd.Parameters.Add("$runId", SqliteType.Text);
        cmd.Parameters.Add("$seq", SqliteType.Integer);
        cmd.Parameters.Add("$idx", SqliteType.Integer);
        cmd.Parameters.Add("$name", SqliteType.Text);
        cmd.Parameters.Add("$offset", SqliteType.Real);
        cmd.Parameters.Add("$dur", SqliteType.Real);
        cmd.Parameters.Add("$status", SqliteType.Integer);
        cmd.Parameters.Add("$bytes", SqliteType.Integer);
        cmd.Parameters.Add("$err", SqliteType.Text);
        cmd.Prepare();
        return cmd;
    }

    private static SqliteCommand PrepareInsertLog(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO logs(run_id, sequence, actor_name, entry, at_seconds, level_rank, level, message, exception, call_index, actor_outcome, is_failed, is_slow)
            VALUES ($runId, $seq, $actorName, $entry, $at, $rank, $level, $msg, $ex, $callIdx, $outcome, $isFailed, $isSlow);
            """;
        cmd.Parameters.Add("$runId", SqliteType.Text);
        cmd.Parameters.Add("$seq", SqliteType.Integer);
        cmd.Parameters.Add("$actorName", SqliteType.Text);
        cmd.Parameters.Add("$entry", SqliteType.Integer);
        cmd.Parameters.Add("$at", SqliteType.Real);
        cmd.Parameters.Add("$rank", SqliteType.Integer);
        cmd.Parameters.Add("$level", SqliteType.Text);
        cmd.Parameters.Add("$msg", SqliteType.Text);
        cmd.Parameters.Add("$ex", SqliteType.Text);
        cmd.Parameters.Add("$callIdx", SqliteType.Integer);
        cmd.Parameters.Add("$outcome", SqliteType.Integer);
        cmd.Parameters.Add("$isFailed", SqliteType.Integer);
        cmd.Parameters.Add("$isSlow", SqliteType.Integer);
        cmd.Prepare();
        return cmd;
    }
}
