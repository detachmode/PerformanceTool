using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Beesharp.Services;
using Beesharp.Services.Scenarios;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;

namespace Beesharp.Tests;

/// <summary>
/// Stories for <see cref="BeesharpApp"/> options, <see cref="Api.ClientIndexEndpoints"/>
/// (<c>/build-info.json</c> and SPA fallback), and <see cref="Api.ProgramEndpoints"/> (<c>/api/preflights</c>, <c>/api/shutdowns</c>).
/// </summary>
public sealed class HostIndexAndOptionsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"beesharp-host-opts-{Guid.NewGuid():N}");

    public HostIndexAndOptionsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static async Task<HttpStatusCode> StatusAsync(Task<HttpResponseMessage> responseTask)
    {
        using var resp = await responseTask;
        return resp.StatusCode;
    }

    private static async Task EnsureOkAsync(Task<HttpResponseMessage> responseTask)
    {
        using var resp = await responseTask;
        resp.EnsureSuccessStatusCode();
    }

    private static async Task<T> ReadOkJsonAsync<T>(Task<HttpResponseMessage> responseTask)
    {
        using var resp = await responseTask;
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<T>(Json))!;
    }

    [Fact]
    public async Task Client_index_serves_build_info_rewrites_wwwroot_index_html_and_returns_404_when_absent()
    {
        // 1. Host WITH a wwwroot/index.html containing fingerprint placeholders serves rewritten HTML with Cache-Control: no-cache.
        var withUiRoot = Path.Combine(root, "with-ui");
        var wwwroot = Path.Combine(withUiRoot, "wwwroot");
        Directory.CreateDirectory(wwwroot);
        await File.WriteAllTextAsync(Path.Combine(wwwroot, "index.html"), """
            <!DOCTYPE html>
            <html><head><script type="importmap"></script></head>
            <body><script src="_framework/blazor.webassembly#[.{fingerprint}].js"></script></body></html>
            """);

        using (var webRootProvider = new PhysicalFileProvider(wwwroot))
        {
            var builder = BeesharpApp.CreateBuilder(options: new BeesharpAppOptions { IncludeBuiltInScenarios = false });
            builder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.WebApplicationBuilder.Environment.ContentRootPath = withUiRoot;
            builder.WebApplicationBuilder.Environment.WebRootFileProvider = webRootProvider;
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:Provider"] = "Memory",
                ["Presets:Directory"] = Path.Combine(root, "presets"),
                ["Environments:Directory"] = Path.Combine(root, "environments"),
                ["Scripts:Directory"] = Path.Combine(root, "scripts"),
            });

            await using var app = builder.Build();
            await app.InitializeAsync();
            await app.WebApplication.StartAsync();

            try
            {
                var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
                using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

                // /build-info.json disables caching (brain 0025).
                using (var buildResp = await http.GetAsync("/build-info.json"))
                {
                    buildResp.EnsureSuccessStatusCode();
                    Assert.Equal("no-store", buildResp.Headers.CacheControl?.ToString());
                    var buildJson = await buildResp.Content.ReadFromJsonAsync<JsonElement>(Json);
                    Assert.True(buildJson.TryGetProperty("sha", out _));
                }

                // Client SPA route falls back to resolved index.html with Cache-Control: no-cache (and caches HTML for subsequent routes).
                using (var spaResp = await http.GetAsync("/scenario/1"))
                {
                    spaResp.EnsureSuccessStatusCode();
                    Assert.Equal("no-cache", spaResp.Headers.CacheControl?.ToString());
                    var html = await spaResp.Content.ReadAsStringAsync();
                    Assert.DoesNotContain("{fingerprint}", html);
                    Assert.Contains("blazor.webassembly.js", html);
                }

                using (var cachedSpaResp = await http.GetAsync("/editor"))
                {
                    cachedSpaResp.EnsureSuccessStatusCode();
                    Assert.Equal("no-cache", cachedSpaResp.Headers.CacheControl?.ToString());
                    Assert.Contains("blazor.webassembly.js", await cachedSpaResp.Content.ReadAsStringAsync());
                }

                // With IncludeBuiltInScenarios=false, catalog has no built-ins.
                Assert.Empty(app.Services.GetRequiredService<ScenarioCatalog>().All);
            }
            finally
            {
                await app.WebApplication.StopAsync();
            }
        }

        // 2. Host WITHOUT index.html in wwwroot (and no bundled beesharp-ui) returns 404 on SPA fallback routes
        //    and unknown /api routes, while /build-info.json still responds with 200 OK.
        var noUiRoot = Path.Combine(root, "no-ui");
        Directory.CreateDirectory(noUiRoot);

        var noUiBuilder = BeesharpApp.CreateBuilder(options: new BeesharpAppOptions { IncludeBuiltInScenarios = false });
        noUiBuilder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        noUiBuilder.WebApplicationBuilder.Environment.ContentRootPath = noUiRoot;
        noUiBuilder.WebApplicationBuilder.Environment.WebRootFileProvider = new NullFileProvider();
        noUiBuilder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        await using var noUiApp = noUiBuilder.Build();
        await noUiApp.InitializeAsync();
        await noUiApp.WebApplication.StartAsync();

        try
        {
            var baseUrl = noUiApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/scenario/1")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/does-not-exist")));
            await EnsureOkAsync(http.GetAsync("/build-info.json"));
        }
        finally
        {
            await noUiApp.WebApplication.StopAsync();
        }
    }

    [Fact]
    public async Task Program_endpoints_cover_preflight_and_shutdown_crud_compile_errors_and_missing_test_runs()
    {
        var builder = BeesharpApp.CreateBuilder();
        builder.WebApplicationBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        await using var app = builder.Build();
        await app.InitializeAsync();
        await app.WebApplication.StartAsync();

        try
        {
            var baseUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            using var http = new HttpClient { BaseAddress = new Uri(baseUrl) };

            // Preflight: bad compile -> 400, valid upload -> 200, GET by id -> 200/404, test-run -> 200/404, delete -> 204/404.
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.PostAsJsonAsync("/api/preflights", new UploadScriptRequest("bad", "bad.cs", "int x = ;"), Json)));
            await EnsureOkAsync(http.PostAsJsonAsync("/api/preflights", new UploadScriptRequest("warm", "warm.cs", """Log("warmed");"""), Json));
            Assert.Equal(HttpStatusCode.OK, await StatusAsync(http.GetAsync("/api/preflights/warm")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.GetAsync("/api/preflights/nope")));

            var preRun = await ReadOkJsonAsync<PreflightRunResult>(http.PostAsJsonAsync("/api/preflights/warm/test-run", new TestRunPreflightRequest(null, 15), Json));
            Assert.True(preRun.Success);
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.PostAsJsonAsync("/api/preflights/nope/test-run", new TestRunPreflightRequest(null, 15), Json)));

            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.DeleteAsync("/api/preflights/warm")));
            Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(http.DeleteAsync("/api/preflights/warm")));

            // Shutdown shares the same endpoint mapping under /api/shutdowns.
            await EnsureOkAsync(http.PostAsJsonAsync("/api/shutdowns", new UploadScriptRequest("tidy", "tidy.cs", """Log("tidied");"""), Json));
            var shutRun = await ReadOkJsonAsync<PreflightRunResult>(http.PostAsJsonAsync("/api/shutdowns/tidy/test-run", new TestRunPreflightRequest(null, 15), Json));
            Assert.True(shutRun.Success);
            Assert.Equal(HttpStatusCode.NoContent, await StatusAsync(http.DeleteAsync("/api/shutdowns/tidy")));

            // Starting a run with an empty plan returns 400 BadRequest cleanly.
            Assert.Equal(HttpStatusCode.BadRequest, await StatusAsync(http.PostAsJsonAsync("/api/runs/start", new StartRunRequest(Plan: new LoadPlan()), Json)));
        }
        finally
        {
            await app.WebApplication.StopAsync();
        }
    }

    [Fact]
    public async Task Host_builder_default_environment_and_headless_error_paths_report_clear_diagnostics()
    {
        var builder = BeesharpApp.CreateBuilder(["run", "--preset", "Missing"]);
        builder.WebApplicationBuilder.Environment.ContentRootPath = root;
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = "Memory",
            ["Presets:Directory"] = Path.Combine(root, "presets"),
            ["Environments:Directory"] = Path.Combine(root, "environments"),
            ["Scripts:Directory"] = Path.Combine(root, "scripts"),
        });

        builder.UseDefaultEnvironment("  Staging  ");
        var handle = builder.AddPreset("Smoke", p => p.Duration(TimeSpan.FromSeconds(1)).Add("http-get"));
        Assert.Equal("Smoke", handle.Name);
        handle.AsDefault();

        await using var app = builder.Build();
        await app.InitializeAsync();
        Assert.Equal("Staging", app.Services.GetRequiredService<PlanWorkspace>().DefaultEnvironment);

        // 1. Unknown CLI flag prints error and usage hint, returning exit code 2.
        using (var o = new StringWriter())
        using (var e = new StringWriter())
        {
            var code = await app.RunHeadlessAsync(["run", "--bogus-flag"], o, e);
            Assert.Equal(HeadlessExitCodes.CouldNotRun, code);
            Assert.Contains("dotnet run -- run --help", e.ToString());
        }

        // 2. Requesting an unknown preset lists available presets clearly and returns exit code 2.
        using (var o = new StringWriter())
        using (var e = new StringWriter())
        {
            var code = await app.RunHeadlessAsync(["run", "--preset", "DoesNotExist"], o, e);
            Assert.Equal(HeadlessExitCodes.CouldNotRun, code);
            Assert.Contains("DoesNotExist", e.ToString());
            Assert.Contains("\"Smoke\"", e.ToString());
        }
    }
}
