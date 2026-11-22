using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Beesharp.Ai;
using Beesharp.AiAdapters;
using Beesharp.Ai.Contracts;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Tests;

/// <summary>A model that answers from a script: each step looks at the conversation so far and returns the next response.</summary>
internal sealed class ScriptedChatClient(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] steps) : IChatClient
{
    private int next;
    public List<IReadOnlyList<ChatMessage>> Seen { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Seen.Add(list);
        Options.Add(options);
        if (next >= steps.Length) throw new InvalidOperationException("The script ran out of steps.");
        return Task.FromResult(steps[next++](list));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Call(string tool, Dictionary<string, object?>? args = null, string? text = null, string callId = "c1") =>
        _ =>
        {
            var contents = new List<AIContent>();
            if (text is not null) contents.Add(new TextContent(text));
            contents.Add(new FunctionCallContent(callId, tool, args));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents));
        };

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Say(string text) =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
}

internal sealed class FakeFactory(IChatClient client) : IAssistantChatClientFactory
{
    public IChatClient Create(AssistantOptions options) => client;
}

internal sealed class FakeRunPort : IRunPort
{
    public int Starts, Stops;
    public RunInfo? Current { get; set; }
    public RunInfo? GetCurrent() => Current;
    public Task<RunInfo> StartAsync(string? environmentName, CancellationToken ct)
    {
        Starts++;
        Current = new RunInfo("run-1", "Running", 0, DateTimeOffset.UtcNow, 0, 0, 0, 0, null, [new EntryFigures(0, "Checkout", 0, 0, 0, 0, 0, 0, 0, 60)]);
        return Task.FromResult(Current);
    }
    public Task<RunInfo> StopAsync(CancellationToken ct) { Stops++; return Task.FromResult(Current!); }
}

internal sealed class FakeHistoryPort : IRunHistoryPort
{
    public static readonly BeeInfo[] Bees =
    [
        new(0, 0, 1.0, 120, 200, 10, null, 0),
        new(1, 0, 2.0, 2400, 200, 10, null, 2),
        new(2, 1, 3.0, 300, 500, 0, "HTTP 500 on /pay", 0),
        new(3, 1, 4.0, 90, 200, 10, null, 0),
    ];

    public Task<string?> LatestRunIdAsync(CancellationToken ct) => Task.FromResult<string?>("run-1");
    public Task<IReadOnlyList<RunInfo>> ListRunsAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<RunInfo>>([]);

    public Task<RunInfo?> GetRunAsync(string runId, CancellationToken ct) => Task.FromResult<RunInfo?>(new RunInfo(runId, "Finished", 30, DateTimeOffset.UtcNow, 4, 1, 1, 6, null,
    [
        new EntryFigures(0, "Search", 2, 2, 0, 0, 1, 2, 0, 30),
        new EntryFigures(1, "Checkout", 2, 2, 0, 1, 0, 0, 0, 30),
    ]));

    public Task<BeePage> ListBeesAsync(string runId, int? entry, BeeOutcomeFilter outcome, int offset, int limit, CancellationToken ct)
    {
        var items = Bees.Where(b => (entry is null || b.Entry == entry) && (outcome == BeeOutcomeFilter.All || (outcome == BeeOutcomeFilter.Failed && b.Outcome == "failed") || (outcome == BeeOutcomeFilter.Slow && b.Outcome == "slow") || (outcome == BeeOutcomeFilter.Ok && b.Outcome == "ok"))).ToList();
        return Task.FromResult(new BeePage(items.Count, items.Skip(offset).Take(limit).ToList()));
    }

    public Task<IReadOnlyList<BeeInfo>> GetBeesAsync(string runId, IReadOnlyList<int> sequences, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BeeInfo>>(Bees.Where(b => sequences.Contains(b.Sequence)).ToList());

    public Task<BeeDetailInfo?> GetBeeAsync(string runId, int sequence, CancellationToken ct) =>
        Task.FromResult<BeeDetailInfo?>(Bees.Where(b => b.Sequence == sequence).Select(b => new BeeDetailInfo(b, [new BeeCallInfo("POST /pay", 0, b.DurationMs, b.StatusCode, b.Error)], [])).FirstOrDefault());

    public LogFilter? LastFilter;
    public Task<LogPageInfo> QueryLogsAsync(string runId, LogFilter filter, CancellationToken ct)
    {
        LastFilter = filter;
        var lines = new List<LogLineInfo>
        {
            new(1, 2, 1, 3.1, "Error", "HTTP 500 on /pay", null),
            new(2, 2, 1, 3.2, "Warning", new string('x', 1000), "System.Exception: boom"),
        };
        return Task.FromResult(new LogPageInfo(214, lines));
    }
}

internal sealed class FakeEnvironmentPort : IEnvironmentPort
{
    public Dictionary<string, EnvironmentInfo> Stored { get; } = new() { ["Staging"] = new("Staging", [new("BaseUrl", "https://staging.example.com"), new("Token", "abcdefghijklmnop")]) };
    public Task<IReadOnlyList<EnvironmentInfo>> ListAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<EnvironmentInfo>>(Stored.Values.ToList());
    public Task<EnvironmentInfo?> GetAsync(string name, CancellationToken ct) => Task.FromResult(Stored.GetValueOrDefault(name));
    public Task SaveAsync(EnvironmentInfo environment, CancellationToken ct) { Stored[environment.Name] = environment; return Task.CompletedTask; }
    public Task<bool> DeleteAsync(string name, CancellationToken ct) => Task.FromResult(Stored.Remove(name));
}

internal sealed class FakeScriptPort : IScriptPort
{
    public List<string> DeletedScripts { get; } = [];
    public IReadOnlyList<ScriptInfo> ListScripts() => [];
    public ScriptInfo? GetScript(string id) => null;
    public Task<CompileOutcome> UploadScriptAsync(string? id, string? fileName, string code, CancellationToken ct) => throw new NotSupportedException();
    public Task<ScriptTestOutcome> TestRunScriptAsync(string id, System.Text.Json.Nodes.JsonObject? parameters, string? environmentName, int timeoutSeconds, CancellationToken ct) => throw new NotSupportedException();
    public bool DeleteScript(string id) { DeletedScripts.Add(id); return true; }
    public IReadOnlyList<ScriptInfo> ListPreflights() => [];
    public ScriptInfo? GetPreflight(string id) => null;
    public Task<CompileOutcome> UploadPreflightAsync(string? id, string? fileName, string code, CancellationToken ct) => throw new NotSupportedException();
    public Task<PreflightTestOutcome> TestRunPreflightAsync(string id, string? environmentName, int timeoutSeconds, CancellationToken ct) => throw new NotSupportedException();
    public bool DeletePreflight(string id) => false;
}

public sealed class AssistantTests : IDisposable
{
    private sealed class FakeHost(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "t";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private readonly string root = Path.Combine(Path.GetTempPath(), "beesharp-ai-" + Guid.NewGuid().ToString("N"));
    private readonly ScenarioCatalog catalog = new([new HttpGetScenario()]);
    private readonly FakeRunPort runs = new();
    private readonly FakeHistoryPort history = new();
    private readonly FakeEnvironmentPort envs = new();
    private readonly FakeScriptPort scripts = new();
    private readonly PlanWorkspace workspace;

    public AssistantTests()
    {
        Directory.CreateDirectory(root);
        workspace = new PlanWorkspace(catalog, new PresetStore(new ConfigurationBuilder().Build(), new FakeHost(root), NullLogger<PresetStore>.Instance));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch { }
    }

    private static AssistantOptions Configured(int maxToolCalls = 24) => new() { Model = "test-model", ApiKey = "k", MaxToolCalls = maxToolCalls };

    private AssistantService Service(IChatClient client, AssistantOptions? options = null)
    {
        options ??= Configured();
        return new AssistantService(options, new FakeFactory(client), new AssistantSessions(options), new PlanWorkspaceAdapter(workspace), runs, history, envs, scripts, catalog, NullLogger<AssistantService>.Instance);
    }

    private static T Item<T>(AssistantTurnDto turn, Func<AssistantItemDto, T?> pick) where T : class => turn.Items.Select(pick).First(x => x is not null)!;

    // ---------- options and status ----------

    [Fact]
    public void Options_bind_from_environment_variable_names()
    {
        var names = new Dictionary<string, string>
        {
            ["AI__Provider"] = "OpenAI", ["AI__Endpoint"] = "http://localhost:11434/v1", ["AI__ApiKey"] = "secret", ["AI__Model"] = "llama3", ["AI__MaxToolCalls"] = "7", ["AI__SystemPrompt"] = "Be brief.",
        };
        try
        {
            foreach (var (k, v) in names) Environment.SetEnvironmentVariable(k, v);
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddBeesharpAi(config);
            var options = (AssistantOptions)services.First(d => d.ServiceType == typeof(AssistantOptions)).ImplementationInstance!;
            Assert.Equal("llama3", options.Model);
            Assert.Equal("http://localhost:11434/v1", options.Endpoint);
            Assert.Equal("secret", options.ApiKey);
            Assert.Equal(7, options.MaxToolCalls);
            Assert.Equal("Be brief.", options.SystemPrompt);
            Assert.True(options.IsConfigured);
        }
        finally { foreach (var k in names.Keys) Environment.SetEnvironmentVariable(k, null); }
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("gpt", null, null, false)]
    [InlineData("gpt", "key", null, true)]
    [InlineData("llama", null, "http://localhost:11434/v1", true)]
    public void Configured_needs_a_model_and_a_key_or_an_endpoint(string? model, string? key, string? endpoint, bool expected) =>
        Assert.Equal(expected, new AssistantOptions { Model = model, ApiKey = key, Endpoint = endpoint }.IsConfigured);

    [Fact]
    public void Unknown_provider_is_not_configured()
    {
        var o = new AssistantOptions { Provider = "Other", Model = "m", ApiKey = "k" };
        Assert.False(o.IsConfigured);
        Assert.Contains("Other", o.NotConfiguredReason);
    }

    [Fact]
    public async Task Not_configured_reports_status_and_refuses_to_chat()
    {
        var service = Service(new ScriptedChatClient(), new AssistantOptions());
        var status = service.Status();
        Assert.False(status.Configured);
        Assert.Null(status.Model);
        Assert.NotNull(status.Reason);
        await Assert.ThrowsAsync<AssistantNotConfiguredException>(() => service.ChatAsync(new AssistantChatRequest(null, "hi"), default));
        Assert.True(Service(new ScriptedChatClient()).Status().Configured);
    }

    // ---------- tool calling ----------

    [Fact]
    public async Task A_tool_call_edits_the_plan_and_the_turn_records_a_chip_and_the_changed_area()
    {
        workspace.AddEntry("http-get");
        workspace.Update(p => { p.Scenarios[0].Label = "Checkout"; return true; });
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("update_scenario_entry", new() { ["entry"] = 1, ["beesPerMinute"] = 60.0 }),
            ScriptedChatClient.Say("Rate is now 60 bees per minute."));

        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "double the checkout rate"), default);

        Assert.Equal(60, workspace.GetPlan().Scenarios[0].RatePerMinute);
        Assert.Equal([ChangedAreas.Plan], turn.Changed);
        var chip = Item(turn, i => i.Tool);
        Assert.Equal("update_scenario_entry", chip.Name);
        Assert.Equal("Changed rate of Checkout to 60 bees/min", chip.Summary);
        Assert.True(chip.Ok);
        Assert.True(chip.Changes);
        Assert.Contains("60", chip.InputJson);
        Assert.NotNull(chip.OutputJson);
        Assert.Equal("editor", chip.Link?.Target);
        Assert.Equal("Rate is now 60 bees per minute.", Item(turn, i => i.Text));
        Assert.Null(turn.Error);
        Assert.False(string.IsNullOrEmpty(turn.SessionId));
        // The model got the tools, the system prompt and the tool result.
        Assert.Contains(model.Options[0]!.Tools!, t => t.Name == "get_plan");
        Assert.Equal(ChatRole.System, model.Seen[0][0].Role);
        Assert.Contains(model.Seen[1], m => m.Role == ChatRole.Tool);
    }

    [Fact]
    public async Task Run_control_and_deletes_run_directly_without_approval()
    {
        workspace.AddEntry("http-get");
        await workspace.SavePresetAsync("Old");
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("start_run", new() { ["environmentName"] = "Staging" }, callId: "a"),
            ScriptedChatClient.Call("stop_run", callId: "b"),
            ScriptedChatClient.Call("delete_preset", new() { ["name"] = "Old" }, callId: "c"),
            ScriptedChatClient.Call("delete_environment", new() { ["name"] = "Staging" }, callId: "d"),
            ScriptedChatClient.Call("delete_script", new() { ["scriptId"] = "s1" }, callId: "e"),
            ScriptedChatClient.Say("Done."));

        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "start, stop, clean up"), default);

        Assert.Equal(1, runs.Starts);
        Assert.Equal(1, runs.Stops);
        Assert.Empty(await workspace.ListPresetsAsync());
        Assert.Empty(envs.Stored);
        Assert.Equal(["s1"], scripts.DeletedScripts);
        Assert.Equal(5, turn.Items.Count(i => i.Tool is { Changes: true }));
        Assert.Contains(ChangedAreas.Run, turn.Changed);
        Assert.Contains(ChangedAreas.Environments, turn.Changed);
        Assert.Contains(turn.Items, i => i.Tool?.Summary == "Started a run with 1 scenario entries on Staging");
    }

    [Fact]
    public async Task A_failing_tool_becomes_a_failed_chip_and_the_conversation_continues()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("update_scenario_entry", new() { ["entry"] = 3, ["beesPerMinute"] = 1.0 }),
            ScriptedChatClient.Say("That entry does not exist."));
        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "change entry 3"), default);

        var chip = Item(turn, i => i.Tool);
        Assert.False(chip.Ok);
        Assert.False(chip.Changes);
        Assert.Contains("There is no entry 3", chip.Summary);
        Assert.Null(turn.Error);
        Assert.Equal("That entry does not exist.", Item(turn, i => i.Text));
        Assert.Contains(model.Seen[1].Last().Contents.OfType<FunctionResultContent>(), r => r.Result?.ToString()?.StartsWith("Error:") == true);
    }

    [Fact]
    public async Task Unknown_tools_and_the_call_limit_are_handled()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("nope", callId: "a"),
            ScriptedChatClient.Call("get_plan", callId: "b"),
            ScriptedChatClient.Call("get_plan", callId: "c"));
        var turn = await Service(model, Configured(maxToolCalls: 2)).ChatAsync(new AssistantChatRequest(null, "go"), default);
        Assert.Equal(2, turn.Items.Count(i => i.Tool is not null));
        Assert.Contains(turn.Items, i => i.Text?.StartsWith("I stopped after 2 tool calls") == true);
    }

    [Fact]
    public async Task A_model_error_is_returned_in_the_turn_and_the_history_is_rolled_back()
    {
        var service = Service(new ScriptedChatClient(_ => throw new InvalidOperationException("boom")));
        var turn = await service.ChatAsync(new AssistantChatRequest(null, "hi"), default);
        Assert.Equal("boom", turn.Error);
        Assert.Empty(turn.Items);
    }

    [Fact]
    public async Task A_conversation_keeps_its_history_and_sessions_can_be_deleted()
    {
        var model = new ScriptedChatClient(ScriptedChatClient.Say("one"), ScriptedChatClient.Say("two"));
        var service = Service(model);
        var first = await service.ChatAsync(new AssistantChatRequest(null, "a"), default);
        var second = await service.ChatAsync(new AssistantChatRequest(first.SessionId, "b"), default);
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal(3, model.Seen[1].Count(m => m.Role != ChatRole.System)); // user, assistant, user
        Assert.True(service.DeleteSession(first.SessionId));
        Assert.False(service.DeleteSession(first.SessionId));
    }

    [Fact]
    public void Sessions_are_capped_and_idle_ones_expire()
    {
        var options = new AssistantOptions { MaxSessions = 2, SessionIdleMinutes = 60 };
        var store = new AssistantSessions(options);
        var a = store.GetOrCreate(null);
        store.GetOrCreate(null);
        store.GetOrCreate(null);
        Assert.Equal(2, store.Count);
        Assert.NotEqual(a.Id, store.GetOrCreate(a.Id).Id); // the oldest was dropped, so its id starts a new conversation
    }

    // ---------- secrets ----------

    [Fact]
    public async Task Environment_secrets_are_masked_for_the_model_and_masked_values_keep_the_stored_ones()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("get_environment", new() { ["name"] = "Staging" }),
            ScriptedChatClient.Call("save_environment", new() { ["name"] = "Staging", ["variables"] = new Dictionary<string, string> { ["Token"] = "ab••••", ["Tenant"] = "acme" } }),
            ScriptedChatClient.Say("ok"));
        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "look and add tenant"), default);

        var read = turn.Items.Select(i => i.Tool).First(t => t?.Name == "get_environment")!;
        Assert.DoesNotContain("abcdefghijklmnop", read.OutputJson);
        Assert.Contains("ab••••", read.OutputJson);
        Assert.Contains("https://staging.example.com", read.OutputJson);
        Assert.Equal("abcdefghijklmnop", envs.Stored["Staging"].Variables.First(v => v.Key == "Token").Value);
        Assert.Equal("acme", envs.Stored["Staging"].Variables.First(v => v.Key == "Tenant").Value);
    }

    // ---------- show tools ----------

    [Fact]
    public async Task Show_logs_adds_a_logs_card_with_the_query_a_bounded_preview_and_no_chip()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("show_logs", new() { ["failedBees"] = true, ["entry"] = 2, ["levels"] = new List<string> { "error", "warn" }, ["title"] = "Checkout errors" }),
            ScriptedChatClient.Say("214 lines, mostly 500s."));
        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "show checkout errors"), default);

        Assert.DoesNotContain(turn.Items, i => i.Tool is not null);
        var view = Item(turn, i => i.View);
        Assert.Equal(ViewKinds.Logs, view.Kind);
        Assert.Equal("Checkout errors", view.Title);
        Assert.Equal(214, view.Total);
        Assert.Equal("run-1", view.Query.RunId);
        Assert.Equal(2, view.Query.Entry);
        Assert.True(view.Query.FailedBees);
        Assert.Equal(["Error", "Warning"], view.Query.Levels);
        Assert.Equal(2, view.Logs!.Count);
        Assert.Equal(3, view.Logs[0].Bee); // sequence 2 is bee B3
        Assert.True(view.Logs[1].Message.Length <= Bounds_MaxLogMessagePlusEllipsis);
        Assert.Equal(1, history.LastFilter!.Entry); // 1-based entry 2 reaches the port as index 1
        Assert.Equal(["Error", "Warning"], history.LastFilter.Levels);
        Assert.Equal(6, history.LastFilter.Limit);
        // The model hears what is on the card, in short.
        var result = model.Seen[1].Last().Contents.OfType<FunctionResultContent>().Single();
        Assert.Contains("A logs card with 2 of 214", result.Result?.ToString());
    }

    private const int Bounds_MaxLogMessagePlusEllipsis = 301;

    [Fact]
    public async Task Show_bee_timeline_holds_the_picked_bees_in_time_order_with_notes_and_outcomes()
    {
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("show_bee_timeline", new()
            {
                ["bees"] = new List<BeeNote> { new(3, "failed payment"), new(2, "slowest search"), new(99, "does not exist") },
                ["title"] = "Slow search and failed payment",
            }),
            ScriptedChatClient.Say("There."));
        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "timeline"), default);

        var view = Item(turn, i => i.View);
        Assert.Equal(ViewKinds.Timeline, view.Kind);
        Assert.Equal("run-1", view.Query.RunId);
        Assert.Equal(["Search", "Checkout"], view.EntryLabels);
        Assert.NotNull(view.Bees);
        Assert.Equal([2, 3], view.Bees.Select(b => b.Bee)); // sorted by start time
        Assert.Equal(["slow", "failed"], view.Bees.Select(b => b.Outcome));
        Assert.Equal(["slowest search", "failed payment"], view.Bees.Select(b => b.Note));
        Assert.Equal(["Search", "Checkout"], view.Bees.Select(b => b.EntryLabel));
        Assert.Equal("HTTP 500 on /pay", view.Bees[1].Error);
        var result = model.Seen[1].Last().Contents.OfType<FunctionResultContent>().Single().Result?.ToString();
        Assert.Contains("99", result); // reported as missing
    }

    [Fact]
    public async Task Other_show_tools_produce_their_card_kinds()
    {
        workspace.AddEntry("http-get");
        workspace.Update(p => { p.Scenarios[0].RatePerMinute = 90; return true; });
        var model = new ScriptedChatClient(
            ScriptedChatClient.Call("show_bees", new() { ["outcome"] = "failed" }, callId: "1"),
            ScriptedChatClient.Call("show_bee", new() { ["bee"] = 3 }, callId: "2"),
            ScriptedChatClient.Call("show_scenario", new() { ["entry"] = 2 }, callId: "3"),
            ScriptedChatClient.Call("show_run", callId: "4"),
            ScriptedChatClient.Call("show_plan_changes", callId: "5"),
            ScriptedChatClient.Say("Shown."));
        var turn = await Service(model).ChatAsync(new AssistantChatRequest(null, "show me everything"), default);

        var kinds = turn.Items.Where(i => i.View is not null).Select(i => i.View!.Kind).ToList();
        Assert.Equal([ViewKinds.Bees, ViewKinds.Bee, ViewKinds.Scenario, ViewKinds.Run, ViewKinds.PlanChanges], kinds);
        var views = turn.Items.Where(i => i.View is not null).Select(i => i.View!).ToList();
        Assert.Equal([3], views[0].Bees!.Select(b => b.Bee));
        Assert.Equal("failed", views[1].Bee!.Bee.Outcome);
        Assert.Single(views[1].Bee!.Calls);
        Assert.Equal("Checkout", views[2].Run!.Entries.Single().Label);
        Assert.Equal(2, views[3].Run!.Entries.Count);
        Assert.Equal("added", views[4].Changes![0].What);
    }

    // ---------- the toolbox itself ----------

    [Fact]
    public void Every_tool_has_a_unique_snake_case_name_and_a_description_and_none_asks_for_approval()
    {
        var tools = new AssistantToolbox(new PlanWorkspaceAdapter(workspace), runs, history, envs, scripts, catalog, new TurnRecorder()).Build();
        Assert.True(tools.Count >= 40);
        Assert.Equal(tools.Count, tools.Select(t => t.Name).Distinct().Count());
        foreach (var tool in tools)
        {
            Assert.Matches("^[a-z]+(_[a-z]+)+$", tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
        }
        Assert.Contains(tools, t => t.Name == "start_run");
        // Optional arguments must not be required in the schema, or a model that leaves them out fails the call.
        foreach (var name in new[] { "start_run", "list_runs", "get_run", "stop_run", "get_run_status", "show_run" })
            Assert.False(tools.Single(t => t.Name == name).JsonSchema.TryGetProperty("required", out var req) && req.GetArrayLength() > 0, name);
        Assert.DoesNotContain(tools, t => t.GetType().Name.Contains("Approval"));
    }
}
