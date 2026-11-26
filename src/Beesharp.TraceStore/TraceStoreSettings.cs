using System.Text.RegularExpressions;

namespace Beesharp.Services.TraceStore;

/// <summary>
/// Where the services' traces are kept (brain 0065), read from the run's <b>Environment</b> (brain 0033) so a dev cluster and production differ only there:
/// <c>TraceStore.Kind</c> (<c>jaeger</c>; the default when only a URL is given), <c>TraceStore.Url</c> (e.g. <c>http://localhost:16686</c>),
/// <c>TraceStore.Token</c> (optional, sent as a Bearer token; <c>${env:NAME}</c> reads an operating-system variable), and the optional tuning keys
/// <c>TraceStore.MaxTraces</c> (cap per run) and <c>TraceStore.SettleSeconds</c> (how long a bee must have ended before its trace is asked for).
/// </summary>
/// <param name="Kind">The kind of trace store (<see cref="TraceStoreKinds"/>).</param>
/// <param name="Url">Base URL of the trace store's query API.</param>
/// <param name="Token">Optional Bearer token, already resolved.</param>
/// <param name="MaxTraces">Optional cap on the traces fetched for a run.</param>
/// <param name="SettleSeconds">Optional seconds a bee must have ended before its trace is asked for.</param>
public sealed partial record TraceStoreSettings(string Kind, string Url, string? Token = null, int? MaxTraces = null, double? SettleSeconds = null)
{
    /// <summary>The environment key of the kind.</summary>
    public const string KindKey = "TraceStore.Kind";

    /// <summary>The environment key of the base URL.</summary>
    public const string UrlKey = "TraceStore.Url";

    /// <summary>The environment key of the token.</summary>
    public const string TokenKey = "TraceStore.Token";

    /// <summary>The environment key of the per-run cap.</summary>
    public const string MaxTracesKey = "TraceStore.MaxTraces";

    /// <summary>The environment key of the settle time in seconds.</summary>
    public const string SettleSecondsKey = "TraceStore.SettleSeconds";

    [GeneratedRegex(@"^\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}$")]
    private static partial Regex EnvReference();

    /// <summary>Reads the settings from an environment's values.</summary>
    /// <param name="values">The environment's key-value pairs (<see cref="Environments.EnvironmentValues"/>, or any dictionary).</param>
    /// <param name="getEnvironmentVariable">Reads an operating-system variable for <c>${env:NAME}</c> (default: the process environment).</param>
    /// <returns>Not configured (no <c>TraceStore.*</c> keys), configured, or a problem to report (bad URL, unknown kind, unset variable).</returns>
    public static TraceStoreResolution FromEnvironment(IReadOnlyDictionary<string, string> values, Func<string, string?>? getEnvironmentVariable = null)
    {
        string? Get(string key) => values.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;
        var kind = Get(KindKey);
        var url = Get(UrlKey);
        if (kind is null && url is null) return TraceStoreResolution.NotConfigured;
        kind = (kind ?? TraceStoreKinds.Jaeger).ToLowerInvariant();
        if (!TraceStoreKinds.Supported.Contains(kind))
            return TraceStoreResolution.Invalid($"{KindKey} '{kind}' is not supported; supported: {string.Join(", ", TraceStoreKinds.Supported)}.");
        if (url is null) return TraceStoreResolution.Invalid($"{UrlKey} is missing (e.g. http://localhost:16686 for Jaeger).");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return TraceStoreResolution.Invalid($"{UrlKey} '{url}' is not an http:// or https:// URL.");

        var token = Get(TokenKey);
        if (token is not null && EnvReference().Match(token) is { Success: true } m)
        {
            var name = m.Groups[1].Value;
            var resolved = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(name);
            if (string.IsNullOrEmpty(resolved)) return TraceStoreResolution.Invalid($"{TokenKey} refers to ${{env:{name}}}, but that environment variable is not set.");
            token = resolved;
        }

        int? maxTraces = null;
        if (Get(MaxTracesKey) is { } rawMax)
        {
            if (!int.TryParse(rawMax, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var max) || max < 1)
                return TraceStoreResolution.Invalid($"{MaxTracesKey} '{rawMax}' is not a whole number of 1 or more.");
            maxTraces = max;
        }
        double? settle = null;
        if (Get(SettleSecondsKey) is { } rawSettle)
        {
            if (!double.TryParse(rawSettle, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) || s < 0 || double.IsNaN(s))
                return TraceStoreResolution.Invalid($"{SettleSecondsKey} '{rawSettle}' is not a number of seconds (0 or more).");
            settle = s;
        }

        return new TraceStoreResolution(new TraceStoreSettings(kind, uri.ToString().TrimEnd('/'), token, maxTraces, settle), null);
    }
}

/// <summary>The kinds of trace store Beesharp can fetch from.</summary>
public static class TraceStoreKinds
{
    /// <summary>Jaeger v2 (<c>GET /api/v3/traces/{traceId}</c>).</summary>
    public const string Jaeger = "jaeger";

    /// <summary>The supported kinds (Elastic is planned behind the same <see cref="ITraceStore"/>).</summary>
    public static readonly IReadOnlyList<string> Supported = [Jaeger];
}

/// <summary>The outcome of reading the trace store settings of an environment.</summary>
/// <param name="Settings">The settings when a trace store is configured and valid.</param>
/// <param name="Problem">What is wrong when the keys are present but invalid.</param>
public sealed record TraceStoreResolution(TraceStoreSettings? Settings, string? Problem)
{
    /// <summary>No <c>TraceStore.*</c> key is set: nothing is fetched.</summary>
    public static TraceStoreResolution NotConfigured { get; } = new(null, null);

    /// <summary>Keys are present but invalid.</summary>
    /// <param name="problem">What is wrong.</param>
    /// <returns>A resolution carrying the problem.</returns>
    public static TraceStoreResolution Invalid(string problem) => new(null, problem);

    /// <summary>Whether a usable trace store is configured.</summary>
    public bool IsConfigured => Settings is not null;
}
