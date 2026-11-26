using Beesharp.Services.Tracing;
using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

namespace Beesharp.Tracing.Tests;

public class TracingSettingsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void Export_is_off_unless_an_endpoint_is_configured()
    {
        var settings = TracingSettings.FromConfiguration(Config());
        Assert.False(settings.Enabled);
        Assert.Null(settings.OtlpEndpoint);
        Assert.Equal("beesharp", settings.ServiceName);
        Assert.False(TracingSettings.Off.Enabled);
    }

    [Fact]
    public void Appsettings_section_enables_export_over_http_protobuf_by_default()
    {
        var settings = TracingSettings.FromConfiguration(Config(("Tracing:OtlpEndpoint", "http://collector:4318")));
        Assert.True(settings.Enabled);
        Assert.Equal(new Uri("http://collector:4318/v1/traces"), settings.OtlpEndpoint);
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, settings.Protocol);
    }

    [Fact]
    public void Grpc_is_used_when_asked_for_and_keeps_the_endpoint_as_written()
    {
        var settings = TracingSettings.FromConfiguration(Config(("Tracing:OtlpEndpoint", "http://collector:4317"), ("Tracing:Protocol", "grpc")));
        Assert.Equal(OtlpExportProtocol.Grpc, settings.Protocol);
        Assert.Equal(new Uri("http://collector:4317"), settings.OtlpEndpoint);
    }

    [Fact]
    public void Standard_otel_environment_variables_are_honoured()
    {
        var settings = TracingSettings.FromConfiguration(Config(
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "grpc"),
            ("OTEL_SERVICE_NAME", "load-lab")));
        Assert.Equal(new Uri("http://collector:4317"), settings.OtlpEndpoint);
        Assert.Equal("load-lab", settings.ServiceName);
    }

    [Fact]
    public void The_tracing_section_wins_over_the_otel_environment_variables()
    {
        var settings = TracingSettings.FromConfiguration(Config(
            ("Tracing:OtlpEndpoint", "http://mine:4318"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://theirs:4318"),
            ("Tracing:ServiceName", "mine"),
            ("OTEL_SERVICE_NAME", "theirs")));
        Assert.Equal(new Uri("http://mine:4318/v1/traces"), settings.OtlpEndpoint);
        Assert.Equal("mine", settings.ServiceName);
    }

    [Theory]
    [InlineData("http://localhost:4318", "http://localhost:4318/v1/traces")]
    [InlineData("http://localhost:4318/", "http://localhost:4318/v1/traces")]
    [InlineData("http://collector/otlp", "http://collector/otlp/v1/traces")]
    [InlineData("http://localhost:4318/v1/traces", "http://localhost:4318/v1/traces")]
    public void Http_protobuf_gets_the_traces_path_the_dotnet_exporter_needs(string configured, string expected)
    {
        var settings = TracingSettings.FromConfiguration(Config(("Tracing:OtlpEndpoint", configured)));
        Assert.Equal(OtlpExportProtocol.HttpProtobuf, settings.Protocol);
        Assert.Equal(new Uri(expected), settings.OtlpEndpoint);
    }

    [Fact]
    public void A_traces_specific_endpoint_is_used_as_written()
    {
        var settings = TracingSettings.FromConfiguration(Config(
            ("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://collector:4318/custom/path"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")));
        Assert.Equal(new Uri("http://collector:4318/custom/path"), settings.OtlpEndpoint);
    }

    [Fact]
    public void Sdk_disabled_turns_export_off_even_with_an_endpoint()
    {
        var settings = TracingSettings.FromConfiguration(Config(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4318"), ("OTEL_SDK_DISABLED", "true")));
        Assert.False(settings.Enabled);
    }

    [Fact]
    public void A_bad_protocol_or_endpoint_fails_naming_the_valid_values()
    {
        var protocol = Assert.Throws<InvalidOperationException>(() => TracingSettings.FromConfiguration(Config(("Tracing:OtlpEndpoint", "http://x:1"), ("Tracing:Protocol", "http/json"))));
        Assert.Contains("http/protobuf (default), grpc", protocol.Message);
        var endpoint = Assert.Throws<InvalidOperationException>(() => TracingSettings.FromConfiguration(Config(("Tracing:OtlpEndpoint", "localhost:4318"))));
        Assert.Contains("http://localhost:4318", endpoint.Message);
    }
}
