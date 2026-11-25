using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using Beesharp.Services.Scenarios.BuiltIn;
using Beesharp.Services.Scenarios.Spec;

namespace Beesharp.Core.Tests;

/// <summary>
/// Stories for <see cref="ParameterSummary"/>: turning stored scenario parameter JSON into
/// clean, human-readable lines for read-only views without per-scenario UI code (brain 0007).
/// </summary>
public sealed class ParameterSummaryTests
{
    public enum RetryMode
    {
        [Display(Name = "Exponential backoff")] Exponential,
        Linear,
    }

    public sealed class NestedCredentials
    {
        [Display(Name = "API Key"), Secret]
        public string ApiKey { get; set; } = "";

        [Display(Name = "Region")]
        public string Region { get; set; } = "eu-central";
    }

    public sealed class RichScenarioParameters
    {
        [Display(Name = "Target URL"), HttpUrl]
        public string Url { get; set; } = "https://shop.test";

        [Display(Name = "Follow redirects")]
        public bool FollowRedirects { get; set; } = true;

        [Display(Name = "Retry mode")]
        public RetryMode Mode { get; set; } = RetryMode.Exponential;

        [Display(Name = "Think time")]
        public TimeSpan ThinkTime { get; set; } = TimeSpan.FromMilliseconds(750);

        [Display(Name = "Seed SKUs")]
        public List<string> Skus { get; set; } = [];

        [Display(Name = "Extra headers")]
        public Dictionary<string, string> Headers { get; set; } = [];

        [Display(Name = "Batch size")]
        public int BatchSize { get; set; } = 10;

        [Display(Name = "Credentials")]
        public NestedCredentials Credentials { get; set; } = new();
    }

    [Fact]
    public void Summary_formats_every_field_kind_masks_secrets_and_indents_nested_groups()
    {
        var spec = ParameterSpec.For(typeof(RichScenarioParameters));
        var values = new JsonObject
        {
            ["url"] = "https://api.shop.test/checkout",
            ["followRedirects"] = true,
            ["mode"] = "Exponential",
            ["thinkTime"] = "00:00:01.500",
            ["skus"] = new JsonArray("BEE-1", "BEE-2"),
            ["headers"] = new JsonObject { ["X-Tenant"] = "acme", ["Accept"] = "application/json" },
            ["batchSize"] = 25,
            ["credentials"] = new JsonObject
            {
                ["apiKey"] = "sk-super-secret-value",
                ["region"] = "us-east",
            },
        };

        var lines = ParameterSummary.Describe(spec, values);

        Assert.Contains(new SummaryLine("Target URL", "https://api.shop.test/checkout"), lines);
        Assert.Contains(new SummaryLine("Follow redirects", "on"), lines);
        Assert.Contains(new SummaryLine("Retry mode", "Exponential backoff"), lines);
        Assert.Contains(new SummaryLine("Think time", "1.5s"), lines);
        Assert.Contains(new SummaryLine("Seed SKUs", "BEE-1\nBEE-2"), lines);
        Assert.Contains(new SummaryLine("Extra headers", "X-Tenant: acme\nAccept: application/json"), lines);
        Assert.Contains(new SummaryLine("Batch size", "25"), lines);

        // Nested group produces a heading line at depth 0, followed by depth-1 children with secrets masked.
        Assert.Contains(new SummaryLine("Credentials", "", Depth: 0, Heading: true), lines);
        Assert.Contains(new SummaryLine("API Key", "••••••", Depth: 1), lines);
        Assert.Contains(new SummaryLine("Region", "us-east", Depth: 1), lines);
        Assert.DoesNotContain(lines, l => l.Value.Contains("sk-super-secret-value"));
    }

    [Fact]
    public void Summary_handles_empty_missing_invalid_and_false_values_gracefully()
    {
        var spec = ParameterSpec.For(typeof(RichScenarioParameters));
        var sparse = new JsonObject
        {
            ["url"] = "",
            ["followRedirects"] = false,
            ["mode"] = "UnknownLegacyChoice",
            ["thinkTime"] = "not-a-duration",
            ["skus"] = new JsonArray(),
            ["headers"] = new JsonObject(),
            ["batchSize"] = null,
            // credentials omitted completely -> group still renders with blank child values
        };

        var lines = ParameterSummary.Describe(spec, sparse);

        Assert.Contains(new SummaryLine("Target URL", "—"), lines);
        Assert.Contains(new SummaryLine("Follow redirects", "off"), lines);
        Assert.Contains(new SummaryLine("Retry mode", "UnknownLegacyChoice"), lines);
        Assert.Contains(new SummaryLine("Think time", "not-a-duration"), lines);
        Assert.Contains(new SummaryLine("Seed SKUs", "—"), lines);
        Assert.Contains(new SummaryLine("Extra headers", "—"), lines);
        Assert.Contains(new SummaryLine("Batch size", "—"), lines);
        Assert.Contains(new SummaryLine("Credentials", "", Depth: 0, Heading: true), lines);
        Assert.Contains(new SummaryLine("API Key", "—", Depth: 1), lines);
        Assert.Contains(new SummaryLine("Region", "—", Depth: 1), lines);
    }

    [Fact]
    public void Built_in_custom_request_summary_masks_token_and_password_while_showing_method_and_headers()
    {
        var scenario = new CustomRequestScenario();
        var values = scenario.Spec.DefaultJson();
        values["method"] = "POST";
        values["auth"]!["scheme"] = "Bearer";
        values["auth"]!["token"] = "bearer-secret-token";
        values["auth"]!["password"] = "";

        var lines = ParameterSummary.Describe(scenario.Spec, values);

        Assert.Contains(new SummaryLine("Method", "POST"), lines);
        Assert.Contains(new SummaryLine("Authentication", "", Depth: 0, Heading: true), lines);
        Assert.Contains(new SummaryLine("Scheme", "Bearer token", Depth: 1), lines);
        Assert.Contains(new SummaryLine("Token", "••••••", Depth: 1), lines);
        Assert.Contains(new SummaryLine("Password", "—", Depth: 1), lines);
    }
}
