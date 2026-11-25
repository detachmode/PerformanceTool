using System.Text;
using System.Text.RegularExpressions;

namespace Beesharp.Services.Scenarios;

/// <summary>Result of normalizing an HTTP call for endpoint auto-detection while preserving its exact raw URL and name.</summary>
public readonly record struct NormalizedHttpCall(string Endpoint, string RawName, string Method, string? Host, string Url);

/// <summary>
/// Automatically normalizes HTTP URLs and call names into stable endpoint keys (brain 0060):
/// collapses numeric IDs, GUIDs, hex tokens, prefixed IDs (<c>SKU-001</c>, <c>cart-412</c>), dates, emails, and
/// strips query strings while keeping API versions (<c>v1</c>, <c>v2</c>) intact.
/// </summary>
public static partial class EndpointNormalizer
{
    private static readonly string[] HttpVerbs = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];

    public static NormalizedHttpCall NormalizeHttp(HttpMethod method, Uri? uri)
    {
        var methodStr = method.Method.ToUpperInvariant();
        var url = uri?.ToString() ?? "";
        var rawPathAndQuery = uri is null
            ? "/"
            : uri.IsAbsoluteUri
                ? (string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery)
                : (string.IsNullOrEmpty(uri.OriginalString) ? "/" : uri.OriginalString);
        var host = uri is { IsAbsoluteUri: true }
            ? (uri.IsDefaultPort ? uri.Host : $"{uri.Host}:{uri.Port}")
            : null;
        var path = uri is null
            ? "/"
            : uri.IsAbsoluteUri
                ? uri.AbsolutePath
                : StripQueryAndFragment(uri.OriginalString);

        var normPath = NormalizePath(path);
        return new NormalizedHttpCall($"{methodStr} {normPath}", $"{methodStr} {rawPathAndQuery}", methodStr, host, url);
    }

    /// <summary>
    /// Normalizes a recorded call name (e.g. from fake data or <c>MeasureCallAsync</c>).
    /// HTTP-like names (<c>GET /products/1042?q=shoe</c> or <c>GET /products/{id} · 3</c>) normalize their path and drop query values;
    /// custom non-HTTP names (<c>Place order</c>) stay intact apart from trailing <c> · N</c> repetition suffixes.
    /// </summary>
    public static string NormalizeCallName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        trimmed = TrailingRepRegex().Replace(trimmed, "").TrimEnd();

        var space = trimmed.IndexOf(' ');
        if (space <= 0) return trimmed;

        var verb = trimmed[..space].ToUpperInvariant();
        if (Array.IndexOf(HttpVerbs, verb) < 0) return trimmed;

        var rest = trimmed[(space + 1)..].Trim();
        if (rest.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || rest.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var ws = rest.IndexOfAny([' ', '\t']);
            var urlPart = ws > 0 ? rest[..ws] : rest;
            if (Uri.TryCreate(urlPart, UriKind.Absolute, out var abs))
                return NormalizeHttp(new HttpMethod(verb), abs).Endpoint;
        }

        if (rest.StartsWith('/'))
        {
            var ws = rest.IndexOfAny([' ', '\t']);
            var pathPart = ws > 0 ? rest[..ws] : rest;
            return $"{verb} {NormalizePath(StripQueryAndFragment(pathPart))}";
        }

        return $"{verb} {rest}";
    }

    public static string? TryExtractMethod(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        var space = trimmed.IndexOf(' ');
        if (space <= 0) return null;
        var verb = trimmed[..space].ToUpperInvariant();
        return Array.IndexOf(HttpVerbs, verb) >= 0 ? verb : null;
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        var stripped = StripQueryAndFragment(path.Trim());
        var parts = stripped.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return "/";

        var sb = new StringBuilder(stripped.Length + 8);
        foreach (var rawSeg in parts)
        {
            var seg = Uri.UnescapeDataString(rawSeg);
            sb.Append('/');
            sb.Append(IsDynamicSegment(seg) ? "{id}" : rawSeg);
        }
        return sb.ToString();
    }

    private static string StripQueryAndFragment(string s)
    {
        var cut = s.IndexOfAny(['?', '#']);
        return cut >= 0 ? s[..cut] : s;
    }

    internal static bool IsApiVersion(string seg) => VersionRegex().IsMatch(seg);

    internal static bool IsPlaceholder(string seg) =>
        (seg.StartsWith('{') && seg.EndsWith('}')) || seg.StartsWith(':');

    private static bool IsDynamicSegment(string seg)
    {
        if (string.IsNullOrEmpty(seg) || IsPlaceholder(seg) || IsApiVersion(seg)) return false;

        // 1. Pure digits (e.g. 42, 88231)
        if (DigitsRegex().IsMatch(seg)) return true;

        // 2. GUID / UUID
        if (Guid.TryParse(seg, out _)) return true;

        // 3. ISO date or timestamp (2026-11-24, 2026-11-24T10:15:00Z)
        if (IsoDateRegex().IsMatch(seg)) return true;

        // 4. Long hex string with at least one digit (ObjectId, SHA hashes, hex IDs)
        if (seg.Length >= 8 && HexRegex().IsMatch(seg) && ContainsDigit(seg)) return true;

        // 5. Prefixed ID: letters + (- or _) + digits/guid/hex (e.g. SKU-001, cart-412, ord_9f8a2b)
        if (PrefixedIdRegex().IsMatch(seg)) return true;

        // 6. Email address
        var at = seg.IndexOf('@');
        if (at > 0 && seg.IndexOf('.', at + 1) > at + 1) return true;

        // 7. Long high-entropy mixed alphanumeric token (not a 3+-word lowercase kebab-case slug)
        if (seg.Length >= 16 && ContainsLetter(seg) && ContainsDigit(seg) && !MultiWordKebabSlugRegex().IsMatch(seg))
            return true;

        return false;
    }

    private static bool ContainsDigit(string s)
    {
        foreach (var c in s) if (char.IsAsciiDigit(c)) return true;
        return false;
    }

    private static bool ContainsLetter(string s)
    {
        foreach (var c in s) if (char.IsAsciiLetter(c)) return true;
        return false;
    }

    [GeneratedRegex(@"\s+·\s+\d+$")]
    private static partial Regex TrailingRepRegex();

    [GeneratedRegex(@"^v\d+(?:\.\d+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^\d+$")]
    private static partial Regex DigitsRegex();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}(?:[T_ ].+)?$")]
    private static partial Regex IsoDateRegex();

    [GeneratedRegex(@"^[0-9a-fA-F]{8,}$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^[a-zA-Z]{1,12}[-_](?:\d+|[0-9a-fA-F]{4,})$")]
    private static partial Regex PrefixedIdRegex();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+){2,}$")]
    private static partial Regex MultiWordKebabSlugRegex();
}

/// <summary>
/// Post-processes normalized endpoint keys across a run (brain 0060):
/// 1) includes host only when multiple hosts exist in the run,
/// 2) adaptively collapses high-cardinality slug segments (e.g. <c>/catalog/wireless-headphones</c>, <c>/catalog/running-shoes</c>)
/// into <c>{id}</c> when a path position sees more than <see cref="DefaultAdaptiveCollapseThreshold"/> distinct values.
/// </summary>
public static class EndpointGrouper
{
    public const int DefaultAdaptiveCollapseThreshold = 10;

    private static readonly HashSet<string> HttpVerbs = new(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"], StringComparer.OrdinalIgnoreCase);

    private sealed class EntryState(string rawEndpoint, string? host, string? method, string[]? segments)
    {
        public string RawEndpoint { get; } = rawEndpoint;
        public string? Host { get; } = host;
        public string? Method { get; } = method;
        public string[]? Segments { get; } = segments;
        public bool IsHttp => Method is not null && Segments is not null;
    }

    public static Dictionary<(string RawEndpoint, string? Host), string> BuildMap(
        IEnumerable<(string RawEndpoint, string? Host)> observed,
        int adaptiveThreshold = DefaultAdaptiveCollapseThreshold)
    {
        var unique = new HashSet<(string RawEndpoint, string? Host)>();
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in observed)
        {
            var raw = string.IsNullOrWhiteSpace(item.RawEndpoint) ? "GET /" : item.RawEndpoint.Trim();
            var host = string.IsNullOrWhiteSpace(item.Host) ? null : item.Host.Trim();
            unique.Add((raw, host));
            if (host is not null) hosts.Add(host);
        }

        var multiHost = hosts.Count > 1;
        var states = new List<EntryState>(unique.Count);
        var customNameHosts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (raw, host) in unique)
        {
            var (method, segments) = TryParseHttpEndpoint(raw);
            states.Add(new EntryState(raw, host, method, segments));
            if (method is null && host is not null)
            {
                if (!customNameHosts.TryGetValue(raw, out var set))
                    customNameHosts[raw] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(host);
            }
        }

        var httpStates = states.Where(s => s.IsHttp).ToList();
        if (httpStates.Count > 0 && adaptiveThreshold >= 1)
        {
            // Two passes handle nested high-cardinality paths (e.g. /orgs/{slug}/projects/{slug})
            for (var pass = 0; pass < 2; pass++)
            {
                var changed = false;
                foreach (var group in httpStates.GroupBy(s => (Method: s.Method!, Host: multiHost ? s.Host ?? "" : "", Len: s.Segments!.Length)))
                {
                    var len = group.Key.Len;
                    if (len == 0) continue;
                    var items = group.ToList();
                    if (items.Count <= adaptiveThreshold) continue;

                    for (var pos = 0; pos < len; pos++)
                    {
                        var byContext = new Dictionary<string, List<EntryState>>(StringComparer.OrdinalIgnoreCase);
                        foreach (var st in items)
                        {
                            var ctxKey = ContextKey(st.Segments!, pos);
                            if (!byContext.TryGetValue(ctxKey, out var list))
                                byContext[ctxKey] = list = [];
                            list.Add(st);
                        }

                        foreach (var bucket in byContext.Values)
                        {
                            var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var st in bucket)
                            {
                                var val = st.Segments![pos];
                                if (!EndpointNormalizer.IsPlaceholder(val) && !EndpointNormalizer.IsApiVersion(val))
                                    distinct.Add(val);
                            }

                            if (distinct.Count > adaptiveThreshold)
                            {
                                foreach (var st in bucket)
                                {
                                    if (st.Segments![pos] != "{id}" && !EndpointNormalizer.IsPlaceholder(st.Segments[pos]))
                                    {
                                        st.Segments[pos] = "{id}";
                                        changed = true;
                                    }
                                }
                            }
                        }
                    }
                }
                if (!changed) break;
            }
        }

        var map = new Dictionary<(string RawEndpoint, string? Host), string>(unique.Count);
        foreach (var st in states)
        {
            if (st.IsHttp)
            {
                var path = st.Segments!.Length == 0 ? "/" : "/" + string.Join('/', st.Segments);
                var canonical = multiHost && st.Host is not null
                    ? $"{st.Method} {st.Host}{path}"
                    : $"{st.Method} {path}";
                map[(st.RawEndpoint, st.Host)] = canonical;
            }
            else
            {
                var includeHost = multiHost && st.Host is not null
                    && customNameHosts.TryGetValue(st.RawEndpoint, out var hs) && hs.Count > 1;
                map[(st.RawEndpoint, st.Host)] = includeHost ? $"{st.RawEndpoint} ({st.Host})" : st.RawEndpoint;
            }
        }
        return map;
    }

    private static string ContextKey(string[] segments, int skipPos)
    {
        if (segments.Length <= 1) return "";
        var sb = new StringBuilder();
        for (var i = 0; i < segments.Length; i++)
        {
            if (i == skipPos) continue;
            if (sb.Length > 0) sb.Append('\0');
            sb.Append(segments[i]);
        }
        return sb.ToString();
    }

    private static (string? Method, string[]? Segments) TryParseHttpEndpoint(string raw)
    {
        var space = raw.IndexOf(' ');
        if (space <= 0) return (null, null);
        var verb = raw[..space].ToUpperInvariant();
        if (!HttpVerbs.Contains(verb)) return (null, null);
        var rest = raw[(space + 1)..].Trim();
        if (!rest.StartsWith('/')) return (null, null);
        var segments = rest.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return (verb, segments);
    }
}
