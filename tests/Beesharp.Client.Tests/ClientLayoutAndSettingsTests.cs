using System.Net;
using Beesharp.Client.Data;
using Beesharp.Client.Environments;
using Beesharp.Client.Layout;

namespace Beesharp.Client.Tests;

/// <summary>
/// Stories for client layout, settings and UI state classes:
/// <see cref="ViewSettings"/>, <see cref="PhoneState"/>, <see cref="BuildInfo"/>,
/// <see cref="EnvironmentsPopupState"/> and <see cref="DataModeState"/>.
/// </summary>
public sealed class ClientLayoutAndSettingsTests
{
    [Fact]
    public async Task View_settings_persist_clamp_reset_and_survive_blocked_or_malformed_storage()
    {
        var js = new FakeJsRuntime();
        var settings = new ViewSettings(js);
        var changes = 0;
        settings.Changed += () => changes++;

        Assert.True(settings.IsDefault);
        settings.Open();
        Assert.True(settings.IsOpen);
        settings.Close();
        Assert.False(settings.IsOpen);

        // Set out-of-range values -> clamped and persisted to localStorage.
        await settings.SetAsync(zoom: 9.9, maxBars: 5);
        Assert.Equal(ViewSettings.ZoomMax, settings.Zoom);
        Assert.Equal(ViewSettings.MaxBarsMin, settings.MaxBars);
        Assert.False(settings.IsDefault);
        Assert.Equal("2.5|30", js.Storage["pt.view-settings"]);

        // Fresh instance loads and clamps stored values.
        js.Storage["pt.view-settings"] = "1.75|250";
        var restored = new ViewSettings(js);
        await restored.LoadAsync();
        Assert.Equal(1.75, restored.Zoom);
        Assert.Equal(250, restored.MaxBars);

        // Reset restores defaults.
        await restored.ResetAsync();
        Assert.True(restored.IsDefault);

        // Malformed or blocked storage falls back silently to defaults without throwing.
        js.Storage["pt.view-settings"] = "corrupt";
        var malformed = new ViewSettings(js);
        await malformed.LoadAsync();
        Assert.True(malformed.IsDefault);

        js.ThrowOnInvoke = true;
        await malformed.LoadAsync();
        await malformed.SetAsync(zoom: 1.5);
        Assert.Equal(1.5, malformed.Zoom);
        Assert.True(changes > 0);
    }

    [Theory]
    [InlineData("http://localhost:16686/trace/{traceId}", "4d7ce6ac47fbe9b3822d67a473ce9774", null, "http://localhost:16686/trace/4d7ce6ac47fbe9b3822d67a473ce9774")]
    [InlineData("https://kibana.example.com/app/apm/traces/{traceId}?span={spanId}", "abc123", "span456", "https://kibana.example.com/app/apm/traces/abc123?span=span456")]
    [InlineData("https://kibana.example.com/app/discover#/?_a=(query:(language:kuery,query:'trace.id:{traceId}'))", "abc123", null, "https://kibana.example.com/app/discover#/?_a=(query:(language:kuery,query:'trace.id:abc123'))")]
    [InlineData("http://localhost:16686/trace/{traceId}?s={spanId}", "id 1/2", "sp&1", "http://localhost:16686/trace/id%201%2F2?s=sp%261")]
    public void Build_trace_url_expands_placeholders_for_http_and_https(string template, string traceId, string? spanId, string expected)
    {
        Assert.Equal(expected, ViewSettings.BuildTraceUrl(template, traceId, spanId));
    }

    [Theory]
    [InlineData(null, "4d7ce6ac47fbe9b3822d67a473ce9774")]
    [InlineData("", "4d7ce6ac47fbe9b3822d67a473ce9774")]
    [InlineData("   ", "4d7ce6ac47fbe9b3822d67a473ce9774")]
    [InlineData("http://localhost:16686/trace/{traceId}", null)]
    [InlineData("http://localhost:16686/trace/{traceId}", "")]
    [InlineData("http://localhost:16686/trace/{traceId}", "   ")]
    [InlineData("javascript:alert('{traceId}')", "4d7ce6ac47fbe9b3822d67a473ce9774")]
    [InlineData("file:///tmp/{traceId}", "4d7ce6ac47fbe9b3822d67a473ce9774")]
    [InlineData("/trace/{traceId}", "4d7ce6ac47fbe9b3822d67a473ce9774")]
    public void Build_trace_url_rejects_blank_inputs_and_non_http_schemes(string? template, string? traceId)
    {
        Assert.Null(ViewSettings.BuildTraceUrl(template, traceId));
    }

    [Fact]
    public async Task Trace_url_template_loads_server_default_persists_user_override_and_falls_back_on_reset()
    {
        var js = new FakeJsRuntime();
        var handler = new StubHttpHandler(_ => StubHttpHandler.JsonResponse(new TracingUiConfig("http://jaeger.local:16686/trace/{traceId}")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://beesharp.test/") };

        var settings = new ViewSettings(js, http);
        await settings.LoadAsync();

        Assert.Equal("http://jaeger.local:16686/trace/{traceId}", settings.ServerTraceUrlTemplate);
        Assert.Null(settings.TraceUrlTemplate);
        Assert.Equal("http://jaeger.local:16686/trace/{traceId}", settings.EffectiveTraceUrlTemplate);
        Assert.Equal("http://jaeger.local:16686/trace/abc123", settings.ResolveTraceUrl("abc123"));
        Assert.True(settings.IsDefault);

        // User browser override wins over server default and persists in localStorage.
        await settings.SetTraceUrlAsync("  https://custom.trace/{traceId}/{spanId}  ");
        Assert.False(settings.IsDefault);
        Assert.Equal("https://custom.trace/{traceId}/{spanId}", settings.TraceUrlTemplate);
        Assert.Equal("https://custom.trace/{traceId}/{spanId}", js.Storage["pt.trace-url"]);
        Assert.Equal("https://custom.trace/abc123/def456", settings.ResolveTraceUrl("abc123", "def456"));

        // Second LoadAsync does not re-fetch server config, and restores localStorage override.
        await settings.LoadAsync();
        Assert.Equal(1, handler.CountRequests(r => r.PathAndQuery == "/api/tracing/config"));

        // Reset clears only the user override and falls back to the server default.
        await settings.ResetAsync();
        Assert.True(settings.IsDefault);
        Assert.Null(settings.TraceUrlTemplate);
        Assert.False(js.Storage.ContainsKey("pt.trace-url"));
        Assert.Equal("http://jaeger.local:16686/trace/abc123", settings.ResolveTraceUrl("abc123"));

        // Failures fetching server config (e.g. Fake mode / GitHub Pages) are ignored silently.
        var failingHandler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var failingHttp = new HttpClient(failingHandler) { BaseAddress = new Uri("https://beesharp.test/") };
        var offline = new ViewSettings(new FakeJsRuntime(), failingHttp);
        await offline.LoadAsync();
        Assert.Null(offline.ServerTraceUrlTemplate);
        Assert.Null(offline.ResolveTraceUrl("abc123"));
    }

    [Fact]
    public void Phone_state_mirrors_browser_viewport_and_ignores_duplicate_or_js_error_signals()
    {
        var js = new FakeJsRuntime { IsPhoneResult = true };
        using var phone = new PhoneState(js);
        var changes = 0;
        phone.Changed += () => changes++;

        Assert.True(phone.IsPhone);
        Assert.Contains("beesharpPhone.is", js.Invocations);
        Assert.Contains("beesharpPhone.listen", js.Invocations);

        // No-op callback does not fire Changed; actual switch fires Changed.
        phone.OnChanged(true);
        Assert.Equal(0, changes);
        phone.OnChanged(false);
        Assert.False(phone.IsPhone);
        Assert.Equal(1, changes);

        // When JS interop throws (e.g. non-browser environment), PhoneState stays safely false.
        js.ThrowOnInvoke = true;
        using var fallback = new PhoneState(js);
        Assert.False(fallback.IsPhone);
    }

    [Fact]
    public void Build_info_formats_commit_sha_url_and_deploy_time()
    {
        var local = new BuildInfo(null, null);
        Assert.Equal("local", local.ShortSha);
        Assert.Null(local.CommitUrl);
        Assert.Equal("", local.TimeText);

        var deployed = new BuildInfo("9721f86abcdef", new DateTime(2026, 11, 24, 21, 30, 0, DateTimeKind.Utc));
        Assert.Equal("9721f86", deployed.ShortSha);
        Assert.Equal($"{BuildInfo.Repo}/commit/9721f86abcdef", deployed.CommitUrl);
        Assert.Equal("24 Nov 2026 21:30 UTC", deployed.TimeText);

        // Current assembly metadata read works without throwing.
        Assert.NotNull(BuildInfo.Current.ShortSha);
    }

    [Fact]
    public async Task Environments_popup_and_data_mode_state_track_open_state_and_mode_toggles()
    {
        // EnvironmentsPopupState increments Version on every Show so the dialog resets cleanly.
        var popup = new EnvironmentsPopupState();
        var popupEvents = 0;
        popup.Changed += () => popupEvents++;

        popup.Show(select: "Staging", addKey: "ApiToken");
        Assert.True(popup.IsOpen);
        Assert.Equal("Staging", popup.Select);
        Assert.Equal("ApiToken", popup.AddKey);
        Assert.Equal(1, popup.Version);

        popup.Close();
        Assert.False(popup.IsOpen);
        Assert.Equal(2, popupEvents);

        // DataModeState loads stored preference, toggles, skips redundant writes, and tolerates JS errors.
        var js = new FakeJsRuntime();
        js.Storage["perftool.fakeMode"] = "false";
        var mode = new DataModeState(js, initialFakeMode: true);
        var modeEvents = 0;
        mode.Changed += () => modeEvents++;

        await mode.LoadAsync();
        Assert.False(mode.IsFakeMode);
        Assert.Equal(1, modeEvents);

        mode.SetFakeMode(false); // no-op
        Assert.Equal(1, modeEvents);

        mode.Toggle();
        Assert.True(mode.IsFakeMode);
        Assert.Equal("true", js.Storage["perftool.fakeMode"]);

        js.ThrowOnInvoke = true;
        await mode.LoadAsync();
        mode.Toggle();
        Assert.False(mode.IsFakeMode);
    }
}
