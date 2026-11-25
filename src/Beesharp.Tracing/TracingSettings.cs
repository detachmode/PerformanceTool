using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

namespace Beesharp.Services.Tracing;

/// <summary>
/// Where Beesharp's own spans are exported (brain 0062). Export is <b>off unless an endpoint is configured</b>. Read from configuration with
/// <see cref="FromConfiguration"/>: the section <c>Tracing</c> (<c>Tracing:OtlpEndpoint</c>, <c>Tracing:Protocol</c>, <c>Tracing:ServiceName</c>; <c>Tracing__OtlpEndpoint</c> as an environment variable)
/// wins over the standard OpenTelemetry environment variables (<c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, <c>OTEL_EXPORTER_OTLP_PROTOCOL</c>, <c>OTEL_SERVICE_NAME</c>).
/// </summary>
/// <param name="OtlpEndpoint">The collector's OTLP endpoint as the exporter needs it, or <see langword="null"/> when export is off.</param>
/// <param name="Protocol">The OTLP transport.</param>
/// <param name="ServiceName">The <c>service.name</c> of Beesharp's spans.</param>
public sealed record TracingSettings(Uri? OtlpEndpoint, OtlpExportProtocol Protocol, string ServiceName)
{
    /// <summary>The service name when nothing configures one.</summary>
    public const string DefaultServiceName = "beesharp";

    /// <summary>Settings with export off.</summary>
    public static TracingSettings Off { get; } = new(null, OtlpExportProtocol.HttpProtobuf, DefaultServiceName);

    /// <summary>Whether spans are exported to a collector.</summary>
    public bool Enabled => OtlpEndpoint is not null;

    /// <summary>Reads the settings. The protocol defaults to <c>http/protobuf</c> (port 4318), the OpenTelemetry spec default. Throws <see cref="InvalidOperationException"/> naming the valid values for an endpoint that is no absolute URL or a protocol that is not <c>http/protobuf</c> or <c>grpc</c>.</summary>
    /// <param name="configuration">Application configuration; in a host it includes the environment variables.</param>
    /// <returns>The resolved settings; <see cref="Enabled"/> is <see langword="false"/> without an endpoint or with <c>OTEL_SDK_DISABLED=true</c>.</returns>
    public static TracingSettings FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var serviceName = First(configuration["Tracing:ServiceName"], configuration["OTEL_SERVICE_NAME"]) ?? DefaultServiceName;
        var sdkDisabled = string.Equals(configuration["OTEL_SDK_DISABLED"], "true", StringComparison.OrdinalIgnoreCase);

        // A tracing-specific OTEL endpoint is used as written; a general one is a base URL, which gets /v1/traces for HTTP (the OpenTelemetry spec).
        var configured = First(configuration["Tracing:OtlpEndpoint"], configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var tracesSpecific = configured is null ? configuration["OTEL_EXPORTER_OTLP_TRACES_ENDPOINT"] : null;
        var protocolText = First(configuration["Tracing:Protocol"], configuration["OTEL_EXPORTER_OTLP_TRACES_PROTOCOL"], configuration["OTEL_EXPORTER_OTLP_PROTOCOL"]);
        var protocol = ParseProtocol(protocolText);

        var text = First(tracesSpecific, configured);
        if (text is null || sdkDisabled) return new TracingSettings(null, protocol, serviceName);
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Tracing endpoint '{text}' is not an absolute http(s) URL, e.g. http://localhost:4318 (http/protobuf, the default) or http://localhost:4317 (grpc).");

        return new TracingSettings(tracesSpecific is not null ? uri : ForProtocol(uri, protocol), protocol, serviceName);
    }

    private static OtlpExportProtocol ParseProtocol(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "http/protobuf" or "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
        "grpc" => OtlpExportProtocol.Grpc,
        _ => throw new InvalidOperationException($"Tracing protocol '{text}' is not supported. Supported: http/protobuf (default), grpc."),
    };

    /// <summary>The .NET exporter sends HTTP to exactly the URL it is given, so the base endpoint gets the traces path.</summary>
    private static Uri ForProtocol(Uri endpoint, OtlpExportProtocol protocol)
    {
        if (protocol != OtlpExportProtocol.HttpProtobuf) return endpoint;
        var path = endpoint.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/v1/traces", StringComparison.Ordinal) ? endpoint : new UriBuilder(endpoint) { Path = path + "/v1/traces" }.Uri;
    }

    private static string? First(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
}
