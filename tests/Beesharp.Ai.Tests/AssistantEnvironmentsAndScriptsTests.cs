using System.Text.Json;
using Beesharp.Ai;
using Beesharp.Ai.Contracts;

namespace Beesharp.Ai.Tests;

/// <summary>
/// Focused stories for the assistant's environment, scenario script, and preflight tools
/// (<c>AssistantToolbox.Environments.cs</c>): secret masking across updates, iterative script authoring,
/// preflight authoring, test-running, and cleanup.
/// </summary>
public sealed class AssistantEnvironmentsAndScriptsTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static JsonElement ToJson(object result) => JsonSerializer.SerializeToElement(result, Json);

    [Fact]
    public async Task Operator_manages_environments_with_secret_masking_key_removal_and_deletion()
    {
        var envs = new InMemoryEnvironmentPort();
        var rec = new TurnRecorder();
        var catalog = TestCatalogFactory.CreateWithSteps();
        var toolbox = new AssistantToolbox(
            new InMemoryPlanPort(catalog),
            new InMemoryRunPort(),
            new InMemoryHistoryPort(),
            envs,
            new InMemoryScriptPort(),
            catalog,
            rec);

        // 1. Create a Staging environment with a public URL, a short secret key, and a long bearer token.
        var created = ToJson(await toolbox.SaveEnvironment(
            "  Staging  ",
            variables: new Dictionary<string, string>
            {
                ["BaseUrl"] = "https://staging.shop.test",
                ["ApiKey"] = "shortkey",
                ["AccessToken"] = "tok-very-long-secret-value",
                ["TemporaryFlag"] = "true",
            }));

        Assert.Equal("Staging", created.GetProperty("name").GetString());
        Assert.Equal("https://staging.shop.test", created.GetProperty("variables").GetProperty("BaseUrl").GetString());
        Assert.Equal("••••", created.GetProperty("variables").GetProperty("ApiKey").GetString());
        Assert.Equal("to••••", created.GetProperty("variables").GetProperty("AccessToken").GetString());
        Assert.Contains("masked", created.GetProperty("note").GetString());

        // 2. List and read environments — secrets remain masked everywhere.
        Assert.Single(ToJson(await toolbox.ListEnvironments(default)).EnumerateArray());

        var fetched = ToJson(await toolbox.GetEnvironment("Staging", default));
        Assert.DoesNotContain("tok-very-long-secret-value", fetched.GetRawText());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => toolbox.GetEnvironment("Prod", default));

        // 3. Update Staging: passing back the masked token preserves the stored secret while removing TemporaryFlag.
        await toolbox.SaveEnvironment(
            "Staging",
            variables: new Dictionary<string, string>
            {
                ["BaseUrl"] = "https://staging2.shop.test",
                ["AccessToken"] = "to••••",
            },
            removeKeys: ["TemporaryFlag"]);

        var storedVars = envs.Stored["Staging"].Variables.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("https://staging2.shop.test", storedVars["BaseUrl"]);
        Assert.Equal("tok-very-long-secret-value", storedVars["AccessToken"]);
        Assert.False(storedVars.ContainsKey("TemporaryFlag"));

        // 4. Delete Staging and confirm deleting again reports KeyNotFoundException.
        var deleted = ToJson(await toolbox.DeleteEnvironment("Staging", default));
        Assert.Equal("Staging", deleted.GetProperty("deleted").GetString());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => toolbox.DeleteEnvironment("Staging", default));
    }

    [Fact]
    public async Task Operator_iterates_on_a_scenario_script_bounds_large_reads_test_runs_and_deletes_it()
    {
        var scripts = new InMemoryScriptPort();
        var rec = new TurnRecorder();
        var catalog = TestCatalogFactory.CreateWithSteps();
        var toolbox = new AssistantToolbox(
            new InMemoryPlanPort(catalog),
            new InMemoryRunPort(),
            new InMemoryHistoryPort(),
            new InMemoryEnvironmentPort(),
            scripts,
            catalog,
            rec);

        // 1. First upload fails compilation -> returns diagnostics and does NOT mark Scripts area as changed.
        scripts.FailNextScriptCompile = true;
        var failedCompile = ToJson(await toolbox.UploadScript("broken code", "checkout.cs"));
        Assert.False(failedCompile.GetProperty("success").GetBoolean());
        Assert.Single(failedCompile.GetProperty("diagnostics").EnumerateArray());
        Assert.DoesNotContain(ChangedAreas.Scripts, rec.Changed);

        // 2. Second upload succeeds (>20k chars verifies codeCut bounding when read back).
        var hugeCode = "// " + new string('a', 21_000);
        var uploaded = ToJson(await toolbox.UploadScript(hugeCode, "checkout.cs", "checkout"));
        Assert.True(uploaded.GetProperty("success").GetBoolean());
        Assert.Contains(ChangedAreas.Scripts, rec.Changed);

        Assert.Single(ToJson(toolbox.ListScripts()).EnumerateArray());
        var readScript = ToJson(toolbox.GetScript("checkout"));
        Assert.True(readScript.GetProperty("codeCut").GetBoolean());
        Assert.Throws<KeyNotFoundException>(() => toolbox.GetScript("nope"));

        // 3. Test-run script: first passing, then failing.
        var okRun = ToJson(await toolbox.TestRunScript("checkout", environmentName: "Staging"));
        Assert.True(okRun.GetProperty("success").GetBoolean());
        Assert.Single(okRun.GetProperty("calls").EnumerateArray());

        scripts.FailNextScriptTestRun = true;
        var failRun = ToJson(await toolbox.TestRunScript("checkout"));
        Assert.False(failRun.GetProperty("success").GetBoolean());
        Assert.Equal("Gateway timeout", failRun.GetProperty("error").GetString());

        // 4. Delete script and confirm missing delete throws.
        Assert.Equal("checkout", ToJson(toolbox.DeleteScript("checkout")).GetProperty("deleted").GetString());
        Assert.Throws<KeyNotFoundException>(() => toolbox.DeleteScript("checkout"));
    }

    [Fact]
    public async Task Operator_uploads_fixes_test_runs_and_deletes_a_preflight_program()
    {
        var scripts = new InMemoryScriptPort();
        var rec = new TurnRecorder();
        var catalog = TestCatalogFactory.CreateWithSteps();
        var toolbox = new AssistantToolbox(
            new InMemoryPlanPort(catalog),
            new InMemoryRunPort(),
            new InMemoryHistoryPort(),
            new InMemoryEnvironmentPort(),
            scripts,
            catalog,
            rec);

        scripts.FailNextPreflightCompile = true;
        var badPre = ToJson(await toolbox.UploadPreflight("broken", "ready.cs"));
        Assert.False(badPre.GetProperty("success").GetBoolean());
        Assert.DoesNotContain(ChangedAreas.Preflights, rec.Changed);

        var okPre = ToJson(await toolbox.UploadPreflight("Log(\"ready\");", "ready.cs", "ready"));
        Assert.True(okPre.GetProperty("success").GetBoolean());
        Assert.Contains(ChangedAreas.Preflights, rec.Changed);

        Assert.Single(ToJson(toolbox.ListPreflights()).EnumerateArray());
        Assert.Equal("ready.cs", ToJson(toolbox.GetPreflight("ready")).GetProperty("fileName").GetString());
        Assert.Throws<KeyNotFoundException>(() => toolbox.GetPreflight("nope"));

        var preOk = ToJson(await toolbox.TestRunPreflight("ready", "Staging"));
        Assert.True(preOk.GetProperty("success").GetBoolean());

        scripts.FailNextPreflightTestRun = true;
        var preFail = ToJson(await toolbox.TestRunPreflight("ready"));
        Assert.False(preFail.GetProperty("success").GetBoolean());
        Assert.Equal("Database not ready", preFail.GetProperty("error").GetString());

        Assert.Equal("ready", ToJson(toolbox.DeletePreflight("ready")).GetProperty("deleted").GetString());
        Assert.Throws<KeyNotFoundException>(() => toolbox.DeletePreflight("ready"));
    }
}
