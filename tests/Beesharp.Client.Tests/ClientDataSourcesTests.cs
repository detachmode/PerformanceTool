using System.Net;
using Beesharp.Client.Data;
using Beesharp.Services;
using Beesharp.Services.Environments;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

/// <summary>
/// Stories for client environment &amp; mode-switching data sources:
/// <see cref="FakeEnvironmentSource"/>, <see cref="ApiEnvironmentSource"/>,
/// <see cref="SwitchableEnvironmentSource"/> and <see cref="SwitchableRunSource"/>.
/// </summary>
public sealed class ClientDataSourcesTests
{
    [Fact]
    public void Fake_environment_source_switches_renames_trims_and_deletes_environments()
    {
        var source = new FakeEnvironmentSource();
        var events = 0;
        source.Changed += () => events++;

        Assert.Equal("Staging", source.ActiveName);
        Assert.NotNull(source.Active);
        Assert.Equal(3, source.Environments.Count);

        // Selecting an unknown environment falls back to null ("No environment").
        source.SetActive("DoesNotExist");
        Assert.Null(source.ActiveName);
        Assert.Null(source.Active);

        source.SetActive("Local");
        Assert.Equal("Local", source.ActiveName);

        // Renaming the active environment keeps it active and strips completely empty rows.
        source.Save("Local", new EnvironmentDefinition
        {
            Name = "  Local Dev  ",
            Variables =
            [
                new("  BaseUrl ", "https://localhost:5001"),
                new("   ", ""),
            ],
        });
        Assert.Equal("Local Dev", source.ActiveName);
        Assert.Single(source.Active!.Variables);
        Assert.Equal("BaseUrl", source.Active.Variables[0].Key);

        // Saving a brand-new environment appends it; deleting the active one clears ActiveName.
        source.Save(null, new EnvironmentDefinition { Name = "QA", Variables = [new("BaseUrl", "https://qa.test")] });
        Assert.Equal(4, source.Environments.Count);

        source.Delete("Local Dev");
        Assert.Null(source.ActiveName);
        Assert.Equal(3, source.Environments.Count);
        Assert.True(events >= 5);
    }

    [Fact]
    public async Task Api_environment_source_refreshes_renames_and_deletes_against_server()
    {
        var serverEnvs = new List<EnvironmentDefinition>
        {
            new() { Name = "Staging", Variables = [new("BaseUrl", "https://staging.test")] },
            new() { Name = "Alpha", Variables = [new("BaseUrl", "https://alpha.test")] },
        };

        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (req.Method == HttpMethod.Get && path == "/api/environments")
                return StubHttpHandler.JsonResponse(serverEnvs);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };
        var api = new ApiEnvironmentSource(http);

        // Refresh sorts alphabetically and makes environments selectable.
        await api.RefreshAsync();
        Assert.Equal(["Alpha", "Staging"], api.Environments.Select(e => e.Name).ToArray());

        api.SetActive("Staging");
        Assert.Equal("Staging", api.Active?.Name);

        // Rename Staging -> Prod: PUT Prod + DELETE Staging, and Prod remains active.
        api.Save("Staging", new EnvironmentDefinition { Name = "Prod", Variables = [new("BaseUrl", "https://prod.test")] });
        Assert.Equal("Prod", api.ActiveName);
        await WaitFor(() => handler.HasRequest(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/api/environments/Staging"));
        Assert.True(handler.HasRequest(r => r.Method == HttpMethod.Put && r.PathAndQuery == "/api/environments/Prod"));

        // Deleting Prod clears ActiveName and issues DELETE /api/environments/Prod.
        api.Delete("Prod");
        Assert.Null(api.ActiveName);
        await WaitFor(() => handler.HasRequest(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/api/environments/Prod"));

        // If server list drops the active environment, RefreshAsync clears ActiveName cleanly.
        api.SetActive("Alpha");
        serverEnvs.Clear();
        await api.RefreshAsync();
        Assert.Null(api.ActiveName);
    }

    [Fact]
    public async Task Switchable_sources_delegate_to_fake_or_live_mode_and_switch_cleanly()
    {
        using var handler = new StubHttpHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/environments")
                return StubHttpHandler.JsonResponse(new List<EnvironmentDefinition>
                {
                    new() { Name = "LiveStaging", Variables = [new("BaseUrl", "https://live.test")] },
                });
            if (path == "/api/scenarios")
                return StubHttpHandler.JsonResponse(new List<ScenarioInfoDto>());
            if (path == "/api/plan")
                return StubHttpHandler.JsonResponse(new PlanWorkspaceState(new LoadPlan(), "Live", new LoadPlan(), false, 1));
            if (path == "/api/presets")
                return StubHttpHandler.JsonResponse(new List<Preset>());
            if (path == "/api/runs/current")
                return StubHttpHandler.JsonResponse(new LiveRunStatusDto("", "Idle", 0, DateTimeOffset.UtcNow, new LoadPlan(), [], [], -1, 0, "Memory", null));
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };

        var catalog = new ScenarioCatalog([new HttpGetScenario()]);
        var js = new FakeJsRuntime();
        var mode = new DataModeState(js, initialFakeMode: true);
        var fakeEnv = new FakeEnvironmentSource();
        var apiEnv = new ApiEnvironmentSource(http);
        var fakeRun = new FakeRunSource(catalog, simulateSeconds: 5);
        var apiRun = new ApiRunSource(http, catalog, apiEnv);
        var scripts = new ScriptApiClient(http, catalog);

        using var switchEnv = new SwitchableEnvironmentSource(mode, fakeEnv, apiEnv);
        using var switchRun = new SwitchableRunSource(mode, fakeRun, apiRun, scripts);

        var runChanges = 0;
        var envChanges = 0;
        switchRun.Changed += () => runChanges++;
        switchEnv.Changed += () => envChanges++;

        // In Fake mode, reads & edits hit FakeRunSource and FakeEnvironmentSource.
        Assert.Equal("Staging", switchEnv.ActiveName);
        var fakeCountBefore = switchRun.Plan.Scenarios.Count;
        switchRun.AddEntry(new HttpGetScenario());
        Assert.Equal(fakeCountBefore + 1, switchRun.Plan.Scenarios.Count);
        switchRun.RemoveEntry(switchRun.Plan.Scenarios.Count - 1);
        Assert.Equal(fakeCountBefore, switchRun.Plan.Scenarios.Count);
        Assert.NotEmpty(switchRun.PresetNames);
        switchRun.SavePreset("TempFake");
        switchRun.LoadPreset("TempFake");
        Assert.False(switchRun.HasUnsavedChanges);
        Assert.False(switchRun.IsEntryChanged(switchRun.Plan.Scenarios[0]));

        // Switching to Live mode delegates environment edits and deletes to ApiEnvironmentSource.
        mode.SetFakeMode(false);
        await WaitFor(() => switchEnv.Environments.Any(e => e.Name == "LiveStaging"));
        switchEnv.SetActive("LiveStaging");
        Assert.Equal("LiveStaging", switchEnv.Active?.Name);

        switchEnv.Save("LiveStaging", new EnvironmentDefinition { Name = "LiveStaging", Variables = [new("BaseUrl", "https://live2.test")] });
        await WaitFor(() => handler.HasRequest(r => r.Method == HttpMethod.Put && r.PathAndQuery == "/api/environments/LiveStaging"));
        Assert.Equal("https://live2.test", switchEnv.Active!.Variables[0].Value);

        switchEnv.Delete("LiveStaging");
        await WaitFor(() => handler.HasRequest(r => r.Method == HttpMethod.Delete && r.PathAndQuery == "/api/environments/LiveStaging"));
        Assert.Null(switchEnv.ActiveName);

        // Switching back to Fake mode restores Fake data without losing state.
        mode.SetFakeMode(true);
        Assert.Equal("Staging", switchEnv.ActiveName);
        Assert.True(runChanges > 0);
        Assert.True(envChanges > 0);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 80 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }
}
