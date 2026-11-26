using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Beesharp.Services.Tracing;

/// <summary>
/// Registers the OpenTelemetry SDK for Beesharp's own spans (brain 0063). Beesharp's spans are the actor, step and call spans of <see cref="BeesharpTracing.Source"/> and nothing else:
/// no ASP.NET Core or HttpClient instrumentation, so Beesharp's own traffic (UI, REST API, assistant) is never exported. The sampler is always on.
/// </summary>
public static class TracingRegistration
{
    /// <summary>
    /// Adds the OpenTelemetry tracer provider to a host. <b>Registers nothing while export is off</b> (no endpoint configured) and nobody passes <paramref name="configure"/>:
    /// a plain Beesharp then pays only for the always-on listener of <see cref="BeesharpTracing"/>.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">The host's configuration, see <see cref="TracingSettings.FromConfiguration"/>.</param>
    /// <param name="configure">Extra setup of the tracer provider, e.g. another exporter (tests use an in-memory one). Passing it turns the provider on even without an endpoint.</param>
    /// <returns>The same <paramref name="services"/>.</returns>
    public static IServiceCollection AddBeesharpTracing(this IServiceCollection services, IConfiguration configuration, Action<TracerProviderBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var settings = TracingSettings.FromConfiguration(configuration);
        services.AddSingleton(settings);
        if (!settings.Enabled && configure is null) return services;
        services.AddOpenTelemetry().WithTracing(builder => Configure(builder, settings, configure));
        return services;
    }

    /// <summary>
    /// The same provider without a host, for the command-line tool. Returns <see langword="null"/> while export is off and <paramref name="configure"/> is <see langword="null"/>.
    /// Dispose the provider (it flushes) before the process ends.
    /// </summary>
    /// <param name="settings">What <see cref="TracingSettings.FromConfiguration"/> read.</param>
    /// <param name="configure">Extra setup of the tracer provider; turns it on even without an endpoint.</param>
    /// <returns>The provider, or <see langword="null"/>.</returns>
    public static TracerProvider? BuildBeesharpTracerProvider(TracingSettings settings, Action<TracerProviderBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.Enabled && configure is null) return null;
        var builder = Sdk.CreateTracerProviderBuilder();
        Configure(builder, settings, configure);
        return builder.Build();
    }

    private static void Configure(TracerProviderBuilder builder, TracingSettings settings, Action<TracerProviderBuilder>? configure)
    {
        builder
            .ConfigureResource(resource => resource.AddService(settings.ServiceName))
            .AddSource(BeesharpTracing.SourceName)
            .SetSampler(new AlwaysOnSampler());

        if (settings.OtlpEndpoint is { } endpoint)
        {
            builder.AddOtlpExporter(otlp =>
            {
                otlp.Endpoint = endpoint;
                otlp.Protocol = settings.Protocol;
                // A collector that is down or slow must never hold up a run: the batch processor exports on its own thread, drops spans when its queue is full,
                // and an export gives up after 3 s (the default would be 10 s, also when the host shuts down and flushes).
                otlp.ExportProcessorType = ExportProcessorType.Batch;
                otlp.TimeoutMilliseconds = 3000;
                otlp.BatchExportProcessorOptions = new BatchExportActivityProcessorOptions
                {
                    MaxQueueSize = 32_768,
                    MaxExportBatchSize = 2048,
                    ScheduledDelayMilliseconds = 1000,
                    ExporterTimeoutMilliseconds = 3000,
                };
            });
        }

        configure?.Invoke(builder);
    }
}
