using System.Collections;
using System.Text.RegularExpressions;

namespace Beesharp.Services.Environments;

/// <summary>One key-value pair of an environment. Values may be credentials: what goes in is up to the user (brain 0033).</summary>
public sealed class EnvironmentVariable
{
    public EnvironmentVariable() { }
    public EnvironmentVariable(string key, string value) { Key = key; Value = value; }

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// A named set of key-value pairs (base URL, tenant, user, token, ...) that scenarios run against (brain 0033). Stored as one JSON file per
/// environment. The order of the pairs is kept, because it is the order the user sees them in.
/// </summary>
public sealed partial class EnvironmentDefinition
{
    public string Name { get; set; } = "";
    public List<EnvironmentVariable> Variables { get; set; } = [];

    public EnvironmentDefinition Clone() => new()
    {
        Name = Name,
        Variables = Variables.Select(v => new EnvironmentVariable(v.Key, v.Value)).ToList(),
    };

    /// <summary>Keys are letters, digits, '_', '.' and '-', and start with a letter or '_' (so <c>{{Key}}</c> is unambiguous).</summary>
    public static bool IsValidKey(string? key) => key is not null && ValidKey().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
    private static partial Regex ValidKey();

    /// <summary>Problems that stop an environment from being saved: empty name, bad or duplicate keys. Blank rows (no key, no value) are ignored.</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(Name)) errors.Add("The environment needs a name.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in Variables)
        {
            var key = v.Key.Trim();
            if (key.Length == 0) { if (v.Value.Length > 0) errors.Add("A value has no key."); continue; }
            if (!IsValidKey(key)) errors.Add($"'{key}' is not a valid key: use letters, digits, _ . and -.");
            else if (!seen.Add(key)) errors.Add($"'{key}' appears more than once.");
        }
        return errors;
    }

    /// <summary>The values as scenarios read them. If a key appears twice, the last one wins.</summary>
    public EnvironmentValues ToValues()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in Variables)
            if (v.Key.Trim() is { Length: > 0 } key) map[key] = v.Value;
        return new EnvironmentValues(Name, map);
    }
}

/// <summary>
/// Read-only view of the active environment for scenario code: <c>context.Environment["BaseUrl"]</c>. A missing key throws with a message
/// that names the key and the environment, so a typo fails the bee with a readable error instead of silently using an empty value.
/// </summary>
public sealed class EnvironmentValues : IReadOnlyDictionary<string, string>
{
    public static readonly EnvironmentValues None = new("", new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> map;

    public EnvironmentValues(string name, IReadOnlyDictionary<string, string> map) { Name = name; this.map = map; }

    /// <summary>Name of the environment, empty when none is active.</summary>
    public string Name { get; }

    public string this[string key] => map.TryGetValue(key, out var v) ? v
        : throw new KeyNotFoundException(Name.Length == 0 ? $"No environment is active, so there is no value for '{key}'." : $"Environment '{Name}' has no key '{key}'.");

    /// <summary>The value, or <paramref name="fallback"/> when the key is missing.</summary>
    public string Get(string key, string fallback = "") => map.TryGetValue(key, out var v) ? v : fallback;

    public IEnumerable<string> Keys => map.Keys;
    public IEnumerable<string> Values => map.Values;
    public int Count => map.Count;
    public bool ContainsKey(string key) => map.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => map.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => map.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
