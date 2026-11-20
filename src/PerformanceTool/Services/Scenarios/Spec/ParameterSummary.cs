using System.Globalization;
using System.Text.Json.Nodes;

namespace PerformanceTool.Services.Scenarios.Spec;

/// <summary>One read-only line of a parameter summary. <see cref="Depth"/> is the nesting level of a group.</summary>
public sealed record SummaryLine(string Label, string Value, int Depth = 0, bool Heading = false);

/// <summary>
/// Read-only, human-readable view of stored parameter values, generated from the same <see cref="ParameterSpec"/>
/// as the editor (docs/brain/0007) so it never needs per-scenario code. Secrets are masked.
/// </summary>
public static class ParameterSummary
{
    public static List<SummaryLine> Describe(ParameterSpec spec, JsonObject values)
    {
        var lines = new List<SummaryLine>();
        Add(spec, values, 0, lines);
        return lines;
    }

    private static void Add(ParameterSpec spec, JsonObject values, int depth, List<SummaryLine> lines)
    {
        foreach (var f in spec.Fields)
        {
            var node = values[f.JsonName];
            if (f.Kind == FieldKind.Group && f.Children is { } children)
            {
                lines.Add(new SummaryLine(f.Label, "", depth, Heading: true));
                Add(children, node as JsonObject ?? new JsonObject(), depth + 1, lines);
                continue;
            }
            lines.Add(new SummaryLine(f.Label, Format(f, node), depth));
        }
    }

    private static string Format(FieldSpec f, JsonNode? node)
    {
        if (node is null) return "—";
        switch (f.Kind)
        {
            case FieldKind.Secret:
                return string.IsNullOrEmpty(Text(node)) ? "—" : "••••••";
            case FieldKind.Toggle:
                return node is JsonValue v && v.TryGetValue<bool>(out var b) && b ? "on" : "off";
            case FieldKind.Choice:
                var raw = Text(node);
                return f.Options.FirstOrDefault(o => o.Value == raw)?.Label ?? raw;
            case FieldKind.Duration:
                var t = Text(node);
                return TimeSpan.TryParse(t, CultureInfo.InvariantCulture, out var ts) ? DurationFormat.Format(ts) : t;
            case FieldKind.List:
                return node is JsonArray arr && arr.Count > 0 ? string.Join("\n", arr.Select(Text)) : "—";
            case FieldKind.KeyValue:
                return node is JsonObject obj && obj.Count > 0 ? string.Join("\n", obj.Select(kv => $"{kv.Key}: {Text(kv.Value)}")) : "—";
            default:
                var s = Text(node);
                return string.IsNullOrEmpty(s) ? "—" : s;
        }
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s ?? "",
        _ => node.ToJsonString(),
    };
}
