using Beesharp.Client.Calls;
using Beesharp.Client.Chart;
using Beesharp.Client.Data;
using Beesharp.Contracts;
using Beesharp.Services.Scenarios;
using Beesharp.Services.Scenarios.BuiltIn;

namespace Beesharp.Client.Tests;

public class CallsFilterTests
{
    [Fact]
    public void CallsFilter_round_trips_query_string_with_1_based_scenario()
    {
        var f = CallsFilter.None
            .WithEndpoint("POST /payments")
            .WithEntry(1)
            .WithOutcome(BeeShow.Slow)
            .WithDurationRange(200, 500)
            .WithTimeWindow(12.5, 45.25)
            .WithSort("time")
            .WithSearch("pay");

        var qs = f.ToQueryString();
        Assert.Contains("scenario=2", qs);
        Assert.Contains("show=slow", qs);
        Assert.Contains("minMs=200", qs);

        var uri = f.Href();
        var q = uri[(uri.IndexOf('?') + 1)..];
        var parsed = CallsFilter.FromQuery("POST /payments", "2", "slow", "200", "500", "12.5", "45.25", "time", "pay");
        Assert.True(f.SameAs(parsed));
        Assert.Equal(1, parsed.Entry);
        Assert.Equal("POST /payments", parsed.Endpoint);
    }

    [Fact]
    public void FakeRunSource_populates_normalized_endpoints_adaptive_slugs_and_custom_thresholds()
    {
        using var source = new FakeRunSource(new ScenarioCatalog([
            new HttpGetScenario(),
            new HttpPostJsonScenario(),
            new CustomRequestScenario(),
            new UserJourneyScenario(),
        ]));

        var calls = source.Current.Store.AllKeptCalls();
        Assert.NotEmpty(calls);

        var endpoints = CallAnalytics.BuildEndpoints(calls);
        Assert.Contains(endpoints.Endpoints, e => e.Endpoint == "GET /catalog/{id}");
        Assert.Contains(endpoints.Endpoints, e => e.Endpoint == "GET /products/{id}");

        var payments = endpoints.Endpoints.Single(e => e.Endpoint == "POST /payments");
        Assert.True(payments.HasCustomThreshold);
        Assert.Equal(1200, payments.SlowThresholdMs);

        var detail = CallAnalytics.BuildEndpointDetail(calls, "GET /catalog/{id}", source.Current.Elapsed);
        Assert.NotNull(detail);
        Assert.All(detail!.Calls, c => Assert.StartsWith("https://shop.example.com/catalog/", c.Url));
    }
}
