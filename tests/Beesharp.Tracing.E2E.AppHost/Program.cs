// Aspire app model of the tracing example (brain 0063): Beesharp, shop-api and payment-api (the same project twice),
// started as processes. Settings (all optional, pass as `--E2E:Name=value` or environment `E2E__Name`):
//   E2E:OtlpEndpoint   where all three send their spans (OTLP http/protobuf). Without it Aspire's dashboard receives them.
//   E2E:DataDirectory  Beesharp's scripts, presets, environments and run storage live here (default: a fresh temp folder).
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var otlpEndpoint = builder.Configuration["E2E:OtlpEndpoint"];
var dataDirectory = builder.Configuration["E2E:DataDirectory"]
    ?? Path.Combine(Path.GetTempPath(), "beesharp-tracing-" + Guid.NewGuid().ToString("N")[..8]);

// Beesharp gets the example's own scenario and preset, copied next to its run storage so nothing in the repo is touched.
var exampleDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "examples", "tracing", "beesharp"));
var scripts = Path.Combine(dataDirectory, "scripts");
var presets = Path.Combine(dataDirectory, "presets");
Directory.CreateDirectory(scripts);
Directory.CreateDirectory(presets);
File.Copy(Path.Combine(exampleDirectory, "shop-journey.cs"), Path.Combine(scripts, "shop-journey.cs"), overwrite: true);
File.Copy(Path.Combine(exampleDirectory, "shop-journey.json"), Path.Combine(presets, "shop-journey.json"), overwrite: true);

// A service under test with plain OpenTelemetry; the same code runs as shop-api and as payment-api (like in compose.yaml).
IResourceBuilder<ProjectResource> Traced(IResourceBuilder<ProjectResource> project, string serviceName)
{
    project = project.WithHttpEndpoint(name: "http");
    if (otlpEndpoint is not null)
        project = project
            .WithEnvironment("OTEL_SERVICE_NAME", serviceName)
            .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint)
            .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"); // the .NET SDK defaults to gRPC
    return project;
}

var payment = Traced(builder.AddProject<Projects.ShopApi>("payment-api", o => o.ExcludeLaunchProfile = true), "payment-api");

var shop = Traced(builder.AddProject<Projects.ShopApi>("shop-api", o => o.ExcludeLaunchProfile = true), "shop-api")
    .WithEnvironment("PaymentUrl", payment.GetEndpoint("http"))
    .WaitFor(payment);

var beesharp = builder.AddProject<Projects.Beesharp>("beesharp", o => o.ExcludeLaunchProfile = true)
    .WithHttpEndpoint(name: "http")
    .WithEnvironment("Scripts__Directory", scripts)
    .WithEnvironment("Presets__Directory", presets)
    .WithEnvironment("Environments__Directory", Path.Combine(dataDirectory, "environments"))
    .WithEnvironment("Storage__DuckDbPath", Path.Combine(dataDirectory, "beesharp.duckdb"))
    // appsettings.json's "Urls" (http and https) beats ASPNETCORE_URLS, which is where Aspire puts the port it picked. The
    // un-prefixed URLS is an app setting like "Urls" and wins over it (the Dockerfile does the same).
    .WithEnvironment(context => context.EnvironmentVariables["URLS"] = context.EnvironmentVariables["ASPNETCORE_URLS"]);
if (otlpEndpoint is not null)
    beesharp
        .WithEnvironment("OTEL_SERVICE_NAME", "beesharp")
        .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpEndpoint)
        .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"); // Beesharp's default, but Aspire sets grpc for every project

builder.Build().Run();
