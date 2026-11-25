using Microsoft.Extensions.AI;

namespace Beesharp.Ai;

/// <summary>One conversation, kept in memory. <see cref="Messages"/> is the history the model sees (without the system prompt).</summary>
/// <param name="id">Unique identifier assigned to this conversation session.</param>
public sealed class ChatSession(string id)
{
    private const int MaxMessages = 80;
    private const int KeepMessages = 50;

    /// <summary>Gets the unique identifier of this conversation session.</summary>
    public string Id { get; } = id;
    /// <summary>Gets the mutable list of messages exchanged in this session (excluding the system prompt).</summary>
    public List<ChatMessage> Messages { get; } = [];
    /// <summary>Gets the UTC timestamp when this session was last accessed or updated.</summary>
    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;
    /// <summary>One turn at a time per conversation.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>Updates <see cref="LastUsedUtc"/> to the current UTC time.</summary>
    public void Touch() => LastUsedUtc = DateTime.UtcNow;

    /// <summary>Removes any messages appended after <paramref name="count"/>, rolling back an interrupted or failed turn.</summary>
    /// <param name="count">The message count to restore <see cref="Messages"/> back to.</param>
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
/// <param name="options">Assistant options controlling session capacity and idle expiration.</param>
public sealed class AssistantSessions(AssistantOptions options)
{
    private readonly Lock sync = new();
    private readonly Dictionary<string, ChatSession> byId = new(StringComparer.Ordinal);

    /// <summary>Gets the number of active sessions currently stored in memory.</summary>
    public int Count { get { lock (sync) return byId.Count; } }

    /// <summary>The conversation with this id, or a new one (with a new id) when there is none, e.g. because it expired.</summary>
    /// <param name="id">Optional existing session identifier; <see langword="null"/> or unknown ids create a new session.</param>
    /// <returns>The existing or newly created <see cref="ChatSession"/>.</returns>
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

    /// <summary>Removes the session with the specified identifier from memory.</summary>
    /// <param name="id">Identifier of the session to remove.</param>
    /// <returns><see langword="true"/> if the session was found and removed; otherwise, <see langword="false"/>.</returns>
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
