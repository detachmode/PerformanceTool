using Beesharp.Services.Environments;
using Beesharp.Services.TraceStore;

namespace Beesharp.TraceStore.Tests;

/// <summary>The trace store lives in the run's Environment (brain 0033, 0065): which keys, which defaults, which mistakes are named.</summary>
public class TraceStoreSettingsTests
{
    private static TraceStoreResolution Resolve(Dictionary<string, string> values, Func<string, string?>? env = null) =>
        TraceStoreSettings.FromEnvironment(values, env);

    [Fact]
    public void No_trace_store_keys_means_nothing_is_fetched()
    {
        var resolution = Resolve(new() { ["BaseUrl"] = "http://shop" });
        Assert.False(resolution.IsConfigured);
        Assert.Null(resolution.Problem);
        Assert.False(TraceStoreSettings.FromEnvironment(EnvironmentValues.None).IsConfigured);
        Assert.Null(TraceStoreSettings.FromEnvironment(EnvironmentValues.None).Problem);
    }

    [Fact]
    public void A_url_alone_is_a_jaeger_store_and_the_tuning_keys_are_read()
    {
        var resolution = Resolve(new()
        {
            [TraceStoreSettings.UrlKey] = " http://localhost:16686/ ",
            [TraceStoreSettings.MaxTracesKey] = "120",
            [TraceStoreSettings.SettleSecondsKey] = "7.5",
        });

        var settings = resolution.Settings!;
        Assert.Equal(("jaeger", "http://localhost:16686", null, 120, 7.5), (settings.Kind, settings.Url, settings.Token, settings.MaxTraces, settings.SettleSeconds));
    }

    [Fact]
    public void The_token_is_taken_literally_or_from_an_operating_system_variable()
    {
        Assert.Equal("abc", Resolve(new() { [TraceStoreSettings.UrlKey] = "http://j", [TraceStoreSettings.TokenKey] = "abc" }).Settings!.Token);

        var viaEnv = Resolve(
            new() { [TraceStoreSettings.UrlKey] = "http://j", [TraceStoreSettings.TokenKey] = "${env:JAEGER_TOKEN}" },
            name => name == "JAEGER_TOKEN" ? "from-env" : null);
        Assert.Equal("from-env", viaEnv.Settings!.Token);

        var unset = Resolve(new() { [TraceStoreSettings.UrlKey] = "http://j", [TraceStoreSettings.TokenKey] = "${env:JAEGER_TOKEN}" }, _ => null);
        Assert.False(unset.IsConfigured);
        Assert.Contains("JAEGER_TOKEN", unset.Problem);
    }

    [Theory]
    [InlineData("elastic", "http://es", "not supported; supported: jaeger")]
    [InlineData("jaeger", null, "TraceStore.Url is missing")]
    [InlineData("jaeger", "localhost:16686", "not an http:// or https:// URL")]
    [InlineData("jaeger", "ftp://x", "not an http:// or https:// URL")]
    public void A_mistake_is_named_and_nothing_is_fetched(string kind, string? url, string problem)
    {
        var values = new Dictionary<string, string> { [TraceStoreSettings.KindKey] = kind };
        if (url is not null) values[TraceStoreSettings.UrlKey] = url;

        var resolution = Resolve(values);

        Assert.False(resolution.IsConfigured);
        Assert.Contains(problem, resolution.Problem);
    }

    [Theory]
    [InlineData("TraceStore.MaxTraces", "0")]
    [InlineData("TraceStore.MaxTraces", "many")]
    [InlineData("TraceStore.SettleSeconds", "-1")]
    public void A_bad_tuning_value_is_named(string key, string value)
    {
        var resolution = Resolve(new() { [TraceStoreSettings.UrlKey] = "http://j", [key] = value });
        Assert.False(resolution.IsConfigured);
        Assert.Contains(key, resolution.Problem);
    }
}
