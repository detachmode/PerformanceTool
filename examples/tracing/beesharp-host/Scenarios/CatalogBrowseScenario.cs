using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using Beesharp.Services.Scenarios;
using Microsoft.Extensions.Logging;

namespace Beesharp.Tracing.Host.Scenarios;

public sealed record CatalogBrowseParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://localhost:5292",
    [property: Description("Search query term")]
    string Query = "honey",
    [property: Description("How many products each bee inspects")]
    [property: Range(2, 10)]
    int Products = 3);

public sealed record LoadCategories;
public sealed record SearchCatalog;
public sealed record ViewProduct(int Index);
public sealed record ReadReviews(int ProductId);
public sealed record FetchRecommendations(int ProductId);

public union CatalogStep(LoadCategories, SearchCatalog, ViewProduct, ReadReviews, FetchRecommendations);

/// <summary>Compiled scenario for catalog browsing: categories, search, product views, reviews, and recommendations.</summary>
public sealed class CatalogBrowseScenario : Scenario<CatalogBrowseParams, CatalogStep>
{
    public override string Id => "catalog-browse";
    public override string Name => "Catalog browse";
    public override string Description => "Browses categories, searches the catalog, opens products, reads reviews, and fetches recommendations.";

    protected override CatalogStep StartStep(CatalogBrowseParams parameters) => new LoadCategories();

    protected override Task<CatalogStep?> RunStepAsync(
        CatalogStep step,
        CatalogBrowseParams p,
        ScenarioContext ctx,
        CancellationToken ct) => step switch
        {
            LoadCategories => DoLoadCategories(ctx, p, ct),
            SearchCatalog => DoSearchCatalog(ctx, p, ct),
            ViewProduct v => DoViewProduct(ctx, p, v, ct),
            ReadReviews r => DoReadReviews(ctx, p, r, ct),
            FetchRecommendations f => DoFetchRecommendations(ctx, p, f, ct),
        };

    private static async Task<CatalogStep?> DoLoadCategories(ScenarioContext ctx, CatalogBrowseParams p, CancellationToken ct)
    {
        using var res = await ctx.Http.GetAsync(p.BaseUrl + "/categories", ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Failed to load categories: HTTP {StatusCode}", (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Loaded catalog categories");
        return new SearchCatalog();
    }

    private static async Task<CatalogStep?> DoSearchCatalog(ScenarioContext ctx, CatalogBrowseParams p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/search?q={Uri.EscapeDataString(p.Query)}", ct);
        sw.Stop();
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Catalog search for {Query} failed with HTTP {StatusCode}", p.Query, (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        if (sw.ElapsedMilliseconds >= 350)
            ctx.Logger.LogWarning("Slow catalog search for {Query} took {ElapsedMs} ms", p.Query, sw.ElapsedMilliseconds);
        else
            ctx.Logger.LogInformation("Searched catalog for {Query} in {ElapsedMs} ms", p.Query, sw.ElapsedMilliseconds);
        return new ViewProduct(1);
    }

    private static async Task<CatalogStep?> DoViewProduct(ScenarioContext ctx, CatalogBrowseParams p, ViewProduct v, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{v.Index}", ct);
        sw.Stop();
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Viewing product {ProductId} failed with HTTP {StatusCode}", v.Index, (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        if (sw.ElapsedMilliseconds >= 350)
            ctx.Logger.LogWarning("Slow product view for {ProductId} took {ElapsedMs} ms", v.Index, sw.ElapsedMilliseconds);
        else
            ctx.Logger.LogInformation("Viewed product {ProductId} ({Index}/{Total})", v.Index, v.Index, p.Products);
        return v.Index < p.Products ? v with { Index = v.Index + 1 } : new ReadReviews(v.Index);
    }

    private static async Task<CatalogStep?> DoReadReviews(ScenarioContext ctx, CatalogBrowseParams p, ReadReviews r, CancellationToken ct)
    {
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{r.ProductId}/reviews", ct);
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Loading reviews for product {ProductId} failed with HTTP {StatusCode}", r.ProductId, (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        ctx.Logger.LogInformation("Read reviews for product {ProductId}", r.ProductId);
        return new FetchRecommendations(r.ProductId);
    }

    private static async Task<CatalogStep?> DoFetchRecommendations(ScenarioContext ctx, CatalogBrowseParams p, FetchRecommendations f, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/recommendations/{f.ProductId}", ct);
        sw.Stop();
        if (!res.IsSuccessStatusCode)
        {
            ctx.Logger.LogError("Fetching recommendations for product {ProductId} failed with HTTP {StatusCode}", f.ProductId, (int)res.StatusCode);
            res.EnsureSuccessStatusCode();
        }
        if (sw.ElapsedMilliseconds >= 250)
            ctx.Logger.LogWarning("Slow recommendations ML query for product {ProductId} took {ElapsedMs} ms", f.ProductId, sw.ElapsedMilliseconds);
        else
            ctx.Logger.LogInformation("Fetched recommendations for product {ProductId} in {ElapsedMs} ms", f.ProductId, sw.ElapsedMilliseconds);
        return null;
    }
}
