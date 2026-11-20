using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace PerformanceTool.Services.Scenarios;

/// <summary>
/// A load-test scenario defined in code. One execution of a scenario = one bar in the live chart.
/// Implement this interface and register it in <c>Program.cs</c> (<c>AddScenario&lt;T&gt;()</c>) to make it pickable in the UI.
/// See docs/brain/0003-code-defined-scenarios.md.
/// </summary>
public interface IScenario
{
    /// <summary>Stable identifier. Stored in presets, so never rename it once released.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }
    IReadOnlyList<ScenarioParameter> Parameters { get; }

    /// <summary>Runs the scenario once. The runner measures the wall-clock time of this call.</summary>
    Task<ScenarioOutcome> ExecuteAsync(ScenarioContext context, CancellationToken cancellationToken);
}

public enum ParameterType { Text, Url, Integer, Multiline, Choice }

public sealed record ScenarioParameter(
    string Key,
    string Label,
    ParameterType Type,
    string DefaultValue,
    string? Help = null,
    IReadOnlyList<string>? Options = null,
    bool Required = true)
{
    /// <summary>Returns an error message, or null if <paramref name="value"/> is valid.</summary>
    public string? Validate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Required ? $"{Label} is required." : null;

        return Type switch
        {
            ParameterType.Url when !ScenarioContext.IsHttpUrl(value) => $"{Label} must be an absolute http(s) URL.",
            ParameterType.Integer when !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) => $"{Label} must be a whole number.",
            ParameterType.Choice when Options is not null && !Options.Contains(value) => $"{Label} must be one of: {string.Join(", ", Options)}.",
            _ => null,
        };
    }
}

/// <summary>Result of one scenario execution. Timing is measured by the runner, not the scenario.</summary>
public sealed record ScenarioOutcome(int? StatusCode, long Bytes, string? Error = null)
{
    public bool Success => Error is null && StatusCode is >= 200 and < 400;
    public static ScenarioOutcome Failed(string error, int? statusCode = null) => new(statusCode, 0, error);
}

/// <summary>Everything a scenario needs for one execution.</summary>
public sealed class ScenarioContext(HttpClient http, IReadOnlyDictionary<string, string> parameters)
{
    public HttpClient Http { get; } = http;
    public IReadOnlyDictionary<string, string> Parameters { get; } = parameters;

    public string Get(string key) => Parameters.TryGetValue(key, out var v) ? v : "";
    public int GetInt(string key) => int.Parse(Get(key), CultureInfo.InvariantCulture);

    /// <summary>Sends a request, reads the whole body (so transfer time is included) and maps it to an outcome.</summary>
    public async Task<ScenarioOutcome> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            var body = await response.Content.ReadAsByteArrayAsync(ct);
            return new ScenarioOutcome((int)response.StatusCode, body.LongLength);
        }
    }

    public static bool IsHttpUrl([NotNullWhen(true)] string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>Splits a multiline parameter into trimmed, non-empty lines.</summary>
    public static IEnumerable<string> Lines(string value) =>
        value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
}
