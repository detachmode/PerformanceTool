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
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Http;

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

    /// <summary>Registers a compiled preflight program in DI so constructor injection of shared services works naturally.</summary>
    public BeesharpAppBuilder AddPreflight<TPreflight>() where TPreflight : class, IPreflightProgram
    {
        Services.AddPreflight<TPreflight>();
        return this;
    }

    /// <summary>Registers an inline delegate preflight program.</summary>
    public BeesharpAppBuilder AddPreflight(string id, string name, Func<ProgramContext, CancellationToken, Task> execute)
    {
        Services.AddSingleton<IPreflightProgram>(new DelegatePreflightProgram(id, name, execute));
        return this;
    }

    /// <summary>Registers a compiled shutdown program in DI so constructor injection of shared services works naturally.</summary>
    public BeesharpAppBuilder AddShutdown<TShutdown>() where TShutdown : class, IShutdownProgram
    {
        Services.AddShutdown<TShutdown>();
        return this;
    }

    /// <summary>Registers an inline delegate shutdown program.</summary>
    public BeesharpAppBuilder AddShutdown(string id, string name, Func<ProgramContext, CancellationToken, Task> execute)
    {
        Services.AddSingleton<IShutdownProgram>(new DelegateShutdownProgram(id, name, execute));
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

        // Automatically attach RecordingHttpHandler to every DI-managed HttpClient (typed, named, Refit, custom primary handlers).
        Services.AddSingleton<IHttpMessageHandlerBuilderFilter, RecordingHttpMessageHandlerBuilderFilter>();

        // "Storage:Provider" is DuckDb (default) or Memory; anything else fails with a message naming the supported ones.
        var storageOptions = new StorageOptions
        {
            Provider = config["Storage:Provider"] ?? StorageOptions.DuckDb,
            DuckDbPath = config["Storage:DuckDbPath"],
            Root = contentRoot,
        };
        Services.AddBeesharpStorage(storageOptions);
        var hostLoggerProvider = new RunManagerLoggerProvider();
        Services.AddSingleton<ILoggerProvider>(hostLoggerProvider);
        var savedToPath = storageOptions.ResolveProvider() == StorageOptions.Memory ? null : storageOptions.ResolveDuckDbPath();

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
        hostLoggerProvider.Attach(app.Services.GetRequiredService<RunManager>());

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

        return new BeesharpApp(app, presetBuilders, codeDefaultPreset, codeDefaultEnvironment, headlessArgs, savedToPath);
    }

    private static void MapStaticAssetsIfAvailable(WebApplication app)
    {
        var appManifest = Path.Combine(AppContext.BaseDirectory, $"{app.Environment.ApplicationName}.staticwebassets.endpoints.json");
        if (File.Exists(appManifest))
        {
            app.MapStaticAssets();
        }
        else
        {
            var hostManifest = Path.Combine(AppContext.BaseDirectory, "Beesharp.Host.staticwebassets.endpoints.json");
            if (File.Exists(hostManifest))
                app.MapStaticAssets(hostManifest);
        }

        // NuGet consumer case: Beesharp.Host ships the published Blazor WASM UI under beesharp-ui/
        // when the consumer does not have index.html in its own WebRootFileProvider.
        if (!app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
        {
            var bundledUi = Path.Combine(AppContext.BaseDirectory, "beesharp-ui");
            if (Directory.Exists(bundledUi))
            {
                app.UseStaticFiles(new StaticFileOptions
                {
                    FileProvider = new PhysicalFileProvider(bundledUi),
                    ContentTypeProvider = CreateUiContentTypeProvider(),
                });
            }
        }
    }

    private static FileExtensionContentTypeProvider CreateUiContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".wasm"] = "application/wasm";
        provider.Mappings[".dat"] = "application/octet-stream";
        provider.Mappings[".blat"] = "application/octet-stream";
        provider.Mappings[".json"] = "application/json";
        provider.Mappings[".js"] = "text/javascript";
        provider.Mappings[".mjs"] = "text/javascript";
        provider.Mappings[".css"] = "text/css";
        provider.Mappings[".svg"] = "image/svg+xml";
        provider.Mappings[".ico"] = "image/x-icon";
        provider.Mappings[".woff2"] = "font/woff2";
        return provider;
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
    private readonly string? savedToPath;
    private bool initialized;

    internal BeesharpApp(
        WebApplication webApplication,
        List<(string Name, Action<PlanBuilder> Configure)> presetBuilders,
        string? codeDefaultPreset,
        string? codeDefaultEnvironment,
        string[]? headlessArgs,
        string? savedToPath = null)
    {
        WebApplication = webApplication;
        this.presetBuilders = presetBuilders;
        this.codeDefaultPreset = codeDefaultPreset;
        this.codeDefaultEnvironment = codeDefaultEnvironment;
        this.headlessArgs = headlessArgs;
        this.savedToPath = savedToPath;
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
            var preflights = Services.GetServices<IPreflightProgram>().ToList();
            var shutdowns = Services.GetServices<IShutdownProgram>().ToList();
            var builtPresets = new List<Preset>(presetBuilders.Count);
            var now = DateTimeOffset.UtcNow;
            foreach (var (name, configure) in presetBuilders)
            {
                var planBuilder = new PlanBuilder();
                configure(planBuilder);
                builtPresets.Add(new Preset(name, now, planBuilder.Build(catalog, preflights, shutdowns)));
            }
            workspace.RegisterCodePresets(builtPresets);
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

    /// <summary>Executes headless <c>run</c> mode against this host's registered scenarios, DI services, presets and storage using the shared <see cref="HeadlessRunner"/>.</summary>
    public async Task<int> RunHeadlessAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var startIndex = args.Length > 0 && string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var parsed = HeadlessArgumentParser.ParseRunOptions(args, startIndex, allowRootAndStorage: false, requirePreset: false);
        if (parsed.IsHelp)
        {
            output.WriteLine(HeadlessArgumentParser.HostRunUsage);
            return HeadlessExitCodes.Ok;
        }
        if (parsed.Error is not null)
        {
            error.WriteLine($"beesharp: {parsed.Error}");
            error.WriteLine("Run \"dotnet run -- run --help\" for usage.");
            return HeadlessExitCodes.CouldNotRun;
        }

        await InitializeAsync(ct);

        var options = parsed.Options!;
        var workspace = Services.GetRequiredService<PlanWorkspace>();
        LoadPlan plan;
        string planLabel;

        if (!string.IsNullOrWhiteSpace(options.Preset))
        {
            var loaded = await workspace.LoadPresetAsync(options.Preset, ct);
            if (loaded is null)
            {
                var available = await workspace.ListPresetsAsync(ct);
                var names = available.Count == 0 ? "none" : string.Join(", ", available.Select(p => $"\"{p.Name}\""));
                error.WriteLine($"beesharp: preset '{options.Preset}' was not found (available: {names}).");
                return HeadlessExitCodes.CouldNotRun;
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

        if (options.DurationSeconds is { } secsOverride)
            plan.DurationSeconds = secsOverride;

        var envName = string.IsNullOrWhiteSpace(options.Environment) ? workspace.DefaultEnvironment : options.Environment.Trim();

        var request = new HeadlessRunRequest
        {
            Plan = plan,
            PresetName = planLabel,
            EnvironmentName = envName,
            Options = options,
            SavedToPath = savedToPath,
        };

        return await HeadlessRunner.RunAsync(request, Services, output, error, ct);
    }

    public ValueTask DisposeAsync() => WebApplication.DisposeAsync();
}

internal sealed class RecordingHttpMessageHandlerBuilderFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.AdditionalHandlers.Add(new RecordingHttpHandler());
    };
}
