using Beesharp.Api;
using Microsoft.AspNetCore.StaticAssets;

namespace Beesharp.Tests;

public class ClientIndexHtmlTests
{
    private const string Html = """
        <script type="importmap"></script>
        <script src="_framework/blazor.webassembly#[.{fingerprint}].js"></script>
        """;

    private static StaticAssetDescriptor Asset(string route, string assetPath, string? label = null, string? fingerprint = null, bool compressed = false)
    {
        var properties = new List<StaticAssetProperty> { new("integrity", "sha256-abc") };
        if (label is not null) properties.Add(new("label", label));
        if (fingerprint is not null) properties.Add(new("fingerprint", fingerprint));
        return new StaticAssetDescriptor
        {
            Route = route,
            AssetPath = assetPath,
            Selectors = compressed ? [new StaticAssetSelector("Content-Encoding", "br", "0.1")] : [],
            Properties = properties,
            ResponseHeaders = [],
        };
    }

    private static readonly StaticAssetDescriptor[] Assets =
    [
        Asset("_framework/blazor.webassembly.js", "_framework/blazor.webassembly.js"),
        Asset("_framework/blazor.webassembly.ab12cd34ef.js", "_framework/blazor.webassembly.js", "_framework/blazor.webassembly.js", "ab12cd34ef"),
        Asset("_framework/blazor.webassembly.ab12cd34ef.js", "_framework/blazor.webassembly.js.br", "_framework/blazor.webassembly.js", "ab12cd34ef", compressed: true),
        Asset("dock-magnify.zz99yy88xx.js", "dock-magnify.js", "dock-magnify.js", "zz99yy88xx"),
        Asset("app.css", "app.css"),
    ];

    [Fact]
    public void Blazor_script_points_to_the_fingerprinted_file()
    {
        var html = ClientIndexHtml.Resolve(Html, Assets);

        Assert.Contains("src=\"_framework/blazor.webassembly.ab12cd34ef.js\"", html);
        Assert.DoesNotContain("{fingerprint}", html);
    }

    [Fact]
    public void Importmap_maps_plain_module_paths_to_fingerprinted_ones()
    {
        var html = ClientIndexHtml.Resolve(Html, Assets);

        Assert.Contains("\"./dock-magnify.js\":\"./dock-magnify.zz99yy88xx.js\"", html);
        Assert.Contains("\"./_framework/blazor.webassembly.js\":\"./_framework/blazor.webassembly.ab12cd34ef.js\"", html);
        Assert.DoesNotContain("<script type=\"importmap\"></script>", html);
    }

    [Fact]
    public void Unknown_asset_drops_the_placeholder_and_keeps_the_plain_path()
    {
        var html = ClientIndexHtml.Resolve(Html, []);

        Assert.Contains("src=\"_framework/blazor.webassembly.js\"", html);
        Assert.Contains("<script type=\"importmap\">{\"imports\":{},\"integrity\":{}}</script>", html);
    }

    [Fact]
    public void Already_resolved_html_stays_the_same_apart_from_an_empty_importmap()
    {
        const string resolved = "<script type=\"importmap\">{\"imports\":{}}</script><script src=\"_framework/blazor.webassembly.ab12cd34ef.js\"></script>";

        Assert.Equal(resolved, ClientIndexHtml.Resolve(resolved, Assets));
    }
}
