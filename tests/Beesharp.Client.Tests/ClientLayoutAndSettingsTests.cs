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
