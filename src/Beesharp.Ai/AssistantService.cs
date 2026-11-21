using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using PerformanceTool.Ai.Contracts;
using PerformanceTool.Services.Scenarios;

namespace PerformanceTool.Ai;

public sealed class AssistantNotConfiguredException(string reason) : Exception(reason);
public sealed class AssistantBusyException() : Exception("The assistant is still working on the previous message of this conversation.");

/// <summary>
/// The agent (brain 0047, 0048): one conversation turn = the user's message, then the model calls tools until it answers. There is no approval
/// step: every tool runs directly. Conversations live in memory (<see cref="AssistantSessions"/>). The loop is our own on top of
/// <see cref="IChatClient"/> (no automatic function invocation), so each tool call can be recorded as an activity chip and the call limit is exact.
/// </summary>
public sealed class AssistantService
{
    private readonly AssistantOptions options;
    private readonly IAssistantChatClientFactory factory;
    private readonly AssistantSessions sessions;
    private readonly IPlanWorkspacePort plan;
    private readonly IRunPort run;
    private readonly IRunHistoryPort history;
    private readonly IEnvironmentPort environments;
    private readonly IScriptPort scripts;
    private readonly ScenarioCatalog catalog;
    private readonly ILogger<AssistantService> logger;
    private IChatClient? client;
    private readonly Lock clientLock = new();

    public AssistantService(
        AssistantOptions options,
        IAssistantChatClientFactory factory,
        AssistantSessions sessions,
        IPlanWorkspacePort plan,
        IRunPort run,
        IRunHistoryPort history,
        IEnvironmentPort environments,
        IScriptPort scripts,
        ScenarioCatalog catalog,
        ILogger<AssistantService> logger)
    {
        this.options = options;
        this.factory = factory;
        this.sessions = sessions;
        this.plan = plan;
        this.run = run;
        this.history = history;
        this.environments = environments;
        this.scripts = scripts;
        this.catalog = catalog;
        this.logger = logger;
    }

    public AssistantStatusDto Status() =>
        options.IsConfigured ? new AssistantStatusDto(true, options.Model!.Trim()) : new AssistantStatusDto(false, null, options.NotConfiguredReason);

    public bool DeleteSession(string sessionId) => sessions.Remove(sessionId);

    public async Task<AssistantTurnDto> ChatAsync(AssistantChatRequest request, CancellationToken ct)
    {
        if (options.NotConfiguredReason is { } reason) throw new AssistantNotConfiguredException(reason);
        if (string.IsNullOrWhiteSpace(request.Message)) throw new ArgumentException("Message is empty.", nameof(request));

        var session = sessions.GetOrCreate(request.SessionId);
        if (!session.Gate.Wait(0)) throw new AssistantBusyException();
        try
        {
            return await RunTurnAsync(session, request.Message.Trim(), ct);
        }
        finally { session.Gate.Release(); }
    }

    private IChatClient Client()
    {
        lock (clientLock) return client ??= factory.Create(options);
    }

    private async Task<AssistantTurnDto> RunTurnAsync(ChatSession session, string message, CancellationToken ct)
    {
        session.Trim();
        var startCount = session.Messages.Count;
        session.Messages.Add(new ChatMessage(ChatRole.User, message));

        var rec = new TurnRecorder();
        var tools = new AssistantToolbox(plan, run, history, environments, scripts, catalog, rec).Build();
        var byName = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var chatOptions = new ChatOptions { Tools = [.. tools] };
        var system = new ChatMessage(ChatRole.System, string.IsNullOrWhiteSpace(options.SystemPrompt) ? SystemPrompt.Default : options.SystemPrompt);
        var maxCalls = Math.Max(1, options.MaxToolCalls);
        var calls = 0;
        string? error = null;

        try
        {
            while (true)
            {
                var messages = new List<ChatMessage>(session.Messages.Count + 1) { system };
                messages.AddRange(session.Messages);
                var response = await Client().GetResponseAsync(messages, chatOptions, ct);

                foreach (var m in response.Messages)
                {
                    session.Messages.Add(m);
                    if (m.Role == ChatRole.Assistant) rec.Text(string.Concat(m.Contents.OfType<TextContent>().Select(t => t.Text)));
                }

                var functionCalls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
                if (functionCalls.Count == 0) break;

                var results = new List<AIContent>();
                var limitHit = false;
                foreach (var call in functionCalls)
                {
                    if (calls >= maxCalls)
                    {
                        limitHit = true;
                        results.Add(new FunctionResultContent(call.CallId, "Not run: the tool call limit for this message was reached."));
                        continue;
                    }
                    calls++;
                    results.Add(new FunctionResultContent(call.CallId, await InvokeAsync(call, byName, rec, ct)));
                }
                session.Messages.Add(new ChatMessage(ChatRole.Tool, results));

                if (limitHit)
                {
                    rec.Text($"I stopped after {maxCalls} tool calls. Ask me to continue if there is more to do.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            session.RollbackTo(startCount);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Assistant turn failed");
            session.RollbackTo(startCount);
            error = Bounds.Cut(ex.GetBaseException().Message, 500);
        }

        session.Touch();
        return new AssistantTurnDto(session.Id, rec.Items, rec.Changed, error);
    }

    private static async Task<object?> InvokeAsync(FunctionCallContent call, Dictionary<string, AIFunction> byName, TurnRecorder rec, CancellationToken ct)
    {
        var inputJson = call.Arguments is { Count: > 0 } ? Bounds.Cut(JsonSerializer.Serialize(call.Arguments, AIJsonUtilities.DefaultOptions), 4000) : null;
        if (!byName.TryGetValue(call.Name, out var function))
        {
            rec.Fail($"The assistant asked for an unknown tool “{call.Name}”");
            rec.AddActivity(call.CallId, call.Name, false, inputJson, null);
            return $"Error: there is no tool '{call.Name}'.";
        }

        try
        {
            if (call.Exception is not null) throw new ArgumentException($"The arguments could not be read: {call.Exception.Message}");
            var result = await function.InvokeAsync(new AIFunctionArguments(call.Arguments), ct);
            rec.AddActivity(call.CallId, call.Name, true, inputJson, Bounds.Cut(JsonSerializer.Serialize(result, AIJsonUtilities.DefaultOptions), 6000));
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var message = Bounds.Cut(ex.GetBaseException().Message, 400);
            rec.Fail($"{call.Name} failed: {message}");
            rec.AddActivity(call.CallId, call.Name, false, inputJson, JsonSerializer.Serialize(new { error = message }));
            return $"Error: {message}";
        }
    }
}
