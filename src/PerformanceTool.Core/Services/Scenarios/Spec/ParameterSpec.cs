using System.Collections.Concurrent;
using System.ComponentModel;
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

/// <summary>Editor/validation metadata for one property of a parameter class (or JSON Schema property on the client).</summary>
public sealed class FieldSpec
{
    public PropertyInfo? Property { get; init; }
    public required string JsonName { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    public string? Description { get; init; }
    public string? Placeholder { get; init; }
    public bool Required { get; init; }
    /// <summary>The property is nullable (e.g. <c>int?</c> or <c>string?</c>), so "empty" / <c>null</c> is a valid value.</summary>
    public bool Nullable { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public IReadOnlyList<ChoiceOption> Options { get; init; } = [];
    public ParameterSpec? Children { get; init; }
    public bool ItemsAreUrls { get; init; }
    /// <summary>Shown only under "More options" in the editor.</summary>
    public bool Advanced { get; init; }
    /// <summary>Default JSON value when constructed from a JSON Schema (no CLR <see cref="Property"/>).</summary>
    public JsonNode? SchemaDefault { get; init; }

    public bool Optional => !Required && (Nullable || Kind is FieldKind.Text or FieldKind.Multiline or FieldKind.Url
        or FieldKind.Secret or FieldKind.List or FieldKind.KeyValue);
    public bool Wide => Kind is FieldKind.Multiline or FieldKind.Url or FieldKind.List or FieldKind.KeyValue or FieldKind.Group;
}

/// <summary>
/// The editor/validation/schema model of a scenario's parameter class, built once per type by reflection
/// (or reconstructed from JSON Schema on the WebAssembly client for server-compiled custom scripts).
/// Parameter values are stored as a <see cref="JsonObject"/> (the preset format) and bound to the typed class
/// when a run starts. See docs/brain/0007-scenario-editor-derived-from-typed-spec.md.
/// </summary>
public sealed partial class ParameterSpec
{
    private static readonly ConcurrentDictionary<Type, ParameterSpec> Cache = new();
    private static readonly NullabilityInfoContext Nullability = new();

    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly ConstructorInfo? defaultCtor;

    private ParameterSpec(Type type, IReadOnlyList<FieldSpec> fields, ConstructorInfo? defaultCtor = null, bool isSchemaOnly = false)
    {
        Type = type;
        Fields = fields;
        this.defaultCtor = defaultCtor;
        IsSchemaOnly = isSchemaOnly;
    }

    public Type Type { get; }
    public IReadOnlyList<FieldSpec> Fields { get; }
    public bool IsSchemaOnly { get; }

    /// <summary>Returns the (cached) spec. Throws <see cref="NotSupportedException"/> for unsupported property types.</summary>
    public static ParameterSpec For(Type type) => Cache.TryGetValue(type, out var spec) ? spec : Cache.GetOrAdd(type, Build(type));

    /// <summary>
    /// Reconstructs a schema-only <see cref="ParameterSpec"/> from the JSON Schema returned by <c>GET /api/scenarios</c>.
    /// Used by the Blazor WebAssembly client to render the generated parameter editor for server-compiled C# scripts.
    /// </summary>
    public static ParameterSpec FromJsonSchema(JsonObject schema)
    {
        var fields = new List<FieldSpec>();
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema["required"] is JsonArray reqArr)
            foreach (var r in reqArr)
                if (r is JsonValue rv && rv.TryGetValue<string>(out var name)) required.Add(name);

        if (schema["properties"] is JsonObject props)
        {
            foreach (var (jsonName, node) in props)
            {
                if (node is not JsonObject ps) continue;
                fields.Add(BuildFieldFromSchema(jsonName, ps, required.Contains(jsonName)));
            }
        }
        return new ParameterSpec(typeof(JsonObject), fields, isSchemaOnly: true);
    }

    private static FieldSpec BuildFieldFromSchema(string jsonName, JsonObject ps, bool isRequired)
    {
        var editorHint = ps["x-editor"]?.GetValue<string>();
        var nullable = ps["type"] is JsonArray arr && arr.Any(t => t?.GetValue<string>() == "null");
        var baseType = ps["type"] switch
        {
            JsonValue v => v.GetValue<string>(),
            JsonArray a => a.Select(t => t?.GetValue<string>()).FirstOrDefault(t => t is not null and not "null") ?? "string",
            _ => "string",
        };

        FieldKind kind;
        if (!string.IsNullOrEmpty(editorHint) && Enum.TryParse<FieldKind>(editorHint, ignoreCase: true, out var parsedKind))
            kind = parsedKind;
        else
            kind = baseType switch
            {
                "boolean" => FieldKind.Toggle,
                "integer" => FieldKind.Integer,
                "number" => FieldKind.Number,
                "array" => FieldKind.List,
                "object" => ps["properties"] is JsonObject ? FieldKind.Group : FieldKind.KeyValue,
                _ => ps["enum"] is JsonArray ? FieldKind.Choice : ps["format"]?.GetValue<string>() == "uri" ? FieldKind.Url : FieldKind.Text,
            };

        IReadOnlyList<ChoiceOption> options = [];
        if (kind == FieldKind.Choice && ps["enum"] is JsonArray enumArr)
            options = enumArr.Select(e => e?.GetValue<string>() ?? "").Where(s => s.Length > 0).Select(s => new ChoiceOption(s, s)).ToList();

        double? min = ps["minimum"] is JsonValue minV && minV.TryGetValue<double>(out var mn) ? mn : null;
        double? max = ps["maximum"] is JsonValue maxV && maxV.TryGetValue<double>(out var mx) ? mx : null;
        var children = kind == FieldKind.Group ? FromJsonSchema(ps) : null;
        var itemsAreUrls = kind == FieldKind.List && ps["items"] is JsonObject items && items["format"]?.GetValue<string>() == "uri";

        return new FieldSpec
        {
            Property = null,
            JsonName = jsonName,
            Label = ps["title"]?.GetValue<string>() ?? Humanize(jsonName),
            Description = ps["description"]?.GetValue<string>(),
            Kind = kind,
            Required = isRequired,
            Nullable = nullable,
            Min = min,
            Max = max,
            Options = options,
            Children = children,
            ItemsAreUrls = itemsAreUrls,
            SchemaDefault = ps["default"]?.DeepClone(),
        };
    }

    // ---------- building ----------

    private static ConstructorInfo? FindDefaultConstructor(Type type)
    {
        var parameterless = type.GetConstructor(Type.EmptyTypes);
        if (parameterless is not null) return parameterless;

        // Support positional records / classes whose primary constructor parameters all have default values.
        return type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length > 0 && c.GetParameters().All(p => p.HasDefaultValue))
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault();
    }

    private static ParameterSpec Build(Type type)
    {
        var ctor = FindDefaultConstructor(type);
        if (ctor is null)
            throw new NotSupportedException($"Parameter type {type.Name} needs a public parameterless constructor or a primary constructor where all parameters have defaults.");

        var fields = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0 && !p.IsDefined(typeof(JsonIgnoreAttribute)))
            .OrderBy(p => p.GetCustomAttribute<DisplayAttribute>()?.GetOrder() ?? int.MaxValue)
            .ThenBy(p => p.MetadataToken) // declaration order
            .Select(p => BuildField(type, p))
            .ToList();
        return new ParameterSpec(type, fields, ctor);
    }

    private static FieldSpec BuildField(Type owner, PropertyInfo p)
    {
        var display = p.GetCustomAttribute<DisplayAttribute>();
        var desc = display?.GetDescription() ?? p.GetCustomAttribute<DescriptionAttribute>()?.Description;
        var range = p.GetCustomAttribute<RangeAttribute>();
        var underlying = System.Nullable.GetUnderlyingType(p.PropertyType);
        var t = underlying ?? p.PropertyType;
        var isNullableRef = !t.IsValueType && IsNullableReference(p);
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
                 && FindDefaultConstructor(t) is not null && t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true)
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
            Description = desc,
            Placeholder = display?.GetPrompt(),
            Kind = kind,
            Required = p.IsDefined(typeof(RequiredAttribute)) || p.GetCustomAttribute<MinLengthAttribute>() is { Length: >= 1 },
            Nullable = underlying is not null || isNullableRef,
            Min = min,
            Max = max,
            Options = options,
            Children = children,
            ItemsAreUrls = kind == FieldKind.List && isUrl,
            Advanced = p.IsDefined(typeof(AdvancedAttribute)),
        };
    }

    private static bool IsNullableReference(PropertyInfo p)
    {
        lock (Nullability)
        {
            try { return Nullability.Create(p).ReadState == NullabilityState.Nullable; }
            catch { return false; }
        }
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

    public object CreateDefault()
    {
        if (IsSchemaOnly) return DefaultJson();
        if (defaultCtor is null || defaultCtor.GetParameters().Length == 0)
            return Activator.CreateInstance(Type)!;
        var args = defaultCtor.GetParameters().Select(p => p.DefaultValue).ToArray();
        return defaultCtor.Invoke(args);
    }

    public JsonObject DefaultJson()
    {
        if (!IsSchemaOnly) return ToJson(CreateDefault());
        var obj = new JsonObject();
        foreach (var f in Fields)
        {
            if (f.Kind == FieldKind.Group) obj[f.JsonName] = f.Children!.DefaultJson();
            else if (f.SchemaDefault is not null) obj[f.JsonName] = f.SchemaDefault.DeepClone();
            else obj[f.JsonName] = f.Kind switch
            {
                FieldKind.Toggle => false,
                FieldKind.Integer or FieldKind.Slider or FieldKind.Number => f.Nullable ? null : 0,
                FieldKind.List => new JsonArray(),
                FieldKind.KeyValue => new JsonObject(),
                FieldKind.Choice => f.Options.FirstOrDefault()?.Value ?? "",
                FieldKind.Duration => "00:00:00",
                _ => f.Nullable ? null : "",
            };
        }
        return obj;
    }

    public JsonObject ToJson(object value) =>
        value is JsonObject jo ? (JsonObject)jo.DeepClone() : (JsonObject)JsonSerializer.SerializeToNode(value, Type, JsonOptions)!;

    /// <summary>Binds and re-serialises: fills defaults, drops unknown keys, converts legacy shapes.</summary>
    public JsonObject Normalize(JsonObject? json, List<string>? problems = null)
    {
        if (IsSchemaOnly)
        {
            var result = DefaultJson();
            if (json is null) return result;
            foreach (var f in Fields)
            {
                if (!json.TryGetPropertyValue(f.JsonName, out var node)) continue;
                if (f.Kind == FieldKind.Group && node is JsonObject child)
                    result[f.JsonName] = f.Children!.Normalize(child, problems);
                else
                    result[f.JsonName] = node?.DeepClone();
            }
            return result;
        }
        return ToJson(Bind(json, problems));
    }

    /// <summary>
    /// Leniently binds stored values to a typed instance. Missing values keep their defaults; values in an older
    /// shape are converted where possible (e.g. a multiline string into a list); anything else is reported in
    /// <paramref name="problems"/> and replaced by the default.
    /// </summary>
    public object Bind(JsonObject? json, List<string>? problems = null, string prefix = "")
    {
        if (IsSchemaOnly) return Normalize(json, problems);
        var obj = CreateDefault();
        if (json is null) return obj;

        foreach (var f in Fields)
        {
            var prop = f.Property!;
            if (!json.TryGetPropertyValue(f.JsonName, out var node)) continue;
            if (node is null)
            {
                if (f.Nullable) prop.SetValue(obj, null);
                continue;
            }

            if (f.Kind == FieldKind.Group)
            {
                if (node is JsonObject child) prop.SetValue(obj, f.Children!.Bind(child, problems, $"{prefix}{f.Label} › "));
                else problems?.Add($"{prefix}{f.Label}: expected a group of settings, using the defaults.");
                continue;
            }

            if (TryConvert(node, f, out var value)) prop.SetValue(obj, value);
            else problems?.Add($"{prefix}{f.Label}: could not read the stored value {Truncate(node.ToJsonString())}, using the default.");
        }
        return obj;
    }

    private static bool TryConvert(JsonNode node, FieldSpec f, out object? value)
    {
        var prop = f.Property!;
        try
        {
            value = node.Deserialize(prop.PropertyType, JsonOptions);
            if (value is not null || f.Nullable) return true;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
        }

        // Legacy/loose shapes, mostly from presets saved before parameters were typed.
        value = null;
        if (node is not JsonValue v || !v.TryGetValue<string>(out var s)) return false;
        var t = System.Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
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
        if (IsSchemaOnly)
            return ValidateByField(json).Values.SelectMany(v => v).ToList();
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
        if (IsSchemaOnly)
        {
            ValidateSchemaFields(json, "", map);
            return map;
        }
        ValidateFieldsOf(Bind(json), "", map);
        return map;
    }

    private void ValidateSchemaFields(JsonObject? json, string prefix, Dictionary<string, List<string>> map)
    {
        foreach (var f in Fields)
        {
            var key = prefix + f.JsonName;
            JsonNode? node = null;
            json?.TryGetPropertyValue(f.JsonName, out node);
            if (f.Kind == FieldKind.Group)
            {
                if (node is JsonObject child) f.Children!.ValidateSchemaFields(child, key + "-", map);
                continue;
            }
            if (f.Required)
            {
                var empty = node is null
                    || (node is JsonValue sv && sv.TryGetValue<string>(out var s) && string.IsNullOrWhiteSpace(s))
                    || (node is JsonArray arr && arr.Count == 0);
                if (empty)
                {
                    if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                    list.Add($"{f.Label} is required.");
                }
            }
            if ((f.Min is not null || f.Max is not null) && node is JsonValue nv && TryGetNumber(nv, out var num))
            {
                if (f.Min is { } mn && num < mn || f.Max is { } mx && num > mx)
                {
                    if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                    list.Add($"{f.Label} must be between {f.Min} and {f.Max}.");
                }
            }
        }
    }

    private static bool TryGetNumber(JsonValue v, out double number)
    {
        if (v.TryGetValue<double>(out number)) return true;
        if (v.TryGetValue<int>(out var i)) { number = i; return true; }
        if (v.TryGetValue<long>(out var l)) { number = l; return true; }
        if (v.TryGetValue<decimal>(out var d)) { number = (double)d; return true; }
        if (v.TryGetValue<float>(out var f)) { number = f; return true; }
        number = 0;
        return false;
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
                var key = prefix + (Fields.FirstOrDefault(f => f.Property?.Name == m)?.JsonName ?? "");
                if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                list.Add(r.ErrorMessage ?? "Invalid value.");
            }
        }
        foreach (var f in Fields.Where(f => f.Kind == FieldKind.Group))
            if (f.Property?.GetValue(obj) is { } child)
                f.Children!.ValidateFieldsOf(child, prefix + f.JsonName + "-", map);
    }

    private void ValidateObject(object obj, string prefix, List<string> errors)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(obj, new ValidationContext(obj), results, validateAllProperties: true);
        errors.AddRange(results.Select(r => prefix + r.ErrorMessage));
        foreach (var f in Fields.Where(f => f.Kind == FieldKind.Group))
            if (f.Property?.GetValue(obj) is { } child)
                f.Children!.ValidateObject(child, $"{prefix}{f.Label} › ", errors);
    }

    // ---------- secrets ----------

    [GeneratedRegex(@"^\$\{env:([A-Za-z_][A-Za-z0-9_]*)\}$")]
    private static partial Regex EnvReference();

    public static bool IsEnvReference(string? value) => value is not null && EnvReference().IsMatch(value.Trim());

    /// <summary>A secret value that is just a <c>{{Key}}</c> placeholder holds no secret itself, so presets keep it (brain 0033).</summary>
    private static bool IsPlaceholderReference(string value) =>
        Services.Environments.Placeholders.Split(value.Trim()) is [{ IsPlaceholder: true }];

    /// <summary>Blanks secret values that aren't <c>${env:NAME}</c> references. Returns how many were removed.</summary>
    public int StripSecrets(JsonObject json)
    {
        var removed = 0;
        foreach (var f in Fields)
        {
            if (f.Kind == FieldKind.Secret && json[f.JsonName] is JsonValue v && v.TryGetValue<string>(out var s)
                && s.Length > 0 && !IsEnvReference(s) && !IsPlaceholderReference(s))
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
        if (IsSchemaOnly) return;
        foreach (var f in Fields)
        {
            if (f.Property is null) continue;
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
