using Beesharp.Ai.Contracts;
using Beesharp.Services;

namespace Beesharp.Ai;

/// <summary>
/// Collects what happens during one turn: the assistant's text, one activity chip per tool call and the view cards the show tools add.
/// Tools talk to it (<see cref="Read"/>, <see cref="Change"/>, <see cref="Show"/>); the agent loop turns it into the <see cref="AssistantTurnDto"/>.
/// </summary>
public sealed class TurnRecorder
{
    private readonly List<AssistantItemDto> items = [];
    private readonly List<string> changed = [];
    private readonly List<ViewItemDto> pendingViews = [];
    private string? summary;
    private string? area;
    private bool isChange;
    private ChipLinkDto? link;
    private bool quiet;

    /// <summary>Gets the ordered items (text, tool activity chips, and view cards) recorded during this turn.</summary>
    public IReadOnlyList<AssistantItemDto> Items => items;
    /// <summary>Gets the distinct area identifiers (<see cref="ChangedAreas"/>) modified during this turn.</summary>
    public IReadOnlyList<string> Changed => changed;

    /// <summary>The plan before the first plan edit of this turn (for <c>show_plan_changes</c>).</summary>
    public LoadPlan? PlanBefore { get; set; }

    /// <summary>Appends a non-empty assistant text message to the recorded turn items.</summary>
    /// <param name="text">The text produced by the assistant.</param>
    public void Text(string text)
    {
        if (!string.IsNullOrWhiteSpace(text)) items.Add(new AssistantItemDto(AssistantItemTypes.Text, Text: text.Trim()));
    }

    /// <summary>A tool that only read something. Reads show as one quiet group in the chat.</summary>
    /// <param name="summary">Short human-readable summary of what the tool read.</param>
    /// <param name="link">Optional navigation link attached to the activity chip.</param>
    public void Read(string summary, ChipLinkDto? link = null)
    {
        this.summary = summary;
        this.link = link;
        isChange = false;
    }

    /// <summary>A tool that changed something in <paramref name="changedArea"/> (<see cref="ChangedAreas"/>).</summary>
    /// <param name="changedArea">Area identifier from <see cref="ChangedAreas"/> indicating what changed.</param>
    /// <param name="summary">Short human-readable summary of the change.</param>
    /// <param name="link">Optional navigation link attached to the activity chip.</param>
    public void Change(string changedArea, string summary, ChipLinkDto? link = null)
    {
        this.summary = summary;
        this.link = link;
        area = changedArea;
        isChange = true;
        if (!changed.Contains(changedArea)) changed.Add(changedArea);
    }

    /// <summary>Adds a card to the chat. A show tool leaves no chip: the card is its trace.</summary>
    /// <param name="view">The view card payload to emit into the turn.</param>
    public void Show(ViewItemDto view)
    {
        pendingViews.Add(view);
        quiet = true;
        summary ??= $"Showed {view.Title}";
    }

    internal void AddActivity(string id, string name, bool ok, string? inputJson, string? outputJson)
    {
        if (!quiet || !ok)
            items.Add(new AssistantItemDto(AssistantItemTypes.Tool, Tool: new ToolActivityDto(
                id, name, summary ?? $"Called {name}", ok, isChange && ok, isChange ? area : null, inputJson, outputJson, link)));
        foreach (var view in pendingViews) items.Add(new AssistantItemDto(AssistantItemTypes.View, View: view));
        pendingViews.Clear();
        summary = null; area = null; isChange = false; link = null; quiet = false;
    }

    /// <summary>A tool that failed: the chip says so, in the tool's own words.</summary>
    internal void Fail(string message)
    {
        summary = message;
        isChange = false;
        quiet = false;
        pendingViews.Clear();
    }
}
