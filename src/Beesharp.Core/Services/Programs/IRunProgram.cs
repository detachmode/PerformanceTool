using System.Text;
using Beesharp.Services.Environments;
using Beesharp.Services.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Beesharp.Services;

/// <summary>
/// Runtime context passed to compiled <see cref="IPreflightProgram"/> and <see cref="IShutdownProgram"/> implementations.
/// Mirrors the globals available to Roslyn preflight/shutdown scripts.
/// </summary>
public sealed class ProgramContext
{
    public required HttpClient Http { get; init; }
    public required ILogger Logger { get; init; }
    public required EnvironmentValues Environment { get; init; }

    /// <summary>Alias for <see cref="Environment"/> matching the <c>Env</c> global in Roslyn scripts.</summary>
    public EnvironmentValues Env => Environment;

    /// <summary>The run bag (brain 0052): runtime data shared by the whole run. Scenarios read it via <c>ctx.RunBag</c>.</summary>
    public required BagAccess Data { get; init; }

    public required CancellationToken CancellationToken { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<string> Fail { get; init; }
    public required Func<string, string, Task<int>> RunProcessAsync { get; init; }
}

/// <summary>
/// A compiled C# program that executes around a load test (preflight before scenarios, or shutdown after scenarios).
/// </summary>
public interface IRunProgram
{
    string Id { get; }
    string Name { get; }
    Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken);
}

/// <summary>Compiled C# preflight program: runs before scenarios start; scenarios only begin if it succeeds.</summary>
public interface IPreflightProgram : IRunProgram;

/// <summary>Compiled C# shutdown program: always runs at the end of a run (even after Stop or a failed preflight).</summary>
public interface IShutdownProgram : IRunProgram;

/// <summary>Base class for compiled C# preflight programs with sensible default <see cref="Id"/> and <see cref="Name"/> derived from the class name.</summary>
public abstract class PreflightProgram : IPreflightProgram
{
    public virtual string Id => RunProgramNaming.DefaultId(GetType(), "PreflightProgram", "Preflight");
    public virtual string Name => RunProgramNaming.DefaultName(GetType(), "PreflightProgram", "Preflight");
    public abstract Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken);
}

/// <summary>Base class for compiled C# shutdown programs with sensible default <see cref="Id"/> and <see cref="Name"/> derived from the class name.</summary>
public abstract class ShutdownProgram : IShutdownProgram
{
    public virtual string Id => RunProgramNaming.DefaultId(GetType(), "ShutdownProgram", "Shutdown");
    public virtual string Name => RunProgramNaming.DefaultName(GetType(), "ShutdownProgram", "Shutdown");
    public abstract Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken);
}

/// <summary>Delegate-backed <see cref="IPreflightProgram"/> for inline registrations.</summary>
public sealed class DelegatePreflightProgram : IPreflightProgram
{
    private readonly Func<ProgramContext, CancellationToken, Task> execute;

    public DelegatePreflightProgram(string id, string name, Func<ProgramContext, CancellationToken, Task> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);
        Id = id.Trim();
        Name = name.Trim();
        this.execute = execute;
    }

    public string Id { get; }
    public string Name { get; }

    public Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken) =>
        execute(context, cancellationToken);
}

/// <summary>Delegate-backed <see cref="IShutdownProgram"/> for inline registrations.</summary>
public sealed class DelegateShutdownProgram : IShutdownProgram
{
    private readonly Func<ProgramContext, CancellationToken, Task> execute;

    public DelegateShutdownProgram(string id, string name, Func<ProgramContext, CancellationToken, Task> execute)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(execute);
        Id = id.Trim();
        Name = name.Trim();
        this.execute = execute;
    }

    public string Id { get; }
    public string Name { get; }

    public Task ExecuteAsync(ProgramContext context, CancellationToken cancellationToken) =>
        execute(context, cancellationToken);
}

public static class ProgramServiceCollectionExtensions
{
    public static IServiceCollection AddPreflight<TPreflight>(this IServiceCollection services)
        where TPreflight : class, IPreflightProgram =>
        services.AddSingleton<IPreflightProgram, TPreflight>();

    public static IServiceCollection AddShutdown<TShutdown>(this IServiceCollection services)
        where TShutdown : class, IShutdownProgram =>
        services.AddSingleton<IShutdownProgram, TShutdown>();
}

internal static class RunProgramNaming
{
    public static string DefaultId(Type type, params string[] suffixesToStrip)
    {
        var words = SplitWords(StripSuffixes(type.Name, suffixesToStrip));
        return string.Join('-', words.Select(w => w.ToLowerInvariant()));
    }

    public static string DefaultName(Type type, params string[] suffixesToStrip)
    {
        var words = SplitWords(StripSuffixes(type.Name, suffixesToStrip));
        if (words.Count == 0) return type.Name;

        var sb = new StringBuilder();
        for (var i = 0; i < words.Count; i++)
        {
            if (i > 0) sb.Append(' ');
            var w = words[i];
            var isAcronym = w.Length > 1 && w.All(char.IsUpper);
            if (isAcronym)
            {
                sb.Append(w);
            }
            else if (i == 0)
            {
                sb.Append(char.ToUpperInvariant(w[0]));
                if (w.Length > 1) sb.Append(w[1..].ToLowerInvariant());
            }
            else
            {
                sb.Append(w.ToLowerInvariant());
            }
        }
        return sb.ToString();
    }

    private static string StripSuffixes(string typeName, string[] suffixes)
    {
        var tick = typeName.IndexOf('`');
        var name = tick >= 0 ? typeName[..tick] : typeName;
        foreach (var suffix in suffixes)
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                return name[..^suffix.Length];
        }
        return name;
    }

    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '_' or '-' or ' ')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            if (current.Length > 0 && char.IsUpper(c))
            {
                var prev = text[i - 1];
                var nextIsLower = i + 1 < text.Length && char.IsLower(text[i + 1]);
                if (!char.IsUpper(prev) || nextIsLower)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }

            current.Append(c);
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }
}
