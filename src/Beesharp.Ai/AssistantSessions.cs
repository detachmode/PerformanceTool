using Microsoft.Extensions.AI;

namespace PerformanceTool.Ai;

/// <summary>One conversation, kept in memory. <see cref="Messages"/> is the history the model sees (without the system prompt).</summary>
public sealed class ChatSession(string id)
{
    private const int MaxMessages = 80;
    private const int KeepMessages = 50;

    public string Id { get; } = id;
    public List<ChatMessage> Messages { get; } = [];
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
    /// <summary>One turn at a time per conversation.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    public void Touch() => LastUsedUtc = DateTime.UtcNow;

    public void RollbackTo(int count)
    {
        if (Messages.Count > count) Messages.RemoveRange(count, Messages.Count - count);
    }

    /// <summary>Drops the oldest messages once the history is long, and always starts again at a user message so no tool result is left without its call.</summary>
    public void Trim()
    {
        Touch();
        if (Messages.Count <= MaxMessages) return;
        var drop = Messages.Count - KeepMessages;
        while (drop < Messages.Count && Messages[drop].Role != ChatRole.User) drop++;
        Messages.RemoveRange(0, Math.Min(drop, Messages.Count));
    }
}

/// <summary>Bounded in-memory store of conversations: at most <see cref="AssistantOptions.MaxSessions"/>, and idle ones expire.</summary>
public sealed class AssistantSessions(AssistantOptions options)
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, ChatSession> byId = new(StringComparer.Ordinal);

    public int Count { get { lock (sync) return byId.Count; } }

    /// <summary>The conversation with this id, or a new one (with a new id) when there is none, e.g. because it expired.</summary>
    public ChatSession GetOrCreate(string? id)
    {
        lock (sync)
        {
            Purge();
            if (id is not null && byId.TryGetValue(id, out var existing)) return existing;
            while (byId.Count >= Math.Max(1, options.MaxSessions))
            {
                var oldest = byId.Values.Where(s => s.Gate.CurrentCount > 0).OrderBy(s => s.LastUsedUtc).FirstOrDefault() ?? byId.Values.OrderBy(s => s.LastUsedUtc).First();
                byId.Remove(oldest.Id);
            }
            var session = new ChatSession(Guid.NewGuid().ToString("n"));
            byId[session.Id] = session;
            return session;
        }
    }

    public bool Remove(string id)
    {
        lock (sync) return byId.Remove(id);
    }

    private void Purge()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(1, options.SessionIdleMinutes));
        foreach (var stale in byId.Values.Where(s => s.LastUsedUtc < cutoff && s.Gate.CurrentCount > 0).Select(s => s.Id).ToList())
            byId.Remove(stale);
    }
}
