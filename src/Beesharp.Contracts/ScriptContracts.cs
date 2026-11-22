using System.Text.Json.Nodes;

namespace Beesharp.Contracts;

// /api/scenarios, /api/scripts, /api/preflights and /api/shutdowns (brain 0040, 0042, 0052, 0054).

/// <summary>One scenario of <c>GET /api/scenarios</c>: the catalog entry with the JSON Schema of its parameters.</summary>
public sealed record ScenarioInfoDto(
    string Id,
    string Name,
    string Description,
    bool IsCustom,
    string? ScriptId,
    JsonObject Parameters);

public sealed record ScriptDiagnosticDto(string Id, string Severity, int Line, int Column, string Message);

public sealed record ScriptScenarioSummary(
    string ScriptId,
    string FileName,
    string ScenarioId,
    string ScenarioName,
    string Description,
    string Code,
    DateTimeOffset UpdatedUtc,
    JsonObject Parameters);

public sealed record ScriptCompileResult(
    bool Success,
    ScriptScenarioSummary? Script,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    string? Error = null);

public sealed record ScriptRecordedCall(string Method, string Url, int? StatusCode, double DurationMs, long Bytes, string? Error = null);

public sealed record ScriptTestRunResult(
    bool Success,
    string ScenarioId,
    string ScenarioName,
    int? StatusCode,
    long Bytes,
    double DurationMs,
    double StartupDurationMs,
    double TeardownDurationMs,
    string? Error,
    IReadOnlyList<ScriptRecordedCall> Calls,
    IReadOnlyList<string> Logs);

public sealed record PreflightScriptSummary(
    string ScriptId,
    string FileName,
    string Name,
    string Code,
    DateTimeOffset UpdatedUtc);

public sealed record PreflightCompileResult(
    bool Success,
    PreflightScriptSummary? Script,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    string? Error = null);

public sealed record PreflightRunResult(
    bool Success,
    double DurationMs,
    string? Error,
    IReadOnlyList<string> Logs);

public sealed record UploadScriptRequest(string? Id, string? FileName, string? Code);

public sealed record TestRunScriptRequest(JsonObject? Parameters = null, string? EnvironmentName = null, int TimeoutSeconds = 30);

public sealed record TestRunPreflightRequest(string? EnvironmentName = null, int TimeoutSeconds = 60);
