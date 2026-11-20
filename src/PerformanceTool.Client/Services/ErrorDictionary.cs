namespace PerformanceTool.Client.Services;

/// <summary>
/// Interning dictionary for error texts (brain 0028): every distinct text (error message, exception text, repeated long log message)
/// is stored once and referenced by an integer id from actors, calls and log entries. Id <see cref="None"/> means "no text".
/// One instance per run (it lives in <see cref="RunStore"/>), so ids are only meaningful together with their dictionary.
/// </summary>
public sealed class ErrorDictionary
{
    public const int None = 0;

    private readonly Dictionary<string, int> ids = new(StringComparer.Ordinal);
    private readonly List<string> texts = [""];

    /// <summary>Number of distinct texts stored.</summary>
    public int Count => texts.Count - 1;

    /// <summary>The id of <paramref name="text"/>; the same text always gives the same id. Null gives <see cref="None"/>.</summary>
    public int Intern(string? text)
    {
        if (text is null) return None;
        if (ids.TryGetValue(text, out var id)) return id;
        id = texts.Count;
        texts.Add(text);
        ids[text] = id;
        return id;
    }

    /// <summary>The text of an id, or null for <see cref="None"/> or an unknown id.</summary>
    public string? Resolve(int id) => id > 0 && id < texts.Count ? texts[id] : null;
}
