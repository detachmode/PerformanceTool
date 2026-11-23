using System.Collections.Concurrent;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Services.Scenarios;

/// <summary>One declared step of a scenario, known before the run (brain 0056). <see cref="Times"/> is optional (null when dynamic).</summary>
public sealed record StepInfo(string Name, int? Times = null);

/// <summary>
/// Reflects a C# 15 <c>union</c> type once when a scenario is constructed (brain 0056), extracting its declared
/// case types in declaration order and their human-readable display names.
/// </summary>
public static class StepUnionReflection
{
    public sealed record CompiledUnion(IReadOnlyList<StepInfo> Steps, IReadOnlyDictionary<Type, int> CaseIndexByType);

    public static CompiledUnion Inspect<TStep>() where TStep : struct, IUnion => Inspect(typeof(TStep));

    public static CompiledUnion Inspect(Type unionType)
    {
        if (!unionType.IsValueType || !typeof(IUnion).IsAssignableFrom(unionType))
            throw new ArgumentException($"Step type '{unionType.Name}' must be a C# 15 union type.", nameof(unionType));

        var cases = new List<Type>();
        foreach (var ctor in unionType.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var ps = ctor.GetParameters();
            if (ps.Length != 1) continue;
            var caseType = ps[0].ParameterType;
            if (caseType == unionType || caseType == typeof(object)) continue;
            if (!cases.Contains(caseType))
                cases.Add(caseType);
        }

        if (cases.Count == 0)
            throw new ArgumentException($"Union type '{unionType.Name}' declares no step cases.", nameof(unionType));

        var steps = new List<StepInfo>(cases.Count);
        var map = new Dictionary<Type, int>(cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            var t = cases[i];
            map[t] = i;
            steps.Add(new StepInfo(DisplayNameOf(t)));
        }

        return new CompiledUnion(steps, map);
    }

    /// <summary>Reads <c>[Display(Name = ...)]</c> or <c>[DisplayName(...)]</c> from a step record, or humanizes PascalCase (<c>OpenProduct</c> → <c>Open product</c>).</summary>
    public static string DisplayNameOf(Type stepType)
    {
        if (stepType.GetCustomAttribute<DisplayAttribute>()?.GetName() is { Length: > 0 } display)
            return display.Trim();
        if (stepType.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName is { Length: > 0 } displayName)
            return displayName.Trim();
        return Humanize(stepType.Name);
    }

    internal static string Humanize(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;
        var sb = new StringBuilder(raw.Length + 6);
        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '_') { sb.Append(' '); continue; }
            var prevUpper = i > 0 && char.IsUpper(raw[i - 1]);
            var nextLower = i + 1 < raw.Length && char.IsLower(raw[i + 1]);
            if (i > 0 && char.IsUpper(c) && (!prevUpper || nextLower))
            {
                sb.Append(' ');
                sb.Append(nextLower ? char.ToLowerInvariant(c) : c);
            }
            else
            {
                sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Base class for compiled C# scenarios whose steps are a C# 15 <c>union</c> run as a step machine (brain 0056).
/// Return the next step from <see cref="RunStepAsync"/>, or <c>null</c> to finish the bee cleanly.
/// </summary>
public abstract class Scenario<TParameters, TStep> : Scenario<TParameters>
    where TParameters : class
    where TStep : struct, IUnion
{
    private readonly StepUnionReflection.CompiledUnion union = StepUnionReflection.Inspect<TStep>();

    public override IReadOnlyList<StepInfo> Steps => union.Steps;

    protected abstract TStep StartStep(TParameters parameters);

    protected abstract Task<TStep?> RunStepAsync(TStep step, TParameters parameters, ScenarioContext context, CancellationToken cancellationToken);

    protected sealed override Task<ScenarioOutcome> ExecuteAsync(TParameters parameters, ScenarioContext context, CancellationToken cancellationToken) =>
        StepMachineRunner.RunAsync(
            union,
            StartStep(parameters),
            (step, ctx, ct) => RunStepAsync(step, parameters, ctx, ct),
            context,
            cancellationToken);
}

/// <summary>Drives a union step machine for one bee, tracking current step, per-step visit count and cancellation.</summary>
internal static class StepMachineRunner
{
    public static async Task<ScenarioOutcome> RunAsync<TStep>(
        StepUnionReflection.CompiledUnion union,
        TStep start,
        Func<TStep, ScenarioContext, CancellationToken, Task<TStep?>> runStep,
        ScenarioContext context,
        CancellationToken ct)
        where TStep : struct, IUnion
    {
        var visits = new int[union.Steps.Count];
        TStep? current = start;
        while (current is { } step)
        {
            ct.ThrowIfCancellationRequested();
            var payload = ((IUnion)step).Value;
            // In C# 15, `cond ? nextStep : null` has the case's nullable type and implicitly converts `null` into a union with null Value.
            if (payload is null) break;
            if (!union.CaseIndexByType.TryGetValue(payload.GetType(), out var stepIndex))
                throw new InvalidOperationException($"Step type '{payload.GetType().Name}' is not declared in '{typeof(TStep).Name}'.");

            var rep = visits[stepIndex]++;
            context.EnterStep(stepIndex, rep, StepDataCapture.Serialize(payload));
            current = await runStep(step, context, ct);
        }
        return new ScenarioOutcome(200, 0);
    }

    /// <summary>
    /// Drives a union step machine where each step yields zero, one or many follow-up steps via <see cref="IAsyncEnumerable{T}"/>.
    /// Yielded steps are pushed depth-first (in yielded order), so retries run before later queued steps.
    /// </summary>
    public static async Task<ScenarioOutcome> RunManyAsync<TStep>(
        StepUnionReflection.CompiledUnion union,
        TStep start,
        Func<TStep, ScenarioContext, CancellationToken, IAsyncEnumerable<TStep>> runStep,
        ScenarioContext context,
        CancellationToken ct)
        where TStep : struct, IUnion
    {
        var visits = new int[union.Steps.Count];
        var pending = new Stack<TStep>();
        pending.Push(start);
        var emitted = new List<TStep>();

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var step = pending.Pop();
            var payload = ((IUnion)step).Value;
            if (payload is null) continue;
            if (!union.CaseIndexByType.TryGetValue(payload.GetType(), out var stepIndex))
                throw new InvalidOperationException($"Step type '{payload.GetType().Name}' is not declared in '{typeof(TStep).Name}'.");

            var rep = visits[stepIndex]++;
            context.EnterStep(stepIndex, rep, StepDataCapture.Serialize(payload));

            emitted.Clear();
            await foreach (var next in runStep(step, context, ct).WithCancellation(ct))
                emitted.Add(next);

            for (var i = emitted.Count - 1; i >= 0; i--)
                pending.Push(emitted[i]);
        }

        return new ScenarioOutcome(200, 0);
    }
}

/// <summary>Captures the public properties of a step record as compact bounded JSON, masking <see cref="SecretAttribute"/> properties.</summary>
internal static class StepDataCapture
{
    private const int MaxJsonLength = 2000;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Type, (PropertyInfo Prop, string Name, bool Secret)[]> Cache = new();

    public static string? Serialize(object payload)
    {
        var props = Cache.GetOrAdd(payload.GetType(), static t => t
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Select(p => (p, ToCamel(p.Name), p.GetCustomAttribute<SecretAttribute>() is not null))
            .ToArray());

        if (props.Length == 0) return null;

        var map = new Dictionary<string, object?>(props.Length);
        foreach (var (prop, name, secret) in props)
        {
            try
            {
                map[name] = secret ? "***" : prop.GetValue(payload);
            }
            catch
            {
                map[name] = null;
            }
        }

        var json = JsonSerializer.Serialize(map, JsonOpts);
        return json.Length <= MaxJsonLength ? json : json[..(MaxJsonLength - 1)] + "…";
    }

    private static string ToCamel(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
}

/// <summary>
/// <see cref="DelegatingHandler"/> that records every <see cref="HttpClient"/> request against the active bee's
/// <see cref="ScenarioContext.Current"/> (brain 0013, 0056), tagging each call with its current step and repetition.
/// </summary>
public sealed class RecordingHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var ctx = ScenarioContext.Current;
        if (ctx is null)
            return await base.SendAsync(request, cancellationToken);

        var offsetMs = ctx.ElapsedMs;
        var name = $"{request.Method.Method} {ShortUrl(request.RequestUri)}";
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            sw.Stop();
            var bytes = response.Content.Headers.ContentLength ?? 0;
            var status = (int)response.StatusCode;
            var error = status >= 400 ? $"HTTP {status}" : null;
            ctx.RecordHttpCall(name, offsetMs, sw.Elapsed.TotalMilliseconds, status, bytes, error);
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            var error = ex is OperationCanceledException && ctx.IsRunStopped ? ExecutionResult.CanceledError : ex.Message;
            ctx.RecordHttpCall(name, offsetMs, sw.Elapsed.TotalMilliseconds, null, 0, error);
            throw;
        }
    }

    private static string ShortUrl(Uri? uri)
    {
        if (uri is null) return "";
        return string.IsNullOrEmpty(uri.PathAndQuery) ? uri.ToString() : uri.PathAndQuery;
    }
}
