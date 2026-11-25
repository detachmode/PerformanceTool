using System.Text.Json.Nodes;
using Beesharp.Services;
using Beesharp.Services.Scenarios;

namespace Beesharp.Contracts;

// /api/scenarios, /api/scripts, /api/preflights and /api/shutdowns (brain 0040, 0042, 0052, 0054).

/// <summary>One scenario of <c>GET /api/scenarios</c>: the catalog entry with the JSON Schema of its parameters.</summary>
/// <param name="Id">Unique scenario identifier.</param>
/// <param name="Name">Human-readable scenario display name.</param>
/// <param name="Description">Short summary describing what the scenario does.</param>
/// <param name="IsCustom"><see langword="true"/> when the scenario comes from a user C# script rather than a built-in type.</param>
/// <param name="ScriptId">Identifier of the backing script when <see cref="IsCustom"/> is <see langword="true"/>, otherwise <see langword="null"/>.</param>
/// <param name="Parameters">JSON Schema object describing the scenario's configurable parameters.</param>
/// <param name="Steps">Optional ordered list of declared steps for step-machine scenarios.</param>
public sealed record ScenarioInfoDto(
    string Id,
    string Name,
    string Description,
    bool IsCustom,
    string? ScriptId,
    JsonObject Parameters,
    IReadOnlyList<StepInfo>? Steps = null);

/// <summary>Compiler diagnostic produced while compiling a scenario, preflight, or shutdown script.</summary>
/// <param name="Id">Diagnostic code (for example <c>CS1002</c>).</param>
/// <param name="Severity">Diagnostic severity such as <c>Error</c> or <c>Warning</c>.</param>
/// <param name="Line">1-based source line number where the diagnostic occurred.</param>
/// <param name="Column">1-based source column number where the diagnostic occurred.</param>
/// <param name="Message">Formatted compiler diagnostic message.</param>
public sealed record ScriptDiagnosticDto(string Id, string Severity, int Line, int Column, string Message);

/// <summary>Compiled metadata and source code for a custom scenario script.</summary>
/// <param name="ScriptId">Unique script identifier.</param>
/// <param name="FileName">Script file name on disk.</param>
/// <param name="ScenarioId">Scenario identifier exposed by the compiled script.</param>
/// <param name="ScenarioName">Human-readable scenario name exposed by the compiled script.</param>
/// <param name="Description">Scenario description exposed by the compiled script.</param>
/// <param name="Code">Full C# source code of the script.</param>
/// <param name="UpdatedUtc">UTC timestamp when the script was last saved or compiled.</param>
/// <param name="Parameters">JSON Schema object describing the script scenario's parameters.</param>
/// <param name="Steps">Optional ordered list of declared steps for step-machine scenarios.</param>
public sealed record ScriptScenarioSummary(
    string ScriptId,
    string FileName,
    string ScenarioId,
    string ScenarioName,
    string Description,
    string Code,
    DateTimeOffset UpdatedUtc,
    JsonObject Parameters,
    IReadOnlyList<StepInfo>? Steps = null);

/// <summary>Result of compiling and registering a custom scenario script.</summary>
/// <param name="Success"><see langword="true"/> when compilation succeeded without errors.</param>
/// <param name="Script">Compiled script summary when <see cref="Success"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
/// <param name="Diagnostics">Compiler diagnostics emitted during compilation.</param>
/// <param name="Error">Optional high-level error message if compilation or loading failed.</param>
public sealed record ScriptCompileResult(
    bool Success,
    ScriptScenarioSummary? Script,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    string? Error = null);

/// <summary>One call recorded during a single-bee script test run.</summary>
/// <param name="Method">Call or HTTP method label.</param>
/// <param name="Url">Target URL or call target string.</param>
/// <param name="StatusCode">Optional HTTP or custom status code returned by the call.</param>
/// <param name="DurationMs">Call duration in milliseconds.</param>
/// <param name="Bytes">Number of payload bytes transferred.</param>
/// <param name="Error">Optional error message if the call failed.</param>
/// <param name="Step">Zero-based step index during which the call was recorded.</param>
/// <param name="Rep">Zero-based repetition index within the step.</param>
/// <param name="OffsetMs">Start offset in milliseconds relative to the bee's execution start.</param>
public sealed record ScriptRecordedCall(string Method, string Url, int? StatusCode, double DurationMs, long Bytes, string? Error = null, int Step = 0, int Rep = 0, double OffsetMs = 0);

/// <summary>Completed result of a synchronous single-bee script test run.</summary>
/// <param name="Success"><see langword="true"/> when the test bee completed without error.</param>
/// <param name="ScenarioId">Identifier of the tested scenario.</param>
/// <param name="ScenarioName">Display name of the tested scenario.</param>
/// <param name="StatusCode">Optional HTTP or scenario status code summarizing the run.</param>
/// <param name="Bytes">Total bytes transferred during the test run.</param>
/// <param name="DurationMs">Total execution duration in milliseconds.</param>
/// <param name="StartupDurationMs">Time spent in scenario startup/setup in milliseconds.</param>
/// <param name="TeardownDurationMs">Time spent in scenario teardown/cleanup in milliseconds.</param>
/// <param name="Error">Optional error message if the test run failed.</param>
/// <param name="Calls">Calls recorded during the test execution.</param>
/// <param name="Logs">Formatted log lines emitted during the test execution.</param>
/// <param name="Steps">Optional declared steps metadata for step-machine scenarios.</param>
/// <param name="FailedStep">Zero-based index of the step that failed, or <c>-1</c> if none.</param>
/// <param name="FailedRep">Zero-based repetition index of the failed step, or <c>-1</c> if none.</param>
/// <param name="StepVisits">Optional list of declared step visits recorded during the test execution.</param>
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
    IReadOnlyList<string> Logs,
    IReadOnlyList<StepInfo>? Steps = null,
    int FailedStep = -1,
    int FailedRep = -1,
    IReadOnlyList<RecordedStepVisit>? StepVisits = null);

/// <summary>Live snapshot of a single-bee script test run polled by the Script Editor Try popup.</summary>
/// <param name="TestRunId">Unique identifier of the active or completed test run.</param>
/// <param name="Running"><see langword="true"/> while the test bee is still executing.</param>
/// <param name="Success"><see langword="true"/> when the test run finished without error.</param>
/// <param name="ScenarioId">Identifier of the tested scenario.</param>
/// <param name="ScenarioName">Display name of the tested scenario.</param>
/// <param name="StatusCode">Optional HTTP or scenario status code summarizing the run.</param>
/// <param name="Bytes">Total bytes transferred so far.</param>
/// <param name="DurationMs">Elapsed or total duration in milliseconds.</param>
/// <param name="StartupDurationMs">Time spent in scenario startup/setup in milliseconds.</param>
/// <param name="TeardownDurationMs">Time spent in scenario teardown/cleanup in milliseconds.</param>
/// <param name="Error">Optional error message if the test run failed.</param>
/// <param name="Calls">Calls recorded so far during the test execution.</param>
/// <param name="Logs">Structured log entries emitted so far during the test execution.</param>
/// <param name="Steps">Optional declared steps metadata for step-machine scenarios.</param>
/// <param name="CurrentStep">Zero-based index of the currently executing step, or <c>-1</c> if none.</param>
/// <param name="CurrentRep">Zero-based repetition index of the currently executing step.</param>
/// <param name="FailedStep">Zero-based index of the step that failed, or <c>-1</c> if none.</param>
/// <param name="FailedRep">Zero-based repetition index of the failed step, or <c>-1</c> if none.</param>
/// <param name="StepVisits">Optional list of declared step visits recorded so far.</param>
public sealed record LiveTestRunStatusDto(
    string TestRunId,
    bool Running,
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
    IReadOnlyList<LiveLogEntryDto> Logs,
    IReadOnlyList<StepInfo>? Steps = null,
    int CurrentStep = -1,
    int CurrentRep = 0,
    int FailedStep = -1,
    int FailedRep = -1,
    IReadOnlyList<RecordedStepVisit>? StepVisits = null);

/// <summary>Metadata and source code for a preflight or shutdown program script.</summary>
/// <param name="ScriptId">Unique program script identifier.</param>
/// <param name="FileName">Script file name on disk.</param>
/// <param name="Name">Human-readable program name.</param>
/// <param name="Code">Full C# source code of the program script.</param>
/// <param name="UpdatedUtc">UTC timestamp when the script was last saved or compiled.</param>
public sealed record PreflightScriptSummary(
    string ScriptId,
    string FileName,
    string Name,
    string Code,
    DateTimeOffset UpdatedUtc);

/// <summary>Result of compiling and registering a preflight or shutdown program script.</summary>
/// <param name="Success"><see langword="true"/> when compilation succeeded without errors.</param>
/// <param name="Script">Compiled program summary when <see cref="Success"/> is <see langword="true"/>; otherwise <see langword="null"/>.</param>
/// <param name="Diagnostics">Compiler diagnostics emitted during compilation.</param>
/// <param name="Error">Optional high-level error message if compilation or loading failed.</param>
public sealed record PreflightCompileResult(
    bool Success,
    PreflightScriptSummary? Script,
    IReadOnlyList<ScriptDiagnosticDto> Diagnostics,
    string? Error = null);

/// <summary>Completed result of executing a preflight or shutdown program script in isolation.</summary>
/// <param name="Success"><see langword="true"/> when the program completed without throwing an error.</param>
/// <param name="DurationMs">Total execution duration in milliseconds.</param>
/// <param name="Error">Optional error message if the program failed.</param>
/// <param name="Logs">Formatted log lines emitted during execution.</param>
public sealed record PreflightRunResult(
    bool Success,
    double DurationMs,
    string? Error,
    IReadOnlyList<string> Logs);

/// <summary>Request payload for uploading or updating a scenario, preflight, or shutdown C# script.</summary>
/// <param name="Id">Optional existing script identifier to update, or <see langword="null"/> to create/derive one.</param>
/// <param name="FileName">Optional target file name for the script.</param>
/// <param name="Code">C# source code of the script.</param>
public sealed record UploadScriptRequest(string? Id, string? FileName, string? Code);

/// <summary>Request payload for executing a single-bee test run of a scenario script.</summary>
/// <param name="Parameters">Optional JSON object overriding scenario parameter values.</param>
/// <param name="EnvironmentName">Optional environment name supplying variables and secrets to the test run.</param>
/// <param name="TimeoutSeconds">Maximum execution timeout in seconds for the test run.</param>
public sealed record TestRunScriptRequest(JsonObject? Parameters = null, string? EnvironmentName = null, int TimeoutSeconds = 30);

/// <summary>Request payload for executing a standalone test run of a preflight or shutdown program script.</summary>
/// <param name="EnvironmentName">Optional environment name supplying variables and secrets to the program run.</param>
/// <param name="TimeoutSeconds">Maximum execution timeout in seconds for the program run.</param>
public sealed record TestRunPreflightRequest(string? EnvironmentName = null, int TimeoutSeconds = 60);
