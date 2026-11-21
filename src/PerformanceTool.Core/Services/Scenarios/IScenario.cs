using System.Diagnostics.CodeAnalysis;
using PerformanceTool.Services.Environments;
using PerformanceTool.Services.Scenarios.Spec;

namespace PerformanceTool.Services.Scenarios;

/// <summary>
/// A load-test scenario defined in code. One execution = one bar in the live chart. Derive from
/// <see cref="Scenario{TParameters}"/> rather than implementing this directly, and register it in <c>Program.cs</c>
/// with <c>AddScenario&lt;T&gt;()</c>. See docs/brain/0003 and 0007.
/// </summary>
public interface IScenario
{
    /// <summary>Stable identifier. Stored in presets, so never rename it once released.</summary>
    string Id { get; }
    string Name { get; }
    string Description { get; }

    /// <summary>Editor, validation and schema model generated from the parameter type.</summary>
    ParameterSpec Spec { get; }

    /// <summary>Runs once. <paramref name="parameters"/> is an instance of <see cref="ParameterSpec.Type"/>.</summary>
    Task<ScenarioOutcome> ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Base class for scenarios with typed parameters. <typeparamref name="TParameters"/> is a plain class whose public
/// properties become editor fields (see docs/brain/0007): property initialisers are the defaults, and
/// DataAnnotations (<c>[Display]</c>, <c>[Required]</c>, <c>[Range]</c>, <c>[MinLength]</c>) plus
/// <see cref="HttpUrlAttribute"/>, <see cref="MultilineAttribute"/> and <see cref="SecretAttribute"/> shape the
/// editor and validation. The same instance is shared by concurrent executions: treat it as read-only.
/// </summary>
public abstract class Scenario<TParameters> : IScenario where TParameters : class, new()
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }

    public ParameterSpec Spec { get; } = ParameterSpec.For(typeof(TParameters));

    Task<ScenarioOutcome> IScenario.ExecuteAsync(object parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        ExecuteAsync((TParameters)parameters, context, cancellationToken);

    protected abstract Task<ScenarioOutcome> ExecuteAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken);
}

/// <summary>Result of one execution. Timing is measured by the runner unless <see cref="Measured"/> is set.</summary>
public sealed record ScenarioOutcome(int? StatusCode, long Bytes, string? Error = null)
{
    /// <summary>When set, success requires exactly this status code instead of any 2xx/3xx.</summary>
    public int? ExpectedStatus { get; init; }

    /// <summary>Overrides the runner's wall-clock time, e.g. to exclude think time between steps.</summary>
    public TimeSpan? Measured { get; init; }

    public bool Success => Error is null && (ExpectedStatus is { } expected ? StatusCode == expected : StatusCode is >= 200 and < 400);

    public string? FailureReason => Success ? null
        : Error ?? (ExpectedStatus is { } expected ? $"Expected HTTP {expected}, got {StatusCode}" : $"HTTP {StatusCode}");

    public ScenarioOutcome Expect(int? status) => this with { ExpectedStatus = status };

    public static ScenarioOutcome Failed(string error, int? statusCode = null) => new(statusCode, 0, error);
}

/// <summary>Shared services for one execution.</summary>
public sealed class ScenarioContext(HttpClient http, EnvironmentValues? environment = null)
{
    /// <summary>
    /// The active environment's values (brain 0033), e.g. <c>context.Environment["BaseUrl"]</c>. Placeholders in parameter values are already
    /// filled in when the run starts; use this when code needs a value that is not a parameter. Empty when no environment is active.
    /// </summary>
    public EnvironmentValues Environment { get; } = environment ?? EnvironmentValues.None;

    /// <summary>Shared by the whole run. Don't dispose it or change its default headers.</summary>
    public HttpClient Http { get; } = http;

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
}
