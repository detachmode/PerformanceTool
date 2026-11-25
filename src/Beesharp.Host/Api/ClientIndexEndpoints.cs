using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.StaticAssets;
using Microsoft.AspNetCore.StaticAssets.Infrastructure;

namespace Beesharp.Api;

/// <summary>
/// Serves the client's index.html for every client route. The SDK fills the <c>#[.{fingerprint}]</c> placeholders and the
/// importmap only when the client is built or published on its own; when the host publishes the client (Docker image),
/// wwwroot/index.html keeps them and the browser would ask for <c>_framework/blazor.webassembly</c> (everything after
/// <c>#</c> is a URL fragment). So the host fills them from its own static asset endpoints, which always match the files it serves.
/// </summary>
public static class ClientIndexEndpoints
{
    /// <summary>
    /// Maps the client build-info endpoint and SPA HTML fallback route. Call after <c>MapStaticAssets()</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder to add the client index endpoints to.</param>
    /// <returns>The same <see cref="IEndpointRouteBuilder"/> instance for chaining.</returns>
    /// <remarks>
    /// Mapped HTTP endpoints:
    /// <list type="bullet">
    /// <item>
    /// <description><c>GET /build-info.json</c> — Returns JSON <c>{ "sha": string? }</c> with the <c>BuildSha</c> assembly metadata value (or <see langword="null"/> if unset) and <c>Cache-Control: no-store</c>. Status: <c>200 OK</c>.</description>
    /// </item>
    /// <item>
    /// <description><c>Fallback (any unmatched route)</c> — Serves the client <c>index.html</c> (<c>text/html; charset=utf-8</c>, <c>Cache-Control: no-cache</c>) resolved from web root <c>index.html</c> via <see cref="ClientIndexHtml.Resolve"/> or, if absent, from the bundled <c>beesharp-ui/index.html</c> file. Status: <c>200 OK</c>, or <c>404 Not Found</c> if neither file exists.</description>
    /// </item>
    /// </list>
    /// </remarks>
    public static IEndpointRouteBuilder MapClientIndex(this IEndpointRouteBuilder app)
    {
        var descriptors = StaticAssetsEndpointDataSourceHelper.HasStaticAssetsDataSource(app, null)
            ? StaticAssetsEndpointDataSourceHelper.ResolveStaticAssetDescriptors(app, null)
            : [];
        var webRoot = app.ServiceProvider.GetRequiredService<IWebHostEnvironment>().WebRootFileProvider;
        var bundledIndexPath = Path.Combine(AppContext.BaseDirectory, "beesharp-ui", "index.html");
        string? html = null;

        // The client asks for build-info.json to notice a newer deploy (brain 0025). On Pages the workflow writes the file;
        // here the host answers with the entry assembly's build sha (which the Docker build shares with the client),
        // falling back to this library's assembly metadata.
        var buildSha = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildSha")?.Value
            ?? typeof(ClientIndexEndpoints).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .FirstOrDefault(a => a.Key == "BuildSha")?.Value;
        app.MapGet("/build-info.json", (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new { sha = string.IsNullOrWhiteSpace(buildSha) ? null : buildSha });
        });

        // Serves the resolved SPA index.html for any unmatched client route (with no-cache so new deploys appear on next load).
        app.MapFallback(async context =>
        {
            if (html is null)
            {
                var file = webRoot.GetFileInfo("index.html");
                if (file.Exists)
                {
                    using var reader = new StreamReader(file.CreateReadStream());
                    html = ClientIndexHtml.Resolve(await reader.ReadToEndAsync(context.RequestAborted), descriptors);
                }
                else if (File.Exists(bundledIndexPath))
                {
                    html = await File.ReadAllTextAsync(bundledIndexPath, context.RequestAborted);
                }
                else
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
            }
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.Headers.CacheControl = "no-cache";   // new deploys show up on the next load (brain 0025)
            await context.Response.WriteAsync(html, context.RequestAborted);
        });
        return app;
    }
}

/// <summary>Fills <c>index.html</c>'s fingerprint placeholders and empty importmap from static asset endpoints.</summary>
public static partial class ClientIndexHtml
{
    /// <summary>
    /// Replaces <c>#[.{fingerprint}]</c> asset placeholders and populates the empty importmap script tag in
    /// <paramref name="html"/> using uncompressed static asset descriptors.
    /// </summary>
    /// <param name="html">The raw <c>index.html</c> content containing placeholders and an empty importmap tag.</param>
    /// <param name="descriptors">The resolved static asset descriptors from the host's static asset endpoints.</param>
    /// <returns>The updated HTML string with fingerprinted asset paths and generated JSON importmap.</returns>
    /// <remarks>
    /// Only descriptors with no selectors (uncompressed assets) that define both <c>label</c> and <c>fingerprint</c>
    /// properties with a fingerprinted route are used. JavaScript modules (<c>.js</c> and <c>.mjs</c>) are added to
    /// the <c>imports</c> and <c>integrity</c> maps serialized into <c>&lt;script type="importmap"&gt;</c>.
    /// </remarks>
    public static string Resolve(string html, IEnumerable<StaticAssetDescriptor> descriptors)
    {
        // label (the plain path, e.g. _framework/dotnet.js) -> fingerprinted route and its integrity. Only uncompressed endpoints.
        var fingerprinted = new SortedDictionary<string, (string Route, string? Integrity)>(StringComparer.Ordinal);
        foreach (var d in descriptors)
        {
            if (d.Selectors.Count > 0) continue;
            string? label = null, fingerprint = null, integrity = null;
            foreach (var p in d.Properties)
            {
                if (p.Name == "label") label = p.Value;
                else if (p.Name == "fingerprint") fingerprint = p.Value;
                else if (p.Name == "integrity") integrity = p.Value;
            }
            if (label is null || fingerprint is null || d.Route == label) continue;
            fingerprinted[label] = (d.Route, integrity);
        }

        html = FingerprintPlaceholder().Replace(html, m =>
        {
            var label = m.Groups["path"].Value + m.Groups["ext"].Value;
            return fingerprinted.TryGetValue(label, out var asset) ? asset.Route : label;
        });

        var modules = fingerprinted.Where(a => a.Key.EndsWith(".js", StringComparison.Ordinal) || a.Key.EndsWith(".mjs", StringComparison.Ordinal)).ToList();
        var importMap = JsonSerializer.Serialize(new
        {
            imports = modules.ToDictionary(a => "./" + a.Key, a => "./" + a.Value.Route),
            integrity = modules.Where(a => a.Value.Integrity is not null).ToDictionary(a => "./" + a.Value.Route, a => a.Value.Integrity),
        });
        return html.Replace("<script type=\"importmap\"></script>", $"<script type=\"importmap\">{importMap}</script>", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"(?<path>[^""'\s=]+)#\[\.\{fingerprint\}\]!?(?<ext>[^""'\s]*)")]
    private static partial Regex FingerprintPlaceholder();
}
