using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using Beesharp.Client.Components;
using Beesharp.Ai.Contracts;
using Beesharp.Client.Services;

namespace Beesharp.Client.Components.Assistant;

/// <summary>One entry of the transcript: a message of the user, or one answer (its text, chips and cards in order).</summary>
public sealed record ChatEntry(bool FromUser, string? Text, IReadOnlyList<AssistantItemDto> Items, string? Error = null, IReadOnlyList<string>? Changed = null);

/// <summary>A set of bees to open in the popup chart (brain 0049). Sequences and entries are 0-based, like the server's data.</summary>
public sealed record BeeSetRequest(
    string Title,
    string RunId,
    IReadOnlyList<int>? Sequences,
    int? Entry,
    string? Outcome,
    int? InitialBee,
    IReadOnlyDictionary<int, string>? Notes,
    string? Show = null);

/// <summary>Log lines to list full screen when the Logs popup cannot show the card's filter (levels, search).</summary>
public sealed record LogsFullRequest(string Title, string RunId, ViewQueryDto Query);

/// <summary>
/// State of the assistant side panel (brain 0047): availability (Live mode and the server says configured), open or closed, the transcript,
/// sending a message, refreshing what a turn changed, and opening what a chip or card leads to (brain 0029).
/// </summary>
public sealed class AssistantState : IDisposable
{
    private readonly AssistantClient client;
    private readonly DataModeState mode;
    private readonly ApiRunSource api;
    private readonly ApiEnvironmentSource environments;
    private readonly ScriptApiClient scripts;
    private readonly LogsPopupState logs;
    private readonly EnvironmentsPopupState environmentsPopup;
    private readonly NavigationManager nav;
    private readonly IJSRuntime? js;
    private AssistantStatusDto? status;
    private CancellationTokenSource? sending;

    public AssistantState(AssistantClient client, DataModeState mode, ApiRunSource api, ApiEnvironmentSource environments, ScriptApiClient scripts,
        LogsPopupState logs, EnvironmentsPopupState environmentsPopup, NavigationManager nav, IJSRuntime? js = null)
    {
        this.client = client;
        this.mode = mode;
        this.api = api;
        this.environments = environments;
        this.scripts = scripts;
        this.logs = logs;
        this.environmentsPopup = environmentsPopup;
        this.nav = nav;
        this.js = js;
        mode.Changed += OnModeChanged;
        if (!mode.IsFakeMode) _ = CheckStatusAsync();
    }

    public event Action? Changed;

    /// <summary>The chat icon only shows in Live mode, and only when the server's assistant has a model.</summary>
    public bool Available => !mode.IsFakeMode && status is { Configured: true };
    public string? Model => status?.Model;
    public bool IsOpen { get; private set; }
    public bool Busy { get; private set; }
    public string? SessionId { get; private set; }
    public List<ChatEntry> Entries { get; } = [];

    /// <summary>The bee set the popup chart should show (null = closed), and the full-screen log list.</summary>
    public BeeSetRequest? BeeSet { get; private set; }
    public int BeeSetVersion { get; private set; }
    public LogsFullRequest? FullLogs { get; private set; }

    public async Task CheckStatusAsync()
    {
        if (mode.IsFakeMode) return;
        status = await client.GetStatusAsync();
        Changed?.Invoke();
    }

    private void OnModeChanged()
    {
        if (mode.IsFakeMode)
        {
            IsOpen = false;
            Changed?.Invoke();
        }
        else _ = CheckStatusAsync();
    }

    public void Toggle()
    {
        if (!Available) return;
        IsOpen = !IsOpen;
        Changed?.Invoke();
    }

    public void Close()
    {
        if (!IsOpen) return;
        IsOpen = false;
        Changed?.Invoke();
    }

    /// <summary>On narrow viewports the assistant is a full-screen sheet; close it when navigating to another page so the target is visible.</summary>
    public async Task CloseIfMobileAsync()
    {
        if (!IsOpen || js is null) return;
        try
        {
            await using var mod = await js.InvokeAsync<IJSObjectReference>("import", "./assistant.js");
            if (await mod.InvokeAsync<bool>("isMobile")) Close();
        }
        catch (JSDisconnectedException) { }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Starts a fresh conversation (the old one is dropped on the server).</summary>
    public void NewChat()
    {
        sending?.Cancel();
        if (SessionId is { } old) _ = client.DeleteSessionAsync(old);
        SessionId = null;
        Entries.Clear();
        Busy = false;
        Changed?.Invoke();
    }

    public async Task SendAsync(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || Busy) return;
        Entries.Add(new ChatEntry(true, text, []));
        Busy = true;
        Changed?.Invoke();
        sending = new CancellationTokenSource();
        var ct = sending.Token;
        try
        {
            var turn = await client.ChatAsync(SessionId, text, ct);
            SessionId = turn.SessionId;
            Entries.Add(new ChatEntry(false, null, turn.Items, turn.Error, turn.Changed));
            await RefreshAsync(turn.Changed);
        }
        catch (OperationCanceledException) { return; }
        catch (AssistantException ex) { Entries.Add(new ChatEntry(false, null, [], ex.Message)); }
        finally
        {
            Busy = false;
            Changed?.Invoke();
        }
    }

    /// <summary>After a turn: show what the agent changed, live, in the editor, environments, scripts and the run.</summary>
    private async Task RefreshAsync(IReadOnlyList<string> changed)
    {
        var tasks = new List<Task>();
        if (changed.Contains(ChangedAreas.Plan) || changed.Contains(ChangedAreas.Presets)) tasks.Add(api.SyncPlanNowAsync());
        if (changed.Contains(ChangedAreas.Environments)) tasks.Add(environments.RefreshAsync());
        if (changed.Contains(ChangedAreas.Scripts) || changed.Contains(ChangedAreas.Preflights)) tasks.Add(scripts.SyncCatalogAsync());
        if (changed.Contains(ChangedAreas.Run)) tasks.Add(api.RefreshAsync());
        await Task.WhenAll(tasks);
    }

    // ---------- where chips and cards lead (brain 0029) ----------

    /// <summary>Makes sure the run of a card is the one the app shows (the Logs popup and the overview read the loaded run). False when a live run is in the way.</summary>
    public async Task<bool> EnsureRunLoadedAsync(string? runId)
    {
        if (string.IsNullOrEmpty(runId) || api.CurrentRunId == runId) return true;
        if (api.IsRunning) return false;
        await api.LoadStoredRunAsync(runId);
        return api.CurrentRunId == runId;
    }

    public bool IsCurrentRun(string? runId) => !string.IsNullOrEmpty(runId) && api.CurrentRunId == runId;

    public async Task OpenLinkAsync(ChipLinkDto link)
    {
        switch (link.Target)
        {
            case "editor":
                await CloseIfMobileAsync();
                nav.NavigateTo($"?edit={Environment.TickCount64}"); // the plan editor is the overview in edit mode
                break;
            case "scripts":
                await CloseIfMobileAsync();
                nav.NavigateTo("editor");
                break;
            case "environments":
                environmentsPopup.Show();
                break;
            case "logs":
                if (link.Query is { } lq) await OpenLogsAsync("Logs", lq);
                break;
            case "run":
                if (link.Query is { Bee: { } bee, RunId: { } runId }) OpenBees(new BeeSetRequest($"Bee B{bee}", runId, [bee - 1], null, null, bee - 1, null));
                else
                {
                    await EnsureRunLoadedAsync(link.Query?.RunId);
                    await CloseIfMobileAsync();
                    nav.NavigateTo("");
                }
                break;
        }
    }

    /// <summary>Opens the Logs popup pre-filtered when it can show the filter, otherwise the full-screen log list.</summary>
    public async Task OpenLogsAsync(string title, ViewQueryDto query)
    {
        var simple = query.Levels is not { Count: > 0 } && string.IsNullOrWhiteSpace(query.Search);
        if (simple && await EnsureRunLoadedAsync(query.RunId))
        {
            var outcome = query.FailedBees is true ? BeeShow.Failed : query.SlowBees is true ? BeeShow.Slow : BeeShow.All;
            logs.Show(query.Entry is { } e ? e - 1 : null, query.Bee is { } b ? b - 1 : null, outcome);
            return;
        }
        if (query.RunId is { } id)
        {
            FullLogs = new LogsFullRequest(title, id, query);
            Changed?.Invoke();
        }
    }

    public void CloseFullLogs()
    {
        FullLogs = null;
        Changed?.Invoke();
    }

    public void OpenBees(BeeSetRequest request)
    {
        BeeSet = request;
        BeeSetVersion++;
        Changed?.Invoke();
    }

    public void CloseBees()
    {
        BeeSet = null;
        Changed?.Invoke();
    }

    /// <summary>"Full logs" of a bee in a bee set: the Logs popup, when the run is the loaded one.</summary>
    public async Task OpenBeeLogsAsync(string runId, int sequence)
    {
        if (await EnsureRunLoadedAsync(runId)) logs.Show(null, sequence);
    }

    public void Dispose()
    {
        mode.Changed -= OnModeChanged;
        sending?.Cancel();
    }
}
