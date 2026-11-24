using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;

namespace Beesharp.Services;

/// <summary>
/// Binds the <c>Plan</c> configuration section (<c>appsettings.json</c> / <c>Plan__*</c> env vars) into
/// <see cref="DefaultPlanOptions"/>, converting nested <c>Parameters</c> sections into <see cref="JsonObject"/>s
/// (brain 0058).
/// </summary>
internal static class DefaultPlanConfigurationBinder
{
    public static DefaultPlanOptions Bind(IConfiguration configuration)
    {
        var section = configuration.GetSection("Plan");
        var options = new DefaultPlanOptions
        {
            DefaultPreset = section["DefaultPreset"],
            DefaultEnvironment = section["DefaultEnvironment"],
            ForceDefault = bool.TryParse(section["ForceDefault"], out var force) && force,
            DurationSeconds = TryInt(section["DurationSeconds"]),
            MaxConcurrency = TryInt(section["MaxConcurrency"]),
            TimeoutSeconds = TryInt(section["TimeoutSeconds"]),
            PreflightScriptId = section["PreflightScriptId"],
            ShutdownScriptId = section["ShutdownScriptId"],
        };

        var scenariosSection = section.GetSection("Scenarios");
        var scenarioChildren = scenariosSection.GetChildren().ToList();
        if (scenarioChildren.Count > 0)
        {
            options.Scenarios = [];
            foreach (var entry in scenarioChildren)
            {
                var scenarioId = entry["ScenarioId"];
                if (string.IsNullOrWhiteSpace(scenarioId)) continue;

                var item = new DefaultScenarioEntryOptions
                {
                    ScenarioId = scenarioId,
                    Label = entry["Label"],
                    RatePerMinute = TryDouble(entry["RatePerMinute"]),
                    Pattern = Enum.TryParse<SpawningPattern>(entry["Pattern"], ignoreCase: true, out var pat) ? pat : null,
                    BeesPerBurst = TryInt(entry["BeesPerBurst"]),
                    BurstsPerMinute = TryDouble(entry["BurstsPerMinute"]),
                    MaxInFlight = TryInt(entry["MaxInFlight"]),
                };

                var paramSection = entry.GetSection("Parameters");
                if (paramSection.GetChildren().Any())
                    item.Parameters = ToJsonObject(paramSection);

                options.Scenarios.Add(item);
            }
        }

        return options;
    }

    private static JsonObject ToJsonObject(IConfigurationSection section)
    {
        var obj = new JsonObject();
        foreach (var child in section.GetChildren())
            obj[child.Key] = ToJsonNode(child);
        return obj;
    }

    private static JsonNode? ToJsonNode(IConfigurationSection section)
    {
        var children = section.GetChildren().ToList();
        if (children.Count == 0)
            return ParseScalar(section.Value);

        // Treat sequential 0..N-1 numeric keys as a JSON array.
        if (IsArraySection(children))
        {
            var arr = new JsonArray();
            foreach (var child in children.OrderBy(c => int.Parse(c.Key, CultureInfo.InvariantCulture)))
                arr.Add(ToJsonNode(child));
            return arr;
        }

        return ToJsonObject(section);
    }

    private static bool IsArraySection(List<IConfigurationSection> children)
    {
        var indices = new HashSet<int>();
        foreach (var child in children)
        {
            if (!int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var idx) || idx < 0)
                return false;
            indices.Add(idx);
        }
        return indices.Count == children.Count && indices.Contains(0) && indices.Max() == children.Count - 1;
    }

    private static JsonNode? ParseScalar(string? value)
    {
        if (value is null) return null;
        if (bool.TryParse(value, out var b)) return JsonValue.Create(b);
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)) return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    private static int? TryInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static double? TryDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? n : null;
}
