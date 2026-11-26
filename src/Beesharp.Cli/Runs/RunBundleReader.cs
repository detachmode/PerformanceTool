using System.IO.Compression;
using DuckDB.NET.Data;

namespace Beesharp.Cli.Runs;

[Flags]
internal enum BundleTables
{
    None = 0,
    Runs = 1 << 0,
    RunSteps = 1 << 1,
    Bees = 1 << 2,
    Calls = 1 << 3,
    Logs = 1 << 4,
    Traces = 1 << 5,
}

internal interface IRunBundle : IDisposable
{
    RunSummaryReport GetSummary();
    RunErrorsReport GetErrors();
    RunCallsReport GetCalls(RunsCallsOptions options);
    RunSlowestCallsReport GetSlowestCalls(RunsCallsOptions options);
    RunTimelineReport GetTimeline(RunsTimelineOptions options);
    RunLogsReport QueryLogs(RunsLogsOptions options);
    RunBeeReport GetBee(int beeNumber);
    RunTraceReport GetTrace(RunsTraceOptions options);
    HotspotsResultDto GetHotspots(RunsHotspotsOptions options);
}

internal static class RunBundleReader
{
    public static IRunBundle Open(string filePath, BundleTables neededTables)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new CouldNotRunException("Run bundle path is required.");

        var fullPath = Path.GetFullPath(filePath);
        if (Directory.Exists(fullPath))
            throw new CouldNotRunException($"Run bundle '{filePath}' is a directory; expected a .zip, .duckdb, or .json file.");
        if (!File.Exists(fullPath))
            throw new CouldNotRunException($"Run bundle '{filePath}' was not found.");

        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        return ext switch
        {
            ".zip" => ParquetZipRunBundle.Open(fullPath, neededTables),
            ".duckdb" => DuckDbFileRunBundle.Open(fullPath),
            ".json" => JsonRunBundle.Open(fullPath),
            _ => throw new CouldNotRunException(
                $"Unsupported run bundle format '{ext}' for '{filePath}'. Expected a .zip (Parquet bundle), .duckdb, or .json export."),
        };
    }
}

/// <summary>
/// Opens a Parquet <c>.zip</c> run bundle lazily: extracts only the <c>.parquet</c> files required by the command
/// into a temporary directory and exposes them via DuckDB <c>CREATE VIEW ... AS SELECT * FROM read_parquet(...)</c>
/// (column projection, predicate pushdown, streaming row-groups, bounded memory).
/// </summary>
internal sealed class ParquetZipRunBundle : IRunBundle
{
    private readonly string tempDir;
    private readonly DuckDBConnection conn;
    private readonly DuckDbBundleQueries queries;
    private bool disposed;

    internal IReadOnlyList<string> ExtractedTables { get; }

    private ParquetZipRunBundle(string tempDir, DuckDBConnection conn, HashSet<string> availableTables, IReadOnlyList<string> extractedTables)
    {
        this.tempDir = tempDir;
        this.conn = conn;
        ExtractedTables = extractedTables;
        queries = new DuckDbBundleQueries(conn, availableTables);
    }

    public static ParquetZipRunBundle Open(string zipPath, BundleTables neededTables)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"beesharp-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        DuckDBConnection? conn = null;

        try
        {
            var requested = new List<(string Table, string FileName)>
            {
                ("runs", "runs.parquet"),
            };
            if (neededTables.HasFlag(BundleTables.RunSteps)) requested.Add(("run_steps", "run_steps.parquet"));
            if (neededTables.HasFlag(BundleTables.Bees)) requested.Add(("bees", "bees.parquet"));
            if (neededTables.HasFlag(BundleTables.Calls)) requested.Add(("calls", "calls.parquet"));
            if (neededTables.HasFlag(BundleTables.Logs)) requested.Add(("logs", "logs.parquet"));
            if (neededTables.HasFlag(BundleTables.Traces))
            {
                requested.Add(("trace_fetches", "trace_fetches.parquet"));
                requested.Add(("trace_spans", "trace_spans.parquet"));
            }

            var availableTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var extractedPaths = new List<(string Table, string FilePath)>();
            var extractedNames = new List<string>();

            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var (table, fileName) in requested)
                {
                    var entry = FindZipEntry(zip, fileName);
                    if (entry is null)
                    {
                        if (table == "runs")
                            throw new CouldNotRunException($"Invalid Parquet bundle '{zipPath}': runs.parquet is missing.");
                        continue;
                    }

                    var destPath = Path.GetFullPath(Path.Combine(tempDir, fileName));
                    if (!destPath.StartsWith(Path.GetFullPath(tempDir) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new CouldNotRunException($"Unsafe entry path in bundle '{zipPath}'.");

                    entry.ExtractToFile(destPath, overwrite: true);
                    availableTables.Add(table);
                    extractedPaths.Add((table, destPath));
                    extractedNames.Add(fileName);
                }
            }

            conn = new DuckDBConnection("Data Source=:memory:");
            conn.Open();

            using (var setup = conn.CreateCommand())
            {
                var spillDir = Path.Combine(tempDir, "spill");
                Directory.CreateDirectory(spillDir);
                setup.CommandText = $"SET temp_directory = '{EscapeSql(spillDir)}';";
                setup.ExecuteNonQuery();
            }

            foreach (var (table, path) in extractedPaths)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"CREATE VIEW {table} AS SELECT * FROM read_parquet('{EscapeSql(path)}');";
                cmd.ExecuteNonQuery();
            }

            return new ParquetZipRunBundle(tempDir, conn, availableTables, extractedNames);
        }
        catch (CouldNotRunException)
        {
            conn?.Dispose();
            TryDeleteDirectory(tempDir);
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or DuckDBException)
        {
            conn?.Dispose();
            TryDeleteDirectory(tempDir);
            throw new CouldNotRunException($"Could not read Parquet bundle '{zipPath}': {ex.Message}");
        }
    }

    private static ZipArchiveEntry? FindZipEntry(ZipArchive zip, string fileName) =>
        zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, fileName, StringComparison.OrdinalIgnoreCase))
        ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, fileName, StringComparison.OrdinalIgnoreCase));

    public RunSummaryReport GetSummary() => queries.GetSummary();
    public RunErrorsReport GetErrors() => queries.GetErrors();
    public RunCallsReport GetCalls(RunsCallsOptions options) => queries.GetCalls(options);
    public RunSlowestCallsReport GetSlowestCalls(RunsCallsOptions options) => queries.GetSlowestCalls(options);
    public RunTimelineReport GetTimeline(RunsTimelineOptions options) => queries.GetTimeline(options);
    public RunLogsReport QueryLogs(RunsLogsOptions options) => queries.QueryLogs(options);
    public RunBeeReport GetBee(int beeNumber) => queries.GetBee(beeNumber);
    public RunTraceReport GetTrace(RunsTraceOptions options) => queries.GetTrace(options);
    public HotspotsResultDto GetHotspots(RunsHotspotsOptions options) => queries.GetHotspots(options);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        conn.Dispose();
        TryDeleteDirectory(tempDir);
    }

    private static string EscapeSql(string value) => value.Replace("'", "''");

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Opens an exported standalone <c>.duckdb</c> file directly with <c>ACCESS_MODE=READ_ONLY</c> (no copy).</summary>
internal sealed class DuckDbFileRunBundle : IRunBundle
{
    private readonly DuckDBConnection conn;
    private readonly DuckDbBundleQueries queries;
    private bool disposed;

    private DuckDbFileRunBundle(DuckDBConnection conn, HashSet<string> availableTables)
    {
        this.conn = conn;
        queries = new DuckDbBundleQueries(conn, availableTables);
    }

    public static DuckDbFileRunBundle Open(string dbPath)
    {
        DuckDBConnection? conn = null;
        try
        {
            var builder = new DuckDBConnectionStringBuilder
            {
                DataSource = dbPath,
            };
            var connStr = $"{builder.ConnectionString};ACCESS_MODE=READ_ONLY";
            conn = new DuckDBConnection(connStr);
            conn.Open();

            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SHOW TABLES;";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    tables.Add(reader.GetString(0));
            }

            if (!tables.Contains("runs"))
                throw new CouldNotRunException($"DuckDB file '{dbPath}' does not contain a 'runs' table.");

            return new DuckDbFileRunBundle(conn, tables);
        }
        catch (CouldNotRunException)
        {
            conn?.Dispose();
            throw;
        }
        catch (Exception ex) when (ex is DuckDBException or IOException or InvalidOperationException)
        {
            conn?.Dispose();
            throw new CouldNotRunException($"Could not open DuckDB file '{dbPath}' in read-only mode: {ex.Message}");
        }
    }

    public RunSummaryReport GetSummary() => queries.GetSummary();
    public RunErrorsReport GetErrors() => queries.GetErrors();
    public RunCallsReport GetCalls(RunsCallsOptions options) => queries.GetCalls(options);
    public RunSlowestCallsReport GetSlowestCalls(RunsCallsOptions options) => queries.GetSlowestCalls(options);
    public RunTimelineReport GetTimeline(RunsTimelineOptions options) => queries.GetTimeline(options);
    public RunLogsReport QueryLogs(RunsLogsOptions options) => queries.QueryLogs(options);
    public RunBeeReport GetBee(int beeNumber) => queries.GetBee(beeNumber);
    public RunTraceReport GetTrace(RunsTraceOptions options) => queries.GetTrace(options);
    public HotspotsResultDto GetHotspots(RunsHotspotsOptions options) => queries.GetHotspots(options);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        conn.Dispose();
    }
}
