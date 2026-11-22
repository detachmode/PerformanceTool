namespace Beesharp.Services.Storage;

/// <summary>Which run storage to use and where it keeps its file (brain 0046, 0054). The host binds it from <c>Storage:Provider</c> and <c>Storage:DuckDbPath</c>.</summary>
public sealed class StorageOptions
{
    public const string DuckDb = "DuckDb";
    public const string Memory = "Memory";
    /// <summary>The providers that exist, for messages.</summary>
    public static readonly string[] SupportedProviders = [DuckDb, Memory];

    /// <summary><c>DuckDb</c> (default) or <c>Memory</c> (tests, quick tries; nothing is kept).</summary>
    public string Provider { get; set; } = DuckDb;

    /// <summary>The DuckDB file. Relative paths start at <see cref="Root"/>. Default: <c>data/beesharp.duckdb</c>.</summary>
    public string? DuckDbPath { get; set; }

    /// <summary>The folder relative paths start from (the host's content root). Default: the current directory.</summary>
    public string Root { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>The normalized provider name; throws <see cref="NotSupportedException"/> naming the supported providers for anything else (SQLite was dropped, brain 0054).</summary>
    public string ResolveProvider()
    {
        var provider = string.IsNullOrWhiteSpace(Provider) ? DuckDb : Provider.Trim();
        foreach (var known in SupportedProviders)
            if (string.Equals(provider, known, StringComparison.OrdinalIgnoreCase)) return known;
        throw new NotSupportedException(
            $"Storage provider '{provider}' is not supported. Supported providers: {string.Join(", ", SupportedProviders)}. " +
            "SQLite storage was removed: set \"Storage:Provider\" to DuckDb or Memory. Runs kept in an old SQLite file are not read anymore.");
    }

    public string ResolveDuckDbPath() =>
        string.IsNullOrWhiteSpace(DuckDbPath)
            ? Path.Combine(Root, "data", "beesharp.duckdb")
            : Path.IsPathRooted(DuckDbPath) ? DuckDbPath : Path.Combine(Root, DuckDbPath);
}
