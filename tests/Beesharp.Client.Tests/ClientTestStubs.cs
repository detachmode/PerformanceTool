using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Beesharp.Client.Tests;

/// <summary>
/// Thread-safe HTTP stub handler for client tests that drive background polling/sync tasks.
/// </summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> handle)
        : this((req, _) => Task.FromResult(handle(req))) { }

    private readonly ConcurrentQueue<(HttpMethod Method, string PathAndQuery)> requests = new();

    public IReadOnlyList<(HttpMethod Method, string PathAndQuery)> Requests => requests.ToArray();

    public bool HasRequest(Func<(HttpMethod Method, string PathAndQuery), bool> predicate) =>
        requests.Any(predicate);

    public int CountRequests(Func<(HttpMethod Method, string PathAndQuery), bool> predicate) =>
        requests.Count(predicate);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requests.Enqueue((request.Method, request.RequestUri?.PathAndQuery ?? ""));
        return await handle(request, cancellationToken);
    }

    public static HttpResponseMessage JsonResponse(object payload, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json"),
        };
}

internal sealed class StubNavigationManager : NavigationManager
{
    public List<string> NavigatedTo { get; } = [];

    public StubNavigationManager(string baseUri = "https://beesharp.test/")
    {
        Initialize(baseUri, baseUri);
    }

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        NavigatedTo.Add(uri);
        Uri = ToAbsoluteUri(uri).ToString();
    }
}

internal sealed class FakeJsRuntime : IJSInProcessRuntime
{
    public Dictionary<string, string?> Storage { get; } = new(StringComparer.Ordinal);
    public bool ThrowOnInvoke { get; set; }
    public bool IsPhoneResult { get; set; }
    public bool IsMobileAssistantResult { get; set; }
    public List<string> Invocations { get; } = [];

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
        FakeJsDispatch.Invoke<TValue>(this, identifier, args);

    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
        FakeJsDispatch.Invoke<TValue>(this, identifier, args);

    public TResult Invoke<TResult>(string identifier, params object?[]? args)
    {
        Invocations.Add(identifier);
        if (ThrowOnInvoke) throw new JSException("sync blocked");
        if (identifier == "beesharpPhone.is") return (TResult)(object)IsPhoneResult;
        return default!;
    }

    internal sealed class FakeJsModule(bool isMobile) : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            FakeJsDispatch.InvokeModule<TValue>(isMobile, identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            FakeJsDispatch.InvokeModule<TValue>(isMobile, identifier);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal static class FakeJsDispatch
{
    public static ValueTask<TValue> Invoke<TValue>(FakeJsRuntime self, string identifier, object?[]? args)
    {
        self.Invocations.Add(identifier);
        if (self.ThrowOnInvoke) throw new JSException("storage blocked");

        if (identifier == "localStorage.getItem" && args is [string key])
            return ValueTask.FromResult((TValue)(object?)self.Storage.GetValueOrDefault(key)!);

        if (identifier == "localStorage.setItem" && args is [string setKey, var setVal])
        {
            self.Storage[setKey] = setVal?.ToString();
            return ValueTask.FromResult(default(TValue)!);
        }

        if (identifier == "import" && typeof(TValue) == typeof(IJSObjectReference))
            return ValueTask.FromResult((TValue)(object)new FakeJsRuntime.FakeJsModule(self.IsMobileAssistantResult));

        return ValueTask.FromResult(default(TValue)!);
    }

    public static ValueTask<TValue> InvokeModule<TValue>(bool mobile, string identifier) =>
        identifier == "isMobile" ? ValueTask.FromResult((TValue)(object)mobile) : ValueTask.FromResult(default(TValue)!);
}
