using System.Globalization;
using Beesharp.Ai;
using Beesharp.AiAdapters;
using Beesharp.Api;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Storage;
using Microsoft.AspNetCore.HttpOverrides;

namespace Beesharp;

/// <summary>Options for <see cref="BeesharpApp.CreateBuilder"/> (brain 0058).</summary>
public sealed class BeesharpAppOptions
{
    /// <summary>Registers the four built-in HTTP scenarios by default.</summary>
    public bool IncludeBuiltInScenarios { get; set; } = true;

    /// <summary>Registers the AI assistant endpoints and adapters by default (stays hidden unless <c>AI__Model</c> is configured).</summary>
    public bool IncludeAiAssistant { get; set; } = true;

    /// <summary>Optional default environment name when not overridden by <c>Plan:DefaultEnvironment</c>.</summary>
    public string? DefaultEnvironment { get; set; }
}

/// <summary>Handle returned by <see cref="BeesharpAppBuilder.AddPreset"/> to mark a code-defined preset as the default.</summary>
public sealed class PresetRegistrationHandle
{
    private readonly Action markDefault;

    internal PresetRegistrationHandle(string name, Action markDefault)
    {
        Name = name;
        this.markDefault = markDefault;
    }

    public string Name { get; }

    /// <summary>Marks this preset as the default plan loaded at startup when no prior run was restored (unless overridden by <c>Plan:DefaultPreset</c>).</summary>
    public PresetRegistrationHandle AsDefault()
    {
        markDefault();
        return this;
    }
}

/// <summary>
/// Aspire-style builder for an embedded Beesharp host (brain 0058). Exposes standard .NET DI
/// (<see cref="Services"/>), configuration (<see cref="Configuration"/>), scenario registration
/// (<see cref="AddScenario{TScenario}"/>) and strongly typed presets (<see cref="AddPreset"/>).
/// </summary>
public sealed class BeesharpAppBuilder
{
    private readonly BeesharpAppOptions options;
    private readonly string[]? headlessArgs;
    private readonly List<(string Name, Action<PlanBuilder> Configure)> presetBuilders = [];
    private string? codeDefaultPreset;
    private string? codeDefaultEnvironment;
    private bool builtInsRegistered;

    internal BeesharpAppBuilder(string[] args, BeesharpAppOptions? options)
    {
        this.options = options ?? new BeesharpAppOptions();
        codeDefaultEnvironment = this.options.DefaultEnvironment;

        var isHeadless = args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase);
        headlessArgs = isHeadless ? args : null;

        // Avoid passing CLI subcommand arguments (e.g. "run --preset smoke") to ASP.NET Core's config parser.
        WebApplicationBuilder = WebApplication.CreateBuilder(isHeadless ? [] : args);

        if (this.options.IncludeBuiltInScenarios)
            AddBuiltInScenarios();
    }

    public WebApplicationBuilder WebApplicationBuilder { get; }
    public IServiceCollection Services => WebApplicationBuilder.Services;
    public ConfigurationManager Configuration => WebApplicationBuilder.Configuration;
    public IWebHostEnvironment Environment => WebApplicationBuilder.Environment;

    /// <summary>Registers a scenario in DI so constructor injection of shared services works naturally.</summary>
    public BeesharpAppBuilder AddScenario<TScenario>() where TScenario : class, IScenario
    {
        Services.AddScenario<TScenario>();
        return this;
    }

    /// <summary>Registers the built-in HTTP scenarios (idempotent).</summary>
    public BeesharpAppBuilder AddBuiltInScenarios()
    {
        if (builtInsRegistered) return this;
        builtInsRegistered = true;
        Services.AddScenario<HttpGetScenario>();
        Services.AddScenario<HttpPostJsonScenario>();
        Services.AddScenario<CustomRequestScenario>();
        Services.AddScenario<UserJourneyScenario>();
        return this;
    }

    /// <summary>
    /// Registers a strongly typed preset in C#. Appears in <c>/api/presets</c> and can be loaded/edited in the UI
    /// (saving edits in the UI writes a disk preset that overrides the code preset by name).
    /// </summary>
    public PresetRegistrationHandle AddPreset(string name, Action<PlanBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        var trimmed = name.Trim();
        presetBuilders.Add((trimmed, configure));
        return new PresetRegistrationHandle(trimmed, () => codeDefaultPreset = trimmed);
    }

    /// <summary>Sets the default environment name (overridden by <c>Plan:DefaultEnvironment</c> in configuration).</summary>
    public BeesharpAppBuilder UseDefaultEnvironment(string environmentName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentName);
        codeDefaultEnvironment = environmentName.Trim();
        return this;
    }

    public BeesharpApp Build()
    {
        var config = Configuration;
        var contentRoot = Environment.ContentRootPath;

        Services.AddBeesharpEngine(new BeesharpDirectories
        {
            Root = contentRoot,
            Presets = config["Presets:Directory"] ?? "presets",
            Environments = config["Environments:Directory"] ?? "environments",
            Scripts = config["Scripts:Directory"] ?? "scripts",
        });

        // "Storage:Provider" is DuckDb (default) or Memory; anything else fails with a message naming the supported ones.
        Services.AddBeesharpStorage(new StorageOptions
        {
            Provider = config["Storage:Provider"] ?? StorageOptions.DuckDb,
            DuckDbPath = config["Storage:DuckDbPath"],
            Root = contentRoot,
        });

        if (options.IncludeAiAssistant)
        {
            Services.AddSingleton<IPlanWorkspacePort, PlanWorkspaceAdapter>();
            Services.AddSingleton<IRunPort, RunAdapter>();
            Services.AddSingleton<IRunHistoryPort, RunHistoryAdapter>();
            Services.AddSingleton<IEnvironmentPort, EnvironmentAdapter>();
            Services.AddSingleton<IScriptPort, ScriptAdapter>();
            Services.AddBeesharpAi(config);
        }

        var app = WebApplicationBuilder.Build();

        // Trust reverse-proxy headers (e.g. cloudflared in Docker) so X-Forwarded-Proto/For are honoured before HTTPS redirection.
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
        };
        forwardedHeadersOptions.KnownIPNetworks.Clear();
        forwardedHeadersOptions.KnownProxies.Clear();
        app.UseForwardedHeaders(forwardedHeadersOptions);

        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }
        app.UseHttpsRedirection();

        // Blazor WebAssembly client static assets (fingerprinted, pre-compressed). Brain 0015 / 0058.
        // Fall back to Beesharp.Host's manifest so plain console apps and test hosts serve the UI too.
        MapStaticAssetsIfAvailable(app);

        app.MapScenarioEndpoints();
        app.MapScriptEndpoints();
        app.MapPreflightEndpoints();
        app.MapShutdownEndpoints();
        app.MapRunEndpoints();
        app.MapPlanEndpoints();
        app.MapEnvironmentEndpoints();
        app.MapRunQueryEndpoints();
        if (options.IncludeAiAssistant)
            app.MapBeesharpAi();

        // Unknown API routes are a real 404, not the client app.
        app.Map("/api/{**rest}", () => Results.NotFound());

        // Everything else is a client-side route of the WebAssembly app (/editor, ...): index.html with its asset placeholders filled.
        app.MapClientIndex();

        return new BeesharpApp(app, presetBuilders, codeDefaultPreset, codeDefaultEnvironment, headlessArgs);
    }

    private static void MapStaticAssetsIfAvailable(WebApplication app)
    {
        var appManifest = Path.Combine(AppContext.BaseDirectory, $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json");
        if (File.Exists(appManifest))
        {
            app.MapStaticAssets();
            return;
        }

        var hostManifest = Path.Combine(AppContext.BaseDirectory, "Beesharp.Host.staticwebassets.endpoints.json");
        if (File.Exists(hostManifest))
            app.MapStaticAssets(hostManifest);
    }
}

/// <summary>
/// Built embedded Beesharp host (brain 0058). Call <see cref="RunAsync"/> to start either the web UI + REST API
/// or headless CLI mode when invoked with <c>run ...</c>.
/// </summary>
public sealed class BeesharpApp : IAsyncDisposable
{
    private readonly List<(string Name, Action<PlanBuilder> Configure)> presetBuilders;
    private readonly string? codeDefaultPreset;
    private readonly string? codeDefaultEnvironment;
    private readonly string[]? headlessArgs;
    private bool initialized;

    internal BeesharpApp(
        WebApplication webApplication,
        List<(string Name, Action<PlanBuilder> Configure)> presetBuilders,
        string? codeDefaultPreset,
        string? codeDefaultEnvironment,
        string[]? headlessArgs)
    {
        WebApplication = webApplication;
        this.presetBuilders = presetBuilders;
        this.codeDefaultPreset = codeDefaultPreset;
        this.codeDefaultEnvironment = codeDefaultEnvironment;
        this.headlessArgs = headlessArgs;
    }

    public static BeesharpAppBuilder CreateBuilder(string[]? args = null, BeesharpAppOptions? options = null) =>
        new(args ?? [], options);

    public WebApplication WebApplication { get; }
    public IServiceProvider Services => WebApplication.Services;
    public bool IsHeadlessRun => headlessArgs is not null;

    /// <summary>
    /// Validates the scenario catalog, loads scripts, registers code presets, restores the latest run (in web mode),
    /// and applies default plan configuration. Idempotent; called automatically by <see cref="RunAsync"/>.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (initialized) return;
        initialized = true;

        // Fail fast: building the catalog builds every scenario's parameter spec, which throws for unsupported types (brain 0007).
        var catalog = Services.GetRequiredService<ScenarioCatalog>();
        await Services.LoadBeesharpScriptsAsync(ct);

        var workspace = Services.GetRequiredService<PlanWorkspace>();
        if (presetBuilders.Count > 0)
        {
            var builtPresets = new List<Preset>(presetBuilders.Count);
            var now = DateTimeOffset.UtcNow;
            foreach (var (name, configure) in presetBuilders)
            {
                var planBuilder = new PlanBuilder();
                configure(planBuilder);
                builtPresets.Add(new Preset(name, now, planBuilder.Build(catalog)));
            }
            workspace.RegisterCodePresets(builtPresets);
        }

        if (!IsHeadlessRun)
        {
            var runs = Services.GetRequiredService<RunManager>();
            await runs.RestoreLatestAsync(ct);
            workspace.SeedIfEmpty(runs.GetCurrent().Plan);
        }

        var defaultOptions = DefaultPlanConfigurationBinder.Bind(WebApplication.Configuration);
        if (string.IsNullOrWhiteSpace(defaultOptions.DefaultPreset) && !string.IsNullOrWhiteSpace(codeDefaultPreset))
            defaultOptions.DefaultPreset = codeDefaultPreset;
        if (string.IsNullOrWhiteSpace(defaultOptions.DefaultEnvironment) && !string.IsNullOrWhiteSpace(codeDefaultEnvironment))
            defaultOptions.DefaultEnvironment = codeDefaultEnvironment;

        await workspace.ApplyDefaultPlanAsync(defaultOptions, ct);
    }

    /// <summary>Starts the host (or runs headless CLI mode when invoked with <c>run ...</c>). Returns process exit code.</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        if (headlessArgs is not null)
            return await RunHeadlessAsync(headlessArgs, Console.Out, Console.Error, ct);

        await WebApplication.RunAsync(ct);
        return 0;
    }

    /// <summary>Executes headless <c>run</c> mode against this host's registered scenarios, DI services, presets and storage.</summary>
    public async Task<int> RunHeadlessAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        await InitializeAsync(ct);

        string? presetArg = null;
        string? envArg = null;
        int? durationOverride = null;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg is "--preset" or "-p") && i + 1 < args.Length)
                presetArg = args[++i];
            else if ((arg is "--env" or "-e") && i + 1 < args.Length)
                envArg = args[++i];
            else if ((arg is "--duration" or "-d") && i + 1 < args.Length && int.TryParse(args[++i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
                durationOverride = secs;
            else
            {
                error.WriteLine($"beesharp: unknown or incomplete option '{arg}'. Usage: run [--preset <name>] [--env <name>] [--duration <seconds>]");
                return 2;
            }
        }

        var workspace = Services.GetRequiredService<PlanWorkspace>();
        LoadPlan plan;
        string planLabel;

        if (!string.IsNullOrWhiteSpace(presetArg))
        {
            var loaded = await workspace.LoadPresetAsync(presetArg, ct);
            if (loaded is null)
            {
                var available = await workspace.ListPresetsAsync(ct);
                var names = available.Count == 0 ? "none" : string.Join(", ", available.Select(p => $"\"{p.Name}\""));
                error.WriteLine($"beesharp: preset '{presetArg}' was not found (available: {names}).");
                return 2;
            }
            plan = loaded.Plan;
            planLabel = loaded.PresetName;
        }
        else
        {
            var state = workspace.GetState();
            plan = state.Plan;
            planLabel = state.PresetName;
        }

        if (durationOverride is { } secsOverride)
            plan.DurationSeconds = secsOverride;

        var catalog = Services.GetRequiredService<ScenarioCatalog>();
        var planErrors = plan.Validate(catalog);
        if (planErrors.Count > 0)
        {
            error.WriteLine($"beesharp: plan '{planLabel}' cannot run: {string.Join("; ", planErrors)}");
            return 2;
        }

        var envName = string.IsNullOrWhiteSpace(envArg) ? workspace.DefaultEnvironment : envArg.Trim();
        if (!string.IsNullOrWhiteSpace(envName))
        {
            var env = await Services.GetRequiredService<EnvironmentStore>().GetAsync(envName, ct);
            if (env is null)
            {
                error.WriteLine($"beesharp: environment '{envName}' was not found.");
                return 2;
            }
        }

        var runs = Services.GetRequiredService<RunManager>();
        output.WriteLine($"beesharp run: plan \"{planLabel}\"{(string.IsNullOrWhiteSpace(envName) ? "" : $", environment {envName}")} ({plan.Scenarios.Count} entries, {plan.DurationSeconds}s)");

        try
        {
            await runs.StartAsync(plan, envName, ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error.WriteLine($"beesharp: {ex.Message}");
            return 2;
        }

        var stopped = false;
        using var reg = ct.Register(() =>
        {
            stopped = true;
            _ = runs.StopAsync();
        });

        await runs.WaitUntilFinishedAsync();

        var status = runs.GetCurrent(afterSeq: int.MaxValue);
        var totalDone = status.Scenarios.Sum(s => s.Finished);
        var totalFailed = status.Scenarios.Sum(s => s.Failed);
        output.WriteLine($"beesharp run finished ({status.State}): {totalDone} bees finished, {totalFailed} failed, elapsed {status.ElapsedSeconds:F1}s (run {status.RunId})");

        if (stopped || totalFailed > 0 || status.PreflightState == "Failed" || status.ShutdownState == "Failed" || status.Error is not null)
            return 1;

        return 0;
    }

    public ValueTask DisposeAsync() => WebApplication.DisposeAsync();
}
