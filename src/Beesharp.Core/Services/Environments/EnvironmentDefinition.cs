using System.Collections;
using System.Text.RegularExpressions;

namespace Beesharp.Services.Environments;

/// <summary>One key-value pair of an environment. Values may be credentials: what goes in is up to the user (brain 0033).</summary>
public sealed class EnvironmentVariable
{
    /// <summary>Initializes an empty <see cref="EnvironmentVariable"/>.</summary>
    public EnvironmentVariable() { }

    /// <summary>Initializes an <see cref="EnvironmentVariable"/> with the specified key and value.</summary>
    /// <param name="key">The variable key.</param>
    /// <param name="value">The variable value.</param>
    public EnvironmentVariable(string key, string value) { Key = key; Value = value; }

    /// <summary>Gets or sets the variable key.</summary>
    public string Key { get; set; } = "";

    /// <summary>Gets or sets the variable value.</summary>
    public string Value { get; set; } = "";
}

/// <summary>
/// A named set of key-value pairs (base URL, tenant, user, token, ...) that scenarios run against (brain 0033). Stored as one JSON file per
/// environment. The order of the pairs is kept, because it is the order the user sees them in.
/// </summary>
public sealed partial class EnvironmentDefinition
{
    /// <summary>Gets or sets the display name of the environment.</summary>
    public string Name { get; set; } = "";

    /// <summary>Gets or sets the ordered list of key-value variables defined in this environment.</summary>
    public List<EnvironmentVariable> Variables { get; set; } = [];

    /// <summary>Creates a deep copy of this <see cref="EnvironmentDefinition"/> and its variables.</summary>
    /// <returns>A new <see cref="EnvironmentDefinition"/> with the same <see cref="Name"/> and copied <see cref="Variables"/>.</returns>
    public EnvironmentDefinition Clone() => new()
    {
        Name = Name,
        Variables = Variables.Select(v => new EnvironmentVariable(v.Key, v.Value)).ToList(),
    };

    /// <summary>Keys are letters, digits, '_', '.' and '-', and start with a letter or '_' (so <c>{{Key}}</c> is unambiguous).</summary>
    /// <param name="key">The variable key to validate.</param>
    /// <returns><see langword="true"/> if <paramref name="key"/> is non-null and matches the allowed key pattern; otherwise <see langword="false"/>.</returns>
    public static bool IsValidKey(string? key) => key is not null && ValidKey().IsMatch(key);

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*$")]
    private static partial Regex ValidKey();

    /// <summary>Problems that stop an environment from being saved: empty name, bad or duplicate keys. Blank rows (no key, no value) are ignored.</summary>
    /// <returns>A list of human-readable validation error messages; empty when the environment is valid.</returns>
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
    /// <returns>A read-only <see cref="EnvironmentValues"/> snapshot built from the non-blank keys.</returns>
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
    /// <summary>An empty <see cref="EnvironmentValues"/> instance used when no environment is active.</summary>
    public static readonly EnvironmentValues None = new("", new Dictionary<string, string>());

    private readonly IReadOnlyDictionary<string, string> map;

    /// <summary>Initializes a new <see cref="EnvironmentValues"/> view.</summary>
    /// <param name="name">The name of the environment, or an empty string when none is active.</param>
    /// <param name="map">The read-only dictionary of variable keys to values.</param>
    public EnvironmentValues(string name, IReadOnlyDictionary<string, string> map) { Name = name; this.map = map; }

    /// <summary>Name of the environment, empty when none is active.</summary>
    public string Name { get; }

    /// <summary>Gets the value associated with the specified <paramref name="key"/>.</summary>
    /// <param name="key">The variable key to look up.</param>
    /// <returns>The value for <paramref name="key"/>.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when <paramref name="key"/> does not exist in the active environment.</exception>
    public string this[string key] => map.TryGetValue(key, out var v) ? v
        : throw new KeyNotFoundException(Name.Length == 0 ? $"No environment is active, so there is no value for '{key}'." : $"Environment '{Name}' has no key '{key}'.");

    /// <summary>The value, or <paramref name="fallback"/> when the key is missing.</summary>
    /// <param name="key">The variable key to look up.</param>
    /// <param name="fallback">The value to return if <paramref name="key"/> is not present.</param>
    /// <returns>The value for <paramref name="key"/> if present; otherwise <paramref name="fallback"/>.</returns>
    public string Get(string key, string fallback = "") => map.TryGetValue(key, out var v) ? v : fallback;

    /// <summary>Gets all variable keys defined in this environment.</summary>
    public IEnumerable<string> Keys => map.Keys;

    /// <summary>Gets all variable values defined in this environment.</summary>
    public IEnumerable<string> Values => map.Values;

    /// <summary>Gets the number of variables in this environment.</summary>
    public int Count => map.Count;

    /// <summary>Determines whether this environment contains the specified <paramref name="key"/>.</summary>
    /// <param name="key">The variable key to locate.</param>
    /// <returns><see langword="true"/> if <paramref name="key"/> is present; otherwise <see langword="false"/>.</returns>
    public bool ContainsKey(string key) => map.ContainsKey(key);

    /// <summary>Attempts to retrieve the value associated with the specified <paramref name="key"/>.</summary>
    /// <param name="key">The variable key to locate.</param>
    /// <param name="value">When this method returns, contains the value associated with <paramref name="key"/>, if found; otherwise the default value.</param>
    /// <returns><see langword="true"/> if <paramref name="key"/> was found; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue(string key, out string value) => map.TryGetValue(key, out value!);

    /// <summary>Returns an enumerator that iterates through the environment key-value pairs.</summary>
    /// <returns>An enumerator over the key-value pairs in this environment.</returns>
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => map.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
