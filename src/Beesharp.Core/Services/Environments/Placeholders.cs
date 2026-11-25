using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beesharp.Services.Environments;

/// <summary>A piece of text: plain, or a <c>{{Key}}</c> placeholder (<see cref="Key"/> set).</summary>
/// <param name="Text">The text content of this segment, either literal text or the raw <c>{{Key}}</c> token.</param>
/// <param name="Key">The referenced environment key when this segment is a placeholder; otherwise <see langword="null"/>.</param>
public readonly record struct PlaceholderPart(string Text, string? Key = null)
{
    /// <summary>Gets a value indicating whether this part represents a placeholder rather than plain text.</summary>
    public bool IsPlaceholder => Key is not null;
}

/// <summary>
/// Finds and fills <c>{{Key}}</c> placeholders (Bruno syntax, no leading dollar sign) in parameter values (brain 0033). This is separate from
/// <c>${env:NAME}</c>, which reads an operating-system environment variable for secret fields and keeps working as before.
/// <list type="bullet">
/// <item>Whitespace inside the braces is allowed: <c>{{ Key }}</c>.</item>
/// <item>An unknown key is left as it is and reported, so the UI can warn and the runner can refuse to start.</item>
/// <item>A backslash escapes: <c>\{{Key}}</c> stays the literal text <c>{{Key}}</c>.</item>
/// <item>Values are inserted as they are and never scanned again, so a value containing <c>{{Other}}</c> is not expanded a second time.</item>
/// </list>
/// </summary>
public static partial class Placeholders
{
    [GeneratedRegex(@"(?<esc>\\)?\{\{\s*(?<key>[A-Za-z_][A-Za-z0-9_.\-]*)\s*\}\}")]
    private static partial Regex Pattern();

    /// <summary>True when the text contains at least one (unescaped) placeholder.</summary>
    /// <param name="text">The text to inspect.</param>
    /// <returns><see langword="true"/> if <paramref name="text"/> contains at least one unescaped placeholder; otherwise <see langword="false"/>.</returns>
    public static bool HasPlaceholder(string? text) =>
        !string.IsNullOrEmpty(text) && Pattern().Matches(text).Any(m => !m.Groups["esc"].Success);

    /// <summary>The distinct keys used in the text, in order of first use.</summary>
    /// <param name="text">The text to scan for placeholder keys.</param>
    /// <returns>The distinct placeholder keys found in <paramref name="text"/>, ordered by first appearance.</returns>
    public static IReadOnlyList<string> Keys(string? text) => string.IsNullOrEmpty(text) ? [] :
        Pattern().Matches(text).Where(m => !m.Groups["esc"].Success).Select(m => m.Groups["key"].Value).Distinct().ToList();

    /// <summary>Splits the text into plain and placeholder parts, for highlighting. Escaped placeholders are plain text without the backslash.</summary>
    /// <param name="text">The text to split.</param>
    /// <returns>An ordered list of <see cref="PlaceholderPart"/> segments representing <paramref name="text"/>.</returns>
    public static IReadOnlyList<PlaceholderPart> Split(string? text)
    {
        var parts = new List<PlaceholderPart>();
        if (string.IsNullOrEmpty(text)) return parts;
        var plain = new StringBuilder();
        var pos = 0;
        foreach (Match m in Pattern().Matches(text))
        {
            plain.Append(text, pos, m.Index - pos);
            pos = m.Index + m.Length;
            if (m.Groups["esc"].Success) { plain.Append(m.Value, 1, m.Length - 1); continue; }
            if (plain.Length > 0) { parts.Add(new(plain.ToString())); plain.Clear(); }
            parts.Add(new(m.Value, m.Groups["key"].Value));
        }
        plain.Append(text, pos, text.Length - pos);
        if (plain.Length > 0) parts.Add(new(plain.ToString()));
        return parts;
    }

    /// <summary>Replaces known placeholders. Unknown keys stay as written and are added to <paramref name="unknown"/> (once each).</summary>
    /// <param name="text">The input text containing optional placeholders.</param>
    /// <param name="values">The environment key-value map used to resolve placeholders, or <see langword="null"/> if none is active.</param>
    /// <param name="unknown">An optional collection that receives each distinct unresolved placeholder key.</param>
    /// <returns>The text with known placeholders replaced and escaped placeholders unescaped.</returns>
    public static string Resolve(string? text, IReadOnlyDictionary<string, string>? values, ICollection<string>? unknown = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return Pattern().Replace(text, m =>
        {
            if (m.Groups["esc"].Success) return m.Value[1..];
            var key = m.Groups["key"].Value;
            if (values is not null && values.TryGetValue(key, out var v)) return v;
            if (unknown is not null && !unknown.Contains(key)) unknown.Add(key);
            return m.Value;
        });
    }

    /// <summary>
    /// Returns a copy of stored parameter values with every string (also inside lists, groups and key-value maps) resolved. Property names stay
    /// as they are. The input is not changed, because the same plan is shown in the editor.
    /// </summary>
    /// <param name="parameters">The parameter JSON object to resolve.</param>
    /// <param name="values">The environment key-value map used to resolve placeholders, or <see langword="null"/> if none is active.</param>
    /// <param name="unknown">An optional collection that receives each distinct unresolved placeholder key.</param>
    /// <returns>A cloned <see cref="JsonObject"/> with all string values resolved.</returns>
    public static JsonObject ResolveJson(JsonObject parameters, IReadOnlyDictionary<string, string>? values, ICollection<string>? unknown = null) =>
        (JsonObject)ResolveNode(parameters, values, unknown)!;

    private static JsonNode? ResolveNode(JsonNode? node, IReadOnlyDictionary<string, string>? values, ICollection<string>? unknown)
    {
        switch (node)
        {
            case JsonObject o:
                var copy = new JsonObject();
                foreach (var (k, v) in o) copy[k] = ResolveNode(v, values, unknown);
                return copy;
            case JsonArray a:
                return new JsonArray(a.Select(x => ResolveNode(x, values, unknown)).ToArray());
            case JsonValue v when v.TryGetValue<string>(out var s):
                return JsonValue.Create(Resolve(s, values, unknown));
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>The keys used anywhere in the stored parameter values.</summary>
    /// <param name="node">The JSON node tree to scan for placeholders.</param>
    /// <returns>The distinct placeholder keys found anywhere in string values within <paramref name="node"/>.</returns>
    public static IReadOnlyList<string> KeysIn(JsonNode? node)
    {
        var keys = new List<string>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o: foreach (var (_, v) in o) Walk(v); break;
                case JsonArray a: foreach (var x in a) Walk(x); break;
                case JsonValue v when v.TryGetValue<string>(out var s):
                    foreach (var k in Keys(s)) if (!keys.Contains(k)) keys.Add(k);
                    break;
            }
        }
        Walk(node);
        return keys;
    }
}
