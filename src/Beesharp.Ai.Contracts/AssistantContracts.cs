namespace Beesharp.Ai.Contracts;

// Wire format of /api/assistant (brain 0047, 0048, 0049). Plain records, JSON with web defaults. Numbering for people is 1-based
// (bee 12 = "B12" in the UI, entry 1 = the first scenario entry), exactly as the UI shows it.

/// <summary>Answer of <c>GET /api/assistant/status</c>. The side panel only shows when <see cref="Configured"/> is true.</summary>
public sealed record AssistantStatusDto(bool Configured, string? Model, string? Reason = null);

/// <summary>Body of <c>POST /api/assistant/chat</c>. A null <see cref="SessionId"/> starts a new conversation.</summary>
public sealed record AssistantChatRequest(string? SessionId, string Message);

/// <summary>What a turn changed, so the client can refresh the editor, the environments, the scripts or the run.</summary>
public static class ChangedAreas
{
    public const string Plan = "plan";
    public const string Presets = "presets";
    public const string Environments = "environments";
    public const string Scripts = "scripts";
    public const string Preflights = "preflights";
    public const string Run = "run";
}

public static class AssistantItemTypes
{
    public const string Text = "text";
    public const string Tool = "tool";
    public const string View = "view";
}

/// <summary>
/// One answer. <see cref="Items"/> are in the order they happened: text of the assistant, quiet activity chips and view cards.
/// <see cref="Error"/> is set when the turn failed (the items before it are still valid).
/// </summary>
public sealed record AssistantTurnDto(
    string SessionId,
    IReadOnlyList<AssistantItemDto> Items,
    IReadOnlyList<string> Changed,
    string? Error = null);

/// <summary>One entry of a turn. Exactly one of <see cref="Text"/>, <see cref="Tool"/>, <see cref="View"/> is set, named by <see cref="Type"/> (<see cref="AssistantItemTypes"/>).</summary>
public sealed record AssistantItemDto(string Type, string? Text = null, ToolActivityDto? Tool = null, ViewItemDto? View = null);

/// <summary>
/// What the agent did with one tool: a one-line summary for the chip, and input and output JSON that the user can reveal.
/// <see cref="Changes"/> is true for tools that changed something (plan, presets, environments, scripts, run); reads are grouped quietly.
/// <see cref="Link"/> says where the chip leads when clicked (brain 0029).
/// </summary>
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

/// <summary>Where a chip or card leads: <c>editor</c>, <c>logs</c>, <c>bee</c>, <c>run</c> (overview), <c>environments</c> or <c>scripts</c>.</summary>
public sealed record ChipLinkDto(string Target, ViewQueryDto? Query = null);

/// <summary>The query behind a card (not a copy of the data): the client re-queries live data with it. Numbers are 1-based like the UI.</summary>
public sealed record ViewQueryDto(
    string? RunId = null,
    int? Entry = null,
    int? Bee = null,
    string? Outcome = null,
    IReadOnlyList<string>? Levels = null,
    string? Search = null,
    bool? FailedBees = null,
    bool? SlowBees = null);

public static class ViewKinds
{
    public const string Logs = "logs";
    public const string Bees = "bees";
    public const string Bee = "bee";
    public const string Scenario = "scenario";
    public const string Run = "run";
    public const string PlanChanges = "plan-changes";
    public const string Timeline = "timeline";
}

/// <summary>
/// A small custom card in the chat (brain 0049). <see cref="Query"/> is what to ask the server for the full data; the other
/// members hold a small bounded preview of it (only the ones that fit <see cref="Kind"/>, see <see cref="ViewKinds"/>).
/// </summary>
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
    IReadOnlyList<string>? EntryLabels = null);

public sealed record ViewLogLineDto(
    int Bee,
    string BeeName,
    int Entry,
    double AtSeconds,
    string Level,
    string Message,
    string? Exception);

/// <summary>A bee in a list or on the timeline. <see cref="Outcome"/> is <c>ok</c>, <c>slow</c> or <c>failed</c>; <see cref="Note"/> is the agent's short reason for showing it.</summary>
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

public sealed record ViewCallDto(string Name, double OffsetMs, double DurationMs, int? StatusCode, string? Error);

public sealed record ViewBeeDetailDto(ViewBeeDto Bee, IReadOnlyList<ViewCallDto> Calls);

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

public sealed record ViewRunDto(
    string RunId,
    string State,
    double ElapsedSeconds,
    int TotalBees,
    int FailedBees,
    int SlowBees,
    long TotalLogs,
    IReadOnlyList<ViewEntryDto> Entries);

/// <summary><see cref="What"/> is <c>added</c>, <c>removed</c> or <c>changed</c>; <see cref="Details"/> are short lines such as "rate 30 → 60 bees/min".</summary>
public sealed record ViewPlanChangeDto(string What, int Entry, string Label, IReadOnlyList<string> Details);
