// Scenario for catalog browsing: categories, search, product views, reviews and recommendations.
using System.Diagnostics;

public record CatalogBrowseParams(
    [property: Description("Base URL of shop-api")]
    string BaseUrl = "http://shop-api:8080",
    [property: Description("Search query term")]
    string Query = "honey",
    [property: Description("How many products each bee inspects")]
    [property: Range(2, 10)]
    int Products = 3);

public record LoadCategories;
public record SearchCatalog;
public record ViewProduct(int Index);
public record ReadReviews(int ProductId);
public record FetchRecommendations(int ProductId);
public union CatalogStep(LoadCategories, SearchCatalog, ViewProduct, ReadReviews, FetchRecommendations);

Scenario.DefineSteps<CatalogBrowseParams, CatalogStep>(
    "Catalog browse",
    start: _ => new LoadCategories(),
    run: async (step, p, ctx) => step switch
    {
        LoadCategories => await DoLoadCategories(ctx, p),
        SearchCatalog => await DoSearchCatalog(ctx, p),
        ViewProduct v => await DoViewProduct(ctx, p, v),
        ReadReviews r => await DoReadReviews(ctx, p, r),
        FetchRecommendations f => await DoFetchRecommendations(ctx, p, f),
    });

async Task<CatalogStep?> DoLoadCategories(ScenarioContext ctx, CatalogBrowseParams p)
{
    using var res = await ctx.Http.GetAsync(p.BaseUrl + "/categories", ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Failed to load categories: HTTP {StatusCode}", (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Loaded catalog categories");
    return new SearchCatalog();
}

async Task<CatalogStep?> DoSearchCatalog(ScenarioContext ctx, CatalogBrowseParams p)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/search?q={Uri.EscapeDataString(p.Query)}", ctx.CancellationToken);
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

async Task<CatalogStep?> DoViewProduct(ScenarioContext ctx, CatalogBrowseParams p, ViewProduct v)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{v.Index}", ctx.CancellationToken);
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

async Task<CatalogStep?> DoReadReviews(ScenarioContext ctx, CatalogBrowseParams p, ReadReviews r)
{
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/products/{r.ProductId}/reviews", ctx.CancellationToken);
    if (!res.IsSuccessStatusCode)
    {
        ctx.Logger.LogError("Loading reviews for product {ProductId} failed with HTTP {StatusCode}", r.ProductId, (int)res.StatusCode);
        res.EnsureSuccessStatusCode();
    }
    ctx.Logger.LogInformation("Read reviews for product {ProductId}", r.ProductId);
    return new FetchRecommendations(r.ProductId);
}

async Task<CatalogStep?> DoFetchRecommendations(ScenarioContext ctx, CatalogBrowseParams p, FetchRecommendations f)
{
    var sw = Stopwatch.StartNew();
    using var res = await ctx.Http.GetAsync(p.BaseUrl + $"/recommendations/{f.ProductId}", ctx.CancellationToken);
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
