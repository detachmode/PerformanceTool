namespace Beesharp.Ai.Contracts;

// Wire format of /api/assistant (brain 0047, 0048, 0049). Plain records, JSON with web defaults. Numbering for people is 1-based
// (bee 12 = "B12" in the UI, entry 1 = the first scenario entry), exactly as the UI shows it.

/// <summary>Answer of <c>GET /api/assistant/status</c>. The side panel only shows when <see cref="Configured"/> is <c>true</c>.</summary>
/// <param name="Configured">Whether an AI provider and model are configured on the server.</param>
/// <param name="Model">The configured model identifier, or <c>null</c> when unconfigured.</param>
/// <param name="Reason">Optional human-readable explanation when <see cref="Configured"/> is <c>false</c>.</param>
public sealed record AssistantStatusDto(bool Configured, string? Model, string? Reason = null);

/// <summary>Body of <c>POST /api/assistant/chat</c>. A <c>null</c> <see cref="SessionId"/> starts a new conversation.</summary>
/// <param name="SessionId">Existing conversation session identifier, or <c>null</c> to start a new session.</param>
/// <param name="Message">User prompt sent to the assistant.</param>
public sealed record AssistantChatRequest(string? SessionId, string Message);

/// <summary>What a turn changed, so the client can refresh the editor, the environments, the scripts, or the run.</summary>
public static class ChangedAreas
{
    /// <summary>Indicates the Live load plan was modified.</summary>
    public const string Plan = "plan";

    /// <summary>Indicates saved presets were created, updated, or deleted.</summary>
    public const string Presets = "presets";

    /// <summary>Indicates environments were created, updated, or deleted.</summary>
    public const string Environments = "environments";

    /// <summary>Indicates scenario scripts were created, updated, or deleted.</summary>
    public const string Scripts = "scripts";

    /// <summary>Indicates preflight or shutdown scripts were created, updated, or deleted.</summary>
    public const string Preflights = "preflights";

    /// <summary>Indicates a load test run was started or stopped.</summary>
    public const string Run = "run";
}

/// <summary>Discriminator values for <see cref="AssistantItemDto.Type"/>.</summary>
public static class AssistantItemTypes
{
    /// <summary>Plain assistant prose item (<see cref="AssistantItemDto.Text"/> is set).</summary>
    public const string Text = "text";

    /// <summary>Tool activity chip item (<see cref="AssistantItemDto.Tool"/> is set).</summary>
    public const string Tool = "tool";

    /// <summary>Rich preview card item (<see cref="AssistantItemDto.View"/> is set).</summary>
    public const string View = "view";
}

/// <summary>
/// One answer from <c>POST /api/assistant/chat</c>. <see cref="Items"/> are in the order they happened:
/// text of the assistant, quiet activity chips, and view cards.
/// <see cref="Error"/> is set when the turn failed (the items before it are still valid).
/// </summary>
/// <param name="SessionId">Session identifier for continuing the conversation.</param>
/// <param name="Items">Ordered list of text, tool activity, and view items produced during the turn.</param>
/// <param name="Changed">Distinct <see cref="ChangedAreas"/> values modified during the turn.</param>
/// <param name="Error">Error message if the turn failed partway through, or <c>null</c> on success.</param>
public sealed record AssistantTurnDto(
    string SessionId,
    IReadOnlyList<AssistantItemDto> Items,
    IReadOnlyList<string> Changed,
    string? Error = null);

/// <summary>
/// One entry of an <see cref="AssistantTurnDto"/>. Exactly one of <see cref="Text"/>, <see cref="Tool"/>,
/// or <see cref="View"/> is set, named by <see cref="Type"/> (<see cref="AssistantItemTypes"/>).
/// </summary>
/// <param name="Type">Item type discriminator from <see cref="AssistantItemTypes"/>.</param>
/// <param name="Text">Markdown or plain text response when <see cref="Type"/> is <see cref="AssistantItemTypes.Text"/>.</param>
/// <param name="Tool">Tool execution summary when <see cref="Type"/> is <see cref="AssistantItemTypes.Tool"/>.</param>
/// <param name="View">Interactive preview card when <see cref="Type"/> is <see cref="AssistantItemTypes.View"/>.</param>
public sealed record AssistantItemDto(string Type, string? Text = null, ToolActivityDto? Tool = null, ViewItemDto? View = null);

/// <summary>
/// What the agent did with one tool: a one-line summary for the chip, and input and output JSON that the user can reveal.
/// <see cref="Changes"/> is <c>true</c> for tools that changed something (plan, presets, environments, scripts, run); reads are grouped quietly.
/// <see cref="Link"/> says where the chip leads when clicked (brain 0029).
/// </summary>
/// <param name="Id">Unique tool call identifier within the turn.</param>
/// <param name="Name">Registered assistant tool name.</param>
/// <param name="Summary">Short one-line description of what the tool did.</param>
/// <param name="Ok"><c>true</c> if the tool call succeeded; <c>false</c> if it failed.</param>
/// <param name="Changes"><c>true</c> if the tool mutated server state; <c>false</c> for read-only calls.</param>
/// <param name="Area">Optional area from <see cref="ChangedAreas"/> affected by the tool.</param>
/// <param name="InputJson">Serialized JSON arguments passed to the tool, if captured.</param>
/// <param name="OutputJson">Serialized JSON result returned by the tool, if captured.</param>
/// <param name="Link">Optional UI navigation target when the user clicks the activity chip.</param>
public sealed record ToolActivityDto(
    string Id,
    string Name,
    string Summary,
    bool Ok,
    bool Changes,
    string? Area,
    string? InputJson,
    string? OutputJson,
    ChipLinkDto? Link = null);

/// <summary>
/// Where a chip or card leads: <c>editor</c>, <c>calls</c>, <c>logs</c>, <c>bee</c>, <c>run</c> (overview),
/// <c>environments</c>, or <c>scripts</c>.
/// </summary>
/// <param name="Target">Destination UI route or view identifier.</param>
/// <param name="Query">Optional filter and selection query parameters for the destination view.</param>
public sealed record ChipLinkDto(string Target, ViewQueryDto? Query = null);

/// <summary>
/// The query behind a card (not a copy of the data): the client re-queries live data with it.
/// Numbers are 1-based like the UI.
/// </summary>
/// <param name="RunId">Target run identifier, or <c>null</c> for the current or latest run.</param>
/// <param name="Entry">1-based scenario entry number to filter by.</param>
/// <param name="Bee">1-based bee number to select or filter by.</param>
/// <param name="Outcome">Outcome filter such as <c>all</c>, <c>ok</c>, <c>slow</c>, or <c>failed</c>.</param>
/// <param name="Levels">Log level names to filter by (e.g., <c>Information</c>, <c>Warning</c>, <c>Error</c>).</param>
/// <param name="Search">Substring filter for log messages or endpoint names.</param>
/// <param name="FailedBees">When <c>true</c>, restricts results to failed bees.</param>
/// <param name="SlowBees">When <c>true</c>, restricts results to slow bees.</param>
/// <param name="Endpoint">Normalized endpoint name (e.g., <c>GET /orders/{id}</c>) to inspect.</param>
/// <param name="MinMs">Minimum call duration threshold in milliseconds.</param>
public sealed record ViewQueryDto(
    string? RunId = null,
    int? Entry = null,
    int? Bee = null,
    string? Outcome = null,
    IReadOnlyList<string>? Levels = null,
    string? Search = null,
    bool? FailedBees = null,
    bool? SlowBees = null,
    string? Endpoint = null,
    double? MinMs = null);

/// <summary>Discriminator values for <see cref="ViewItemDto.Kind"/>.</summary>
public static class ViewKinds
{
    /// <summary>Preview card showing matching log lines.</summary>
    public const string Logs = "logs";

    /// <summary>Preview card listing bees filtered by outcome or entry.</summary>
    public const string Bees = "bees";

    /// <summary>Preview card showing a single bee and its call waterfall.</summary>
    public const string Bee = "bee";

    /// <summary>Preview card showing slow/failing endpoints or individual call samples.</summary>
    public const string Calls = "calls";

    /// <summary>Preview card summarizing a single scenario entry and its first bees.</summary>
    public const string Scenario = "scenario";

    /// <summary>Preview card summarizing an entire run and its scenario entries.</summary>
    public const string Run = "run";

    /// <summary>Preview card showing a diff of changes made to the Live plan.</summary>
    public const string PlanChanges = "plan-changes";

    /// <summary>Preview card plotting a hand-picked set of bees on a shared timeline.</summary>
    public const string Timeline = "timeline";
}

/// <summary>
/// A small custom card in the chat (brain 0049). <see cref="Query"/> is what to ask the server for the full data;
/// the other members hold a small bounded preview of it (only the ones that fit <see cref="Kind"/>, see <see cref="ViewKinds"/>).
/// </summary>
/// <param name="Kind">Card kind discriminator from <see cref="ViewKinds"/>.</param>
/// <param name="Title">Display heading shown at the top of the card.</param>
/// <param name="Query">Query parameters used by the client to drill down into the full data.</param>
/// <param name="Total">Total number of matching items on the server before preview truncation.</param>
/// <param name="Logs">Preview log lines when <see cref="Kind"/> is <see cref="ViewKinds.Logs"/>.</param>
/// <param name="Bees">Preview bees when <see cref="Kind"/> is <see cref="ViewKinds.Bees"/>, <see cref="ViewKinds.Scenario"/>, or <see cref="ViewKinds.Timeline"/>.</param>
/// <param name="Bee">Single bee detail and call waterfall when <see cref="Kind"/> is <see cref="ViewKinds.Bee"/>.</param>
/// <param name="Run">Run summary when <see cref="Kind"/> is <see cref="ViewKinds.Run"/> or <see cref="ViewKinds.Scenario"/>.</param>
/// <param name="Changes">Plan diff entries when <see cref="Kind"/> is <see cref="ViewKinds.PlanChanges"/>.</param>
/// <param name="EntryLabels">Ordered labels of scenario entries in the referenced run (0-indexed matching 1-based <c>Entry - 1</c>).</param>
/// <param name="CallEndpoints">Endpoint summaries when <see cref="Kind"/> is <see cref="ViewKinds.Calls"/>.</param>
/// <param name="CallSamples">Individual call samples for a selected endpoint when <see cref="Kind"/> is <see cref="ViewKinds.Calls"/>.</param>
public sealed record ViewItemDto(
    string Kind,
    string Title,
    ViewQueryDto Query,
    long Total = 0,
    IReadOnlyList<ViewLogLineDto>? Logs = null,
    IReadOnlyList<ViewBeeDto>? Bees = null,
    ViewBeeDetailDto? Bee = null,
    ViewRunDto? Run = null,
    IReadOnlyList<ViewPlanChangeDto>? Changes = null,
    IReadOnlyList<string>? EntryLabels = null,
    IReadOnlyList<ViewCallEndpointDto>? CallEndpoints = null,
    IReadOnlyList<ViewCallSampleDto>? CallSamples = null);

/// <summary>A single log line preview displayed inside a logs view card.</summary>
/// <param name="Bee">1-based bee number that emitted the log line.</param>
/// <param name="BeeName">Display label for the bee (for example, <c>B12</c>).</param>
/// <param name="Entry">1-based scenario entry number.</param>
/// <param name="AtSeconds">Elapsed run time in seconds when the log line was recorded.</param>
/// <param name="Level">Log severity level (for example, <c>Information</c>, <c>Warning</c>, <c>Error</c>).</param>
/// <param name="Message">Formatted log message text (bounded for preview).</param>
/// <param name="Exception">Optional exception details associated with the log line.</param>
public sealed record ViewLogLineDto(
    int Bee,
    string BeeName,
    int Entry,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception);

/// <summary>
/// A bee in a list or on the timeline. <see cref="Outcome"/> is <c>ok</c>, <c>slow</c>, or <c>failed</c>;
/// <see cref="Note"/> is the agent's short reason for showing it.
/// </summary>
/// <param name="Bee">1-based bee number.</param>
/// <param name="Entry">1-based scenario entry number.</param>
/// <param name="EntryLabel">Display label of the scenario entry.</param>
/// <param name="StartedAt">Start offset in seconds from the beginning of the run.</param>
/// <param name="DurationMs">Total execution duration of the bee in milliseconds.</param>
/// <param name="StatusCode">Representative HTTP status code if recorded, or <c>null</c>.</param>
/// <param name="Outcome">Execution outcome: <c>ok</c>, <c>slow</c>, or <c>failed</c>.</param>
/// <param name="Error">Error summary if the bee failed, or <c>null</c>.</param>
/// <param name="Note">Optional short agent annotation explaining why this bee was highlighted.</param>
public sealed record ViewBeeDto(
    int Bee,
    int Entry,
    string EntryLabel,
    double StartedAt,
    double DurationMs,
    int? StatusCode,
    string Outcome,
    string? Error,
    string? Note = null);

/// <summary>A single recorded HTTP or operation call inside a bee's waterfall.</summary>
/// <param name="Name">Operation or step name for the call.</param>
/// <param name="OffsetMs">Start offset in milliseconds relative to the start of the bee.</param>
/// <param name="DurationMs">Duration of the call in milliseconds.</param>
/// <param name="StatusCode">HTTP status code returned by the call, or <c>null</c> if unavailable.</param>
/// <param name="Error">Error message if the call failed, or <c>null</c>.</param>
/// <param name="Endpoint">Normalized endpoint template, if applicable.</param>
/// <param name="IsSlow"><c>true</c> if the call exceeded the slow-call threshold.</param>
public sealed record ViewCallDto(string Name, double OffsetMs, double DurationMs, int? StatusCode, string? Error, string? Endpoint = null, bool IsSlow = false);

/// <summary>Aggregated timing and failure statistics for a single normalized endpoint.</summary>
/// <param name="Endpoint">Normalized endpoint name (for example, <c>GET /orders/{id}</c>).</param>
/// <param name="Method">HTTP method name.</param>
/// <param name="TotalCount">Total number of calls observed for this endpoint.</param>
/// <param name="SlowCount">Number of calls classified as slow.</param>
/// <param name="FailedCount">Number of calls that failed.</param>
/// <param name="TypicalMs">Typical (median/baseline) call duration in milliseconds.</param>
/// <param name="P95Ms">95th percentile call duration in milliseconds.</param>
/// <param name="MaxMs">Maximum observed call duration in milliseconds.</param>
/// <param name="SlowThresholdMs">Effective slow-call threshold in milliseconds.</param>
/// <param name="HasCustomThreshold"><c>true</c> if a custom slow threshold is configured for this endpoint.</param>
public sealed record ViewCallEndpointDto(
    string Endpoint,
    string Method,
    long TotalCount,
    long SlowCount,
    long FailedCount,
    double TypicalMs,
    double P95Ms,
    double MaxMs,
    double SlowThresholdMs,
    bool HasCustomThreshold);

/// <summary>An individual call sample shown when inspecting a specific endpoint in a calls view card.</summary>
/// <param name="Bee">1-based bee number that executed the call.</param>
/// <param name="Entry">1-based scenario entry number.</param>
/// <param name="EntryLabel">Display label of the scenario entry.</param>
/// <param name="AtSeconds">Elapsed run time in seconds when the call occurred.</param>
/// <param name="DurationMs">Call duration in milliseconds.</param>
/// <param name="StatusCode">HTTP status code returned by the call, or <c>null</c>.</param>
/// <param name="Error">Error message if the call failed, or <c>null</c>.</param>
/// <param name="IsSlow"><c>true</c> if the call was classified as slow.</param>
/// <param name="Ratio">Slowness ratio relative to the typical duration, or <c>null</c> for failed or unbaselined calls.</param>
/// <param name="Url">Concrete request URL (bounded for preview).</param>
public sealed record ViewCallSampleDto(
    int Bee,
    int Entry,
    string EntryLabel,
    double AtSeconds,
    double DurationMs,
    int? StatusCode,
    string? Error,
    bool IsSlow,
    double? Ratio,
    string Url);

/// <summary>Detailed view payload for a single bee and its recorded calls.</summary>
/// <param name="Bee">Summary metadata for the bee.</param>
/// <param name="Calls">Bounded list of calls executed by the bee.</param>
public sealed record ViewBeeDetailDto(ViewBeeDto Bee, IReadOnlyList<ViewCallDto> Calls);

/// <summary>Execution figures for a single scenario entry within a run.</summary>
/// <param name="Entry">1-based scenario entry number.</param>
/// <param name="Label">Display label for the scenario entry.</param>
/// <param name="Starts">Total number of bee executions started.</param>
/// <param name="Finished">Total number of bee executions finished.</param>
/// <param name="InFlight">Number of bee executions currently in flight.</param>
/// <param name="Failed">Number of failed bee executions.</param>
/// <param name="SlowBees">Number of finished bees with at least one slow call.</param>
/// <param name="Dropped">Number of scheduled starts dropped due to concurrency limits.</param>
/// <param name="BeesPerMinute">Configured or effective arrival rate in bees per minute.</param>
public sealed record ViewEntryDto(
    int Entry,
    string Label,
    int Starts,
    int Finished,
    int InFlight,
    int Failed,
    int SlowBees,
    int Dropped,
    double BeesPerMinute);

/// <summary>Summary snapshot of a run displayed in a run or scenario view card.</summary>
/// <param name="RunId">Unique run identifier.</param>
/// <param name="State">Current run lifecycle state.</param>
/// <param name="ElapsedSeconds">Elapsed run duration in seconds.</param>
/// <param name="TotalBees">Total bees executed in the run.</param>
/// <param name="FailedBees">Total failed bees in the run.</param>
/// <param name="SlowBees">Total slow bees in the run.</param>
/// <param name="TotalLogs">Total log lines recorded in the run.</param>
/// <param name="Entries">Per-entry figures included in the card.</param>
public sealed record ViewRunDto(
    string RunId,
    string State,
    double ElapsedSeconds,
    int TotalBees,
    int FailedBees,
    int SlowBees,
    long TotalLogs,
    IReadOnlyList<ViewEntryDto> Entries);

/// <summary>
/// Describes one difference between two versions of a load plan.
/// <see cref="What"/> is <c>added</c>, <c>removed</c>, or <c>changed</c>;
/// <see cref="Details"/> are short lines such as <c>"rate 30 → 60 bees/min"</c>.
/// </summary>
/// <param name="What">Change kind: <c>added</c>, <c>removed</c>, or <c>changed</c>.</param>
/// <param name="Entry">1-based scenario entry number, or <c>0</c> for plan-wide settings.</param>
/// <param name="Label">Display label of the changed entry or <c>"Plan settings"</c>.</param>
/// <param name="Details">Short human-readable lines describing individual property changes.</param>
public sealed record ViewPlanChangeDto(string What, int Entry, string Label, IReadOnlyList<string> Details);
