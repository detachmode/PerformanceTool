using System.Diagnostics;

namespace Beesharp.Services.Tracing;

/// <summary>
/// Beesharp's own <see cref="ActivitySource"/> (brain 0063). Every actor (bee) is the root of one distributed trace; steps and calls are its children.
/// Core depends only on <c>System.Diagnostics</c>: an OpenTelemetry SDK in the host (<c>AddSource(<see cref="SourceName"/>)</c>) turns these spans into exports.
/// </summary>
/// <remarks>
/// A <see cref="ActivitySource"/> returns <see langword="null"/> from <c>StartActivity</c> while nobody listens, which would leave calls without a trace ID
/// whenever no exporter is configured. So the first use installs one always-on <see cref="ActivityListener"/> for this source (brain 0063): trace and span IDs
/// (and the sampled flag of the <c>traceparent</c> sent to the services under test) exist whether or not anything is exported.
/// </remarks>
public static class BeesharpTracing
{
    /// <summary>Name of the <see cref="ActivitySource"/> that carries all actor, step and call spans. Add it to a <c>TracerProvider</c> to export them.</summary>
    public const string SourceName = "Beesharp";

    private static readonly ActivityListener AlwaysOn = new()
    {
        ShouldListenTo = static source => source.Name == SourceName,
        Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
    };

    /// <summary>The <see cref="ActivitySource"/> of all Beesharp spans. Touching it installs the always-on listener.</summary>
    public static ActivitySource Source { get; } = Create();

    private static ActivitySource Create()
    {
        ActivitySource.AddActivityListener(AlwaysOn);
        return new ActivitySource(SourceName, typeof(BeesharpTracing).Assembly.GetName().Version?.ToString());
    }
}
