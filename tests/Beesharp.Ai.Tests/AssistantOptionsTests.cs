using Microsoft.Extensions.Configuration;
using Beesharp.Ai;

namespace Beesharp.Ai.Tests;

/// <summary>The assistant is configured from environment variables (brain 0047).</summary>
public sealed class AssistantOptionsTests
{
    [Fact]
    public void Options_bind_from_environment_variable_names()
    {
        var names = new Dictionary<string, string>
        {
            ["AI__Provider"] = "OpenAI", ["AI__Endpoint"] = "http://localhost:11434/v1", ["AI__ApiKey"] = "secret", ["AI__Model"] = "llama3", ["AI__MaxToolCalls"] = "7", ["AI__SystemPrompt"] = "Be brief.",
        };
        try
        {
            foreach (var (k, v) in names) Environment.SetEnvironmentVariable(k, v);
            var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            services.AddBeesharpAi(config);
            var options = (AssistantOptions)services.First(d => d.ServiceType == typeof(AssistantOptions)).ImplementationInstance!;
            Assert.Equal("llama3", options.Model);
            Assert.Equal("http://localhost:11434/v1", options.Endpoint);
            Assert.Equal("secret", options.ApiKey);
            Assert.Equal(7, options.MaxToolCalls);
            Assert.Equal("Be brief.", options.SystemPrompt);
            Assert.True(options.IsConfigured);
        }
        finally { foreach (var k in names.Keys) Environment.SetEnvironmentVariable(k, null); }
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("gpt", null, null, false)]
    [InlineData("gpt", "key", null, true)]
    [InlineData("llama", null, "http://localhost:11434/v1", true)]
    public void Configured_needs_a_model_and_a_key_or_an_endpoint(string? model, string? key, string? endpoint, bool expected) =>
        Assert.Equal(expected, new AssistantOptions { Model = model, ApiKey = key, Endpoint = endpoint }.IsConfigured);

    [Fact]
    public void Unknown_provider_is_not_configured()
    {
        var o = new AssistantOptions { Provider = "Other", Model = "m", ApiKey = "k" };
        Assert.False(o.IsConfigured);
        Assert.Contains("Other", o.NotConfiguredReason);
    }
}
