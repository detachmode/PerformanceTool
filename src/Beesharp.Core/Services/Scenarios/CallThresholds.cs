using System.Text.RegularExpressions;

namespace Beesharp.Services.Scenarios;

/// <summary>
/// Per-scenario and per-endpoint slow-call duration thresholds configured by a scenario author (brain 0060).
/// Precedence when evaluating a call: inline per-call threshold → per-endpoint rule here → scenario default here → automatic <c>2× typical</c>.
/// </summary>
public sealed class CallThresholds
{
    public static readonly CallThresholds None = new();

    private readonly List<(string Pattern, Regex? Wildcard, string? NormalizedPattern, double ThresholdMs)> rules;

    public TimeSpan? Default { get; }
    public IReadOnlyDictionary<string, TimeSpan> Rules { get; }

    public CallThresholds(TimeSpan? defaultThreshold = null, IReadOnlyDictionary<string, TimeSpan>? rules = null)
    {
        if (defaultThreshold is { } d && d <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(defaultThreshold), "Slow threshold must be greater than zero.");

        Default = defaultThreshold;
        var dict = new Dictionary<string, TimeSpan>(StringComparer.OrdinalIgnoreCase);
        var compiled = new List<(string, Regex?, string?, double)>();

        if (rules is not null)
        {
            foreach (var (key, value) in rules)
            {
                if (string.IsNullOrWhiteSpace(key))
                    throw new ArgumentException("Endpoint pattern cannot be empty.", nameof(rules));
                if (value <= TimeSpan.Zero)
                    throw new ArgumentOutOfRangeException(nameof(rules), $"Slow threshold for '{key}' must be greater than zero.");

                var trimmed = key.Trim();
                dict[trimmed] = value;

                Regex? wildcard = trimmed.Contains('*')
                    ? new Regex("^" + Regex.Escape(trimmed).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
                    : null;
                var normalized = wildcard is null ? EndpointNormalizer.NormalizeCallName(trimmed) : null;
                compiled.Add((trimmed, wildcard, normalized, value.TotalMilliseconds));
            }
        }

        Rules = dict;
        this.rules = compiled;
    }

    public CallThresholds WithDefault(TimeSpan threshold) => new(threshold, Rules);

    public CallThresholds For(string endpointPattern, TimeSpan threshold)
    {
        if (string.IsNullOrWhiteSpace(endpointPattern))
            throw new ArgumentException("Endpoint pattern cannot be empty.", nameof(endpointPattern));
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold), "Slow threshold must be greater than zero.");

        var next = new Dictionary<string, TimeSpan>(Rules, StringComparer.OrdinalIgnoreCase)
        {
            [endpointPattern.Trim()] = threshold,
        };
        return new CallThresholds(Default, next);
    }

    /// <summary>Resolves the threshold in milliseconds for an endpoint/raw call name, or null when only the automatic <c>2× typical</c> rule applies.</summary>
    public double? ResolveMs(string? endpoint, string? rawName = null, string? method = null)
    {
        if (rules.Count > 0)
        {
            var normEndpoint = string.IsNullOrWhiteSpace(endpoint) ? null : EndpointNormalizer.NormalizeCallName(endpoint);
            var pathOnly = ExtractPathOnly(endpoint);
            var normPathOnly = ExtractPathOnly(normEndpoint);
            var rawPathOnly = ExtractPathOnly(rawName);

            // 1. Exact match (against endpoint, normalized endpoint, rawName, or normalized rule pattern)
            foreach (var (pattern, wildcard, normalized, ms) in rules)
            {
                if (wildcard is not null) continue;
                if (MatchesExact(pattern, endpoint, normEndpoint, rawName, pathOnly, normPathOnly, rawPathOnly)
                    || (normalized is not null && MatchesExact(normalized, endpoint, normEndpoint, rawName, pathOnly, normPathOnly, rawPathOnly)))
                    return ms;
            }

            // 2. Wildcard match (* matches any sequence)
            foreach (var (_, wildcard, _, ms) in rules)
            {
                if (wildcard is null) continue;
                if ((endpoint is not null && wildcard.IsMatch(endpoint))
                    || (normEndpoint is not null && wildcard.IsMatch(normEndpoint))
                    || (rawName is not null && wildcard.IsMatch(rawName))
                    || (pathOnly is not null && wildcard.IsMatch(pathOnly))
                    || (normPathOnly is not null && wildcard.IsMatch(normPathOnly))
                    || (rawPathOnly is not null && wildcard.IsMatch(rawPathOnly)))
                    return ms;
            }
        }

        return Default?.TotalMilliseconds;
    }

    private static bool MatchesExact(string pattern, string? endpoint, string? normEndpoint, string? rawName, string? pathOnly, string? normPathOnly, string? rawPathOnly) =>
        (endpoint is not null && string.Equals(pattern, endpoint, StringComparison.OrdinalIgnoreCase))
        || (normEndpoint is not null && string.Equals(pattern, normEndpoint, StringComparison.OrdinalIgnoreCase))
        || (rawName is not null && string.Equals(pattern, rawName, StringComparison.OrdinalIgnoreCase))
        || (pathOnly is not null && string.Equals(pattern, pathOnly, StringComparison.OrdinalIgnoreCase))
        || (normPathOnly is not null && string.Equals(pattern, normPathOnly, StringComparison.OrdinalIgnoreCase))
        || (rawPathOnly is not null && string.Equals(pattern, rawPathOnly, StringComparison.OrdinalIgnoreCase));

    private static string? ExtractPathOnly(string? call)
    {
        if (string.IsNullOrWhiteSpace(call)) return null;
        var space = call.IndexOf(' ');
        if (space <= 0) return null;
        var rest = call[(space + 1)..].Trim();
        return rest.StartsWith('/') ? rest : null;
    }
}
