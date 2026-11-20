using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PerformanceTool.Services.Scenarios.Spec;

/// <summary>How a parameter is edited. Derived from the property's C# type and attributes, never set by hand.</summary>
public enum FieldKind { Text, Multiline, Url, Secret, Toggle, Choice, Integer, Slider, Number, Duration, List, KeyValue, Group }

public sealed record ChoiceOption(string Value, string Label);

/// <summary>Editor/validation metadata for one property of a parameter class.</summary>
public sealed class FieldSpec
{
    public required PropertyInfo Property { get; init; }
    public required string JsonName { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
    public bool Required { get; init; }
    /// <summary>The property is a nullable value type (e.g. <c>int?</c>), so "empty" is a valid value.</summary>
    public bool Nullable { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public IReadOnlyList<ChoiceOption> Options { get; init; } = [];
    public ParameterSpec? Children { get; init; }
    public bool ItemsAreUrls { get; init; }
    /// <summary>Shown only under "More options" in the editor.</summary>
    public bool Advanced { get; init; }

    public bool Optional => !Required && (Nullable || Kind is FieldKind.Text or FieldKind.Multiline or FieldKind.Url
        or FieldKind.Secret or FieldKind.List or FieldKind.KeyValue);
    public bool Wide => Kind is FieldKind.Multiline or FieldKind.Url or FieldKind.List or FieldKind.KeyValue or FieldKind.Group;
}

/// <summary>
/// The editor/validation/schema model of a scenario's parameter class, built once per type by reflection.
/// Parameter values are stored as a <see cref="JsonObject"/> (the preset format) and bound to the typed class
/// when a run starts. See docs/brain/0007-scenario-editor-derived-from-typed-spec.md.
/// </summary>
public sealed partial class ParameterSpec
{
    private static readonly ConcurrentDictionary<Type, ParameterSpec> Cache = new();

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private ParameterSpec(Type type, IReadOnlyList<FieldSpec> fields)
    {
        Type = type;
        Fields = fields;
    }

    public Type Type { get; }
    public IReadOnlyList<FieldSpec> Fields { get; }

    /// <summary>Returns the (cached) spec. Throws <see cref="NotSupportedException"/> for unsupported property types.</summary>
    public static ParameterSpec For(Type type) => Cache.TryGetValue(type, out var spec) ? spec : Cache.GetOrAdd(type, Build(type));

    // ---------- building ----------

    private static ParameterSpec Build(Type type)
    {
        if (type.GetConstructor(Type.EmptyTypes) is null)
            throw new NotSupportedException($"Parameter type {type.Name} needs a public parameterless constructor (its property initialisers are the defaults).");

        var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(JsonIgnoreAttribute)))
            .OrderBy(p => p.GetCustomAttribute<DisplayAttribute>()?.GetOrder() ?? int.MaxValue)
            .ThenBy(p => p.MetadataToken) // declaration order
            .Select(p => BuildField(type, p))
            .ToList();
        return new ParameterSpec(type, fields);
    }

    private static FieldSpec BuildField(Type owner, PropertyInfo p)
    {
        var display = p.GetCustomAttribute<DisplayAttribute>();
        var range = p.GetCustomAttribute<RangeAttribute>();
        var underlying = System.Nullable.GetUnderlyingType(p.PropertyType);
        var t = underlying ?? p.PropertyType;
        var isUrl = p.IsDefined(typeof(HttpUrlAttribute));
        double? min = null, max = null;
        IReadOnlyList<ChoiceOption> options = [];
        ParameterSpec? children = null;

        FieldKind kind;
        if (t == typeof(string))
            kind = p.IsDefined(typeof(SecretAttribute)) ? FieldKind.Secret
                : isUrl ? FieldKind.Url
                : p.IsDefined(typeof(MultilineAttribute)) ? FieldKind.Multiline
                : FieldKind.Text;
        else if (t == typeof(bool))
            kind = FieldKind.Toggle;
        else if (t.IsEnum)
        {
            kind = FieldKind.Choice;
            options = Enum.GetNames(t)
                .Select(n => new ChoiceOption(n, t.GetField(n)!.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? n))
                .ToList();
        }
        else if (t == typeof(int) || t == typeof(long) || t == typeof(double) || t == typeof(float) || t == typeof(decimal))
        {
            var integer = t == typeof(int) || t == typeof(long);
            if (range is not null)
            {
                min = Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture);
                max = Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture);
            }
            kind = !integer ? FieldKind.Number : max - min <= 20 ? FieldKind.Slider : FieldKind.Integer;
        }
        else if (t == typeof(TimeSpan))
            kind = FieldKind.Duration;
        else if (IsStringList(t))
            kind = FieldKind.List;
        else if (t == typeof(Dictionary<string, string>))
            kind = FieldKind.KeyValue;
        else if (t.IsClass && !typeof(System.Collections.IEnumerable).IsAssignableFrom(t)
                 && t.GetConstructor(Type.EmptyTypes) is not null && t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
        {
            kind = FieldKind.Group;
            children = For(t);
        }
        else
            throw new NotSupportedException(
                $"{owner.Name}.{p.Name}: type {t.Name} is not supported by the scenario editor. Supported: string, bool, enums, " +
                "int, long, double, float, decimal (also nullable), TimeSpan, string[]/List<string>, Dictionary<string,string> " +
                "and nested parameter classes. See docs/brain/0007.");

        return new FieldSpec
        {
            Property = p,
            JsonName = p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? JsonNamingPolicy.CamelCase.ConvertName(p.Name),
            Label = display?.GetName() ?? Humanize(p.Name),
            Description = display?.GetDescription(),
            Placeholder = display?.GetPrompt(),
            Kind = kind,
            Required = p.IsDefined(typeof(RequiredAttribute)) || p.GetCustomAttribute<MinLengthAttribute>() is { Length: >= 1 },
            Nullable = underlying is not null,
            Min = min,
            Max = max,
            Options = options,
            Children = children,
            ItemsAreUrls = kind == FieldKind.List && isUrl,
            Advanced = p.IsDefined(typeof(AdvancedAttribute)),
        };
    }

    private static bool IsStringList(Type t) =>
        t == typeof(string[]) ||
        (t.IsGenericType && t.GetGenericArguments() is [var arg] && arg == typeof(string) &&
         t.GetGenericTypeDefinition() is var g && (g == typeof(List<>) || g == typeof(IList<>) || g == typeof(IReadOnlyList<>)));

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex WordBoundary();

    private static string Humanize(string name)
    {
        var words = WordBoundary().Split(name);
        return string.Join(' ', words.Select((w, i) => i == 0 ? w : w.ToLowerInvariant()));
    }

    // ---------- values ----------

    public object CreateDefault() => Activator.CreateInstance(Type)!;

    public JsonObject DefaultJson() => ToJson(CreateDefault());

    public JsonObject ToJson(object value) => (JsonObject)JsonSerializer.SerializeToNode(value, Type, JsonOptions)!;

    /// <summary>Binds and re-serialises: fills defaults, drops unknown keys, converts legacy shapes.</summary>
    public JsonObject Normalize(JsonObject? json, List<string>? problems = null) => ToJson(Bind(json, problems));

    /// <summary>
    /// Leniently binds stored values to a typed instance. Missing values keep their defaults; values in an older
    /// shape are converted where possible (e.g. a multiline string into a list); anything else is reported in
    /// <paramref name="problems"/> and replaced by the default.
    /// </summary>
    public object Bind(JsonObject? json, List<string>? problems = null, string prefix = "")
    {
        var obj = CreateDefault();
        if (json is null) return obj;

        foreach (var f in Fields)
        {
            if (!json.TryGetPropertyValue(f.JsonName, out var node)) continue;
            if (node is null)
            {
                if (f.Nullable) f.Property.SetValue(obj, null);
                continue;
            }

            if (f.Kind == FieldKind.Group)
            {
                if (node is JsonObject child) f.Property.SetValue(obj, f.Children!.Bind(child, problems, $"{prefix}{f.Label} › "));
                else problems?.Add($"{prefix}{f.Label}: expected a group of settings, using the defaults.");
                continue;
            }

            if (TryConvert(node, f, out var value)) f.Property.SetValue(obj, value);
            else problems?.Add($"{prefix}{f.Label}: could not read the stored value {Truncate(node.ToJsonString())}, using the default.");
        }
        return obj;
    }

    private static bool TryConvert(JsonNode node, FieldSpec f, out object? value)
    {
        try
        {
            value = node.Deserialize(f.Property.PropertyType, JsonOptions);
            if (value is not null || f.Nullable) return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
        }

        // Legacy/loose shapes, mostly from presets saved before parameters were typed.
        value = null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return false;
        var t = System.Nullable.GetUnderlyingType(f.Property.PropertyType) ?? f.Property.PropertyType;
        switch (f.Kind)
        {
            case FieldKind.List:
                var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
                value = t.IsArray ? lines.ToArray() : lines;
                return true;
            case FieldKind.KeyValue:
                var dict = new Dictionary<string, string>();
                foreach (var line in s.Split('\n'))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0) dict[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
                value = dict;
                return true;
            case FieldKind.Toggle when bool.TryParse(s.Trim(), out var b):
                value = b;
                return true;
            case FieldKind.Choice when Enum.TryParse(t, s.Trim(), ignoreCase: true, out var e):
                value = e;
                return true;
            case FieldKind.Duration when DurationFormat.TryParse(s, out var d):
                value = d;
                return true;
            default:
                return false;
        }
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..37] + "...";

    // ---------- validation ----------

    /// <summary>Validates stored values with the DataAnnotations on the parameter class (recursively).</summary>
    public List<string> Validate(JsonObject? json)
    {
        var errors = new List<string>();
        var obj = Bind(json, errors);
        ValidateObject(obj, "", errors);
        return errors;
    }

    /// <summary>
    /// Same checks as <see cref="Validate"/>, but per field, for inline messages. Keys are the field's JSON name, nested
    /// groups joined with "-" (matching the editor's element ids); "" holds messages that belong to no single field.
    /// </summary>
    public Dictionary<string, List<string>> ValidateByField(JsonObject? json)
    {
        var map = new Dictionary<string, List<string>>();
        ValidateFieldsOf(Bind(json), "", map);
        return map;
    }

    private void ValidateFieldsOf(object obj, string prefix, Dictionary<string, List<string>> map)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(obj, new ValidationContext(obj), results, validateAllProperties: true);
        foreach (var r in results)
        {
            var members = r.MemberNames.Any() ? r.MemberNames : [""];
            foreach (var m in members)
            {
                var key = prefix + (Fields.FirstOrDefault(f => f.Property.Name == m)?.JsonName ?? "");
                if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                list.Add(r.ErrorMessage ?? "Invalid value.");
            }
        }
        foreach (var f in Fields.Where(f => f.Kind == FieldKind.Group))
            if (f.Property.GetValue(obj) is { } child)
                f.Children!.ValidateFieldsOf(child, prefix + f.JsonName + "-", map);
    }

    private void ValidateObject(object obj, string prefix, List<string> errors)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(obj, new ValidationContext(obj), results, validateAllProperties: true);
        errors.AddRange(results.Select(r => prefix + r.ErrorMessage));
        foreach (var f in Fields.Where(f => f.Kind == FieldKind.Group))
            if (f.Property.GetValue(obj) is { } child)
                f.Children!.ValidateObject(child, $"{prefix}{f.Label} › ", errors);
    }

    // ---------- secrets ----------

    [GeneratedRegex(@"^\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}$")]
    private static partial Regex EnvReference();

    public static bool IsEnvReference(string? value) => value is not null && EnvReference().IsMatch(value.Trim());

    /// <summary>Blanks secret values that aren't <c>${env:NAME}</c> references. Returns how many were removed.</summary>
    public int StripSecrets(JsonObject json)
    {
        var removed = 0;
        foreach (var f in Fields)
        {
            if (f.Kind == FieldKind.Secret && json[f.JsonName] is JsonValue v && v.TryGetValue<string>(out var s)
                && s.Length > 0 && !IsEnvReference(s))
            {
                json[f.JsonName] = "";
                removed++;
            }
            else if (f.Kind == FieldKind.Group && json[f.JsonName] is JsonObject child)
                removed += f.Children!.StripSecrets(child);
        }
        return removed;
    }

    /// <summary>Replaces <c>${env:NAME}</c> in secret fields of a bound instance with the environment variable's value.</summary>
    public void ResolveSecrets(object obj)
    {
        foreach (var f in Fields)
        {
            if (f.Kind == FieldKind.Secret && f.Property.GetValue(obj) is string s && EnvReference().Match(s.Trim()) is { Success: true } m)
                f.Property.SetValue(obj, Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
            else if (f.Kind == FieldKind.Group && f.Property.GetValue(obj) is { } child)
                f.Children!.ResolveSecrets(child);
        }
    }

    // ---------- JSON Schema (for the API, CLI and coding harnesses) ----------

    public JsonObject ToJsonSchema()
    {
        var defaults = DefaultJson();
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var f in Fields)
        {
            var schema = f.Kind == FieldKind.Group ? f.Children!.ToJsonSchema() : FieldSchema(f);
            schema["title"] = f.Label;
            if (f.Description is not null) schema["description"] = f.Description;
            schema["x-editor"] = f.Kind.ToString().ToLowerInvariant();
            if (f.Kind != FieldKind.Group && f.Kind != FieldKind.Secret && defaults[f.JsonName] is { } d) schema["default"] = d.DeepClone();
            properties[f.JsonName] = schema;
            if (f.Required) required.Add(f.JsonName);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
            ["additionalProperties"] = false,
        };
    }

    private static JsonObject FieldSchema(FieldSpec f)
    {
        JsonObject s = f.Kind switch
        {
            FieldKind.Url => new() { ["type"] = "string", ["format"] = "uri" },
            FieldKind.Secret => new() { ["type"] = "string", ["writeOnly"] = true },
            FieldKind.Toggle => new() { ["type"] = "boolean" },
            FieldKind.Choice => new() { ["type"] = "string", ["enum"] = new JsonArray(f.Options.Select(o => (JsonNode)o.Value).ToArray()) },
            FieldKind.Integer or FieldKind.Slider => new() { ["type"] = "integer" },
            FieldKind.Number => new() { ["type"] = "number" },
            FieldKind.Duration => new() { ["type"] = "string", ["format"] = "timespan", ["examples"] = new JsonArray("00:00:00.5", "00:00:02") },
            FieldKind.List => new()
            {
                ["type"] = "array",
                ["items"] = f.ItemsAreUrls ? new JsonObject { ["type"] = "string", ["format"] = "uri" } : new JsonObject { ["type"] = "string" },
            },
            FieldKind.KeyValue => new() { ["type"] = "object", ["additionalProperties"] = new JsonObject { ["type"] = "string" } },
            _ => new() { ["type"] = "string" },
        };
        if (f.Min is { } min) s["minimum"] = min;
        if (f.Max is { } max) s["maximum"] = max;
        if (f.Kind == FieldKind.List && f.Required) s["minItems"] = 1;
        if (f.Nullable) s["type"] = new JsonArray(s["type"]!.GetValue<string>(), "null");
        return s;
    }
}
