using System.Net.Http.Json;
using System.Text.Json;
using PerformanceTool.Ai.Contracts;
using PerformanceTool.Client.Services;

namespace PerformanceTool.Client.Components.Assistant;

/// <summary>Raised when the server refuses or fails a chat message; the message is meant for the user.</summary>
public sealed class AssistantException(string message) : Exception(message);

// Shapes of the run queries the cards use (GET /api/runs/{id}/summary and /bees). 0-based indexes, like the rest of /api/runs.
public sealed record AssistantEntryDto(int Entry, string Label, int Starts, int Finished, int InFlight, int Failed, int SlowBees, int SlowCalls, int Dropped, double BeesPerMinute);

public sealed record AssistantRunDto(
    string RunId, string State, double ElapsedSeconds, DateTimeOffset StartedUtc, int TotalBees, int FailedBees, int SlowBees, long TotalLogs, string? Error,
    List<AssistantEntryDto> Entries);

public sealed record AssistantBeeDto(int Sequence, int Entry, double StartedAt, double DurationMs, int? StatusCode, long Bytes, string? Error, int SlowCalls)
{
    public string Outcome => Error is not null ? "failed" : SlowCalls > 0 ? "slow" : "ok";
}

public sealed record AssistantBeePageDto(long Total, List<AssistantBeeDto> Items);

/// <summary>Small HTTP client for <c>/api/assistant</c> (the contracts) and the run queries the cards re-query live data with (brain 0047, 0049).</summary>
public sealed class AssistantClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public async Task<AssistantStatusDto?> GetStatusAsync(CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AssistantStatusDto>("api/assistant/status", JsonOpts, ct); }
        catch { return null; } // no server (GitHub Pages) or an old server: no assistant
    }

    public async Task<AssistantTurnDto> ChatAsync(string? sessionId, string message, CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try { resp = await http.PostAsJsonAsync("api/assistant/chat", new AssistantChatRequest(sessionId, message), JsonOpts, ct); }
        catch (HttpRequestException) { throw new AssistantException("The server could not be reached."); }
        if (resp.IsSuccessStatusCode)
            return await resp.Content.ReadFromJsonAsync<AssistantTurnDto>(JsonOpts, ct) ?? throw new AssistantException("The server sent an empty answer.");
        var text = await resp.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { Length: > 0 } msg) throw new AssistantException(msg);
        }
        catch (JsonException) { }
        throw new AssistantException($"The assistant answered with HTTP {(int)resp.StatusCode}.");
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        try { await http.DeleteAsync($"api/assistant/sessions/{Uri.EscapeDataString(sessionId)}"); }
        catch { }
    }

    public async Task<AssistantRunDto?> GetRunAsync(string runId, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AssistantRunDto>($"api/runs/{Uri.EscapeDataString(runId)}/summary", JsonOpts, ct); }
        catch { return null; }
    }

    /// <summary>Bees of a run (0-based entry), failed first, paged. Up to 5000 per request.</summary>
    public async Task<AssistantBeePageDto?> GetBeesAsync(string runId, int? entry, string? outcome, int offset, int limit, CancellationToken ct = default)
    {
        var url = $"api/runs/{Uri.EscapeDataString(runId)}/bees?offset={offset}&limit={limit}"
            + (entry is { } e ? $"&entry={e}" : "") + (string.IsNullOrEmpty(outcome) ? "" : $"&outcome={Uri.EscapeDataString(outcome)}");
        try { return await http.GetFromJsonAsync<AssistantBeePageDto>(url, JsonOpts, ct); }
        catch { return null; }
    }

    /// <summary>Exactly these bees (0-based sequences); unknown ones are left out.</summary>
    public async Task<AssistantBeePageDto?> GetBeesAsync(string runId, IEnumerable<int> sequences, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<AssistantBeePageDto>($"api/runs/{Uri.EscapeDataString(runId)}/bees?sequences={string.Join(',', sequences)}", JsonOpts, ct); }
        catch { return null; }
    }

    public async Task<ServerBeeDetailDto?> GetBeeDetailAsync(string runId, int sequence, CancellationToken ct = default)
    {
        try { return await http.GetFromJsonAsync<ServerBeeDetailDto>($"api/runs/{Uri.EscapeDataString(runId)}/bees/{sequence}", JsonOpts, ct); }
        catch { return null; }
    }

    /// <summary>One page of the log lines of a run with the same filters as the Logs popup (0-based entry and bee).</summary>
    public async Task<ServerLogPageDto?> GetLogsAsync(string runId, ViewQueryDto q, int offset, int limit, CancellationToken ct = default)
    {
        var parts = new List<string> { $"offset={offset}", $"limit={limit}" };
        if (q.Entry is { } e) parts.Add($"entry={e - 1}");
        if (q.Bee is { } b) parts.Add($"bee={b - 1}");
        if (q.FailedBees is true) parts.Add("failed=true");
        if (q.SlowBees is true) parts.Add("slow=true");
        if (q.Levels is { Count: > 0 } lv) parts.Add($"levels={Uri.EscapeDataString(string.Join(',', lv))}");
        if (!string.IsNullOrWhiteSpace(q.Search)) parts.Add($"search={Uri.EscapeDataString(q.Search)}");
        try { return await http.GetFromJsonAsync<ServerLogPageDto>($"api/runs/{Uri.EscapeDataString(runId)}/logs?{string.Join('&', parts)}", JsonOpts, ct); }
        catch { return null; }
    }
}
