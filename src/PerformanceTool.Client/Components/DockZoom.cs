using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace PerformanceTool.Client.Components;

/// <summary>
/// Wraps a bar chart in an element and gives it Dock-like magnification (brain 0023). The per-frame work is in
/// <c>wwwroot/dock-magnify.js</c>; Blazor only hears about a selection once (click or touch release) through <see cref="OnPick"/>
/// with the index of the child bar. Keyboard activation of the bar buttons is not touched.
/// </summary>
public sealed class DockZoom : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public string Tag { get; set; } = "div";
    /// <summary>True when bars can be picked (popup chart); false for the passive card chart, where a click opens the card.</summary>
    [Parameter] public bool Select { get; set; }
    /// <summary>False on the cards: no magnification there (brain 0023), only the clock-driven growth of running bars.</summary>
    [Parameter] public bool Magnify { get; set; } = true;
    [Parameter] public EventCallback<int> OnPick { get; set; }
    [Parameter] public RenderFragment? ChildContent { get; set; }
    [Parameter(CaptureUnmatchedValues = true)] public Dictionary<string, object>? Attributes { get; set; }

    private ElementReference element;
    private IJSObjectReference? module;
    private DotNetObjectReference<DockZoom>? self;
    private bool attached;

    protected override void BuildRenderTree(RenderTreeBuilder b)
    {
        b.OpenElement(0, Tag);
        b.AddMultipleAttributes(1, Attributes);
        b.AddElementReferenceCapture(2, r => element = r);
        b.AddContent(3, ChildContent);
        b.CloseElement();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try
        {
            module = await JS.InvokeAsync<IJSObjectReference>("import", "./dock-magnify.js");
            self = DotNetObjectReference.Create(this);
            await module.InvokeVoidAsync("attach", element, self, new { select = Select, magnify = Magnify });
            attached = true;
        }
        catch (JSDisconnectedException) { }
        catch (OperationCanceledException) { }
    }

    [JSInvokable] public Task Pick(int index) => OnPick.InvokeAsync(index);

    public async ValueTask DisposeAsync()
    {
        try { if (module is not null) { if (attached) await module.InvokeVoidAsync("detach", element); await module.DisposeAsync(); } }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
        self?.Dispose();
    }
}
