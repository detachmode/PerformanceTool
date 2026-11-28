// One-click interactive .NET Aspire 17 AppHost for local development and demoing:
// starts Jaeger v2 (container), payment-api, shop-api, and Beesharp together, all reachable on LAN.
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);

var lanIp = ResolveLanIp();
static string ResolveLanIp()
{
    try
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Connect("8.8.8.8", 80);
        if (socket.LocalEndPoint is IPEndPoint ep && !IPAddress.IsLoopback(ep.Address))
            return ep.Address.ToString();
    }
    catch { /* fall back to NIC scan */ }

    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            continue;
        foreach (var addr in nic.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(addr.Address))
                return addr.Address.ToString();
        }
    }
    return "localhost";
}

var dataDirectory = Path.Combine(builder.AppHostDirectory, ".data");
var scripts = Path.Combine(dataDirectory, "scripts");
var presets = Path.Combine(dataDirectory, "presets");
var environments = Path.Combine(dataDirectory, "environments");
Directory.CreateDirectory(scripts);
Directory.CreateDirectory(presets);
Directory.CreateDirectory(environments);

// Point the compiled Beesharp.Host scenarios at local shop-api (http://localhost:5292)
// so pressing Play in Beesharp works immediately out of the box.
const string shopApiLocalUrl = "http://localhost:5292";

// Jaeger v2 in default all-in-one memory mode (0.0.0.0:16687 for LAN UI, 4319 for OTLP HTTP).
var jaeger = builder.AddContainer("jaeger", "jaegertracing/jaeger", "2.23.0")
    .WithContainerRuntimeArgs("-p", "0.0.0.0:16687:16686")
    .WithHttpEndpoint(targetPort: 16686, name: "ui")
    .WithHttpEndpoint(port: 4319, targetPort: 4318, name: "otlp-http", isProxied: false)
    .WithEndpoint(port: 4317, targetPort: 4317, name: "otlp-grpc", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithUrls(ctx =>
    {
        foreach (var u in ctx.Urls)
        {
            if (u.Endpoint?.EndpointName == "ui")
            {
                u.Url = $"http://{lanIp}:16687";
                u.DisplayText = "Jaeger UI";
            }
            else if (u.Url.Contains("localhost", StringComparison.OrdinalIgnoreCase))
            {
                u.Url = u.Url.Replace("localhost", lanIp, StringComparison.OrdinalIgnoreCase);
            }
        }
    });

var otlpHttp = jaeger.GetEndpoint("otlp-http");

var shopApiProject = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "shop-api", "ShopApi.csproj"));
var beesharpProject = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "beesharp-host", "Beesharp.Tracing.Host.csproj"));

var payment = builder.AddDotnetProject("payment-api", shopApiProject)
    .WithHttpEndpoint(port: 5293, targetPort: 5293, name: "http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithUrls(ctx =>
    {
        foreach (var u in ctx.Urls)
            u.Url = u.Url.Replace("localhost", lanIp, StringComparison.OrdinalIgnoreCase);
    })
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:5293")
    .WithEnvironment("OTEL_SERVICE_NAME", "payment-api")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpHttp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
    .WaitFor(jaeger);

var shop = builder.AddDotnetProject("shop-api", shopApiProject)
    .WithHttpEndpoint(port: 5292, targetPort: 5292, name: "http", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithUrls(ctx =>
    {
        foreach (var u in ctx.Urls)
            u.Url = u.Url.Replace("localhost", lanIp, StringComparison.OrdinalIgnoreCase);
    })
    .WithEnvironment("ASPNETCORE_URLS", "http://0.0.0.0:5292")
    .WithEnvironment("PaymentUrl", payment.GetEndpoint("http"))
    .WithEnvironment("OTEL_SERVICE_NAME", "shop-api")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpHttp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
    .WaitFor(payment);

builder.AddDotnetProject("beesharp", beesharpProject)
    .WithHttpsEndpoint(port: 7050, targetPort: 7050, name: "https", isProxied: false)
    .WithExternalHttpEndpoints()
    .WithUrls(ctx =>
    {
        foreach (var u in ctx.Urls)
        {
            u.Url = u.Url.Replace("localhost", lanIp, StringComparison.OrdinalIgnoreCase);
            u.DisplayText = "Beesharp UI (HTTPS)";
        }
    })
    .WithEnvironment("Scripts__Directory", scripts)
    .WithEnvironment("Presets__Directory", presets)
    .WithEnvironment("Environments__Directory", environments)
    .WithEnvironment("Storage__DuckDbPath", Path.Combine(dataDirectory, "beesharp.duckdb"))
    .WithEnvironment("ShopApiUrl", shopApiLocalUrl)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("URLS", "https://0.0.0.0:7050")
    .WithEnvironment("OTEL_SERVICE_NAME", "beesharp")
    .WithEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", otlpHttp)
    .WithEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf")
    .WithEnvironment("Tracing__TraceUrlTemplate", $"http://{lanIp}:16687/trace/{{traceId}}")
    .WaitFor(shop);

builder.Build().Run();
