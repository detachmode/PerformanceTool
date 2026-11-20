using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.JSInterop;

namespace PerformanceTool.Client.Components;

/// <summary>A brushed time frame in run seconds. <see cref="ToEnd"/> is true when the brush reached the newest bar (the live edge).</summary>
public readonly record struct BrushRange(double From, double To, bool ToEnd);

/// <summary>
/// Wraps a bar chart in an element and gives it Dock-like magnification (brain 0023). The per-frame work is in
/// <c>wwwroot/dock-magnify.js</c>; Blazor only hears about a selection once (click or touch release) through <see cref="OnPick"/>
/// with the key of the bar. Keyboard activation of the bar buttons is not touched.
/// </summary>
public sealed class DockZoom : ComponentBase, IAsyncDisposable
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public string Tag { get; set; } = "div";
    /// <summary>True when bars can be picked (popup chart); false for the passive card chart, where a click opens the card.</summary>
    [Parameter] public bool Select { get; set; }
    /// <summary>False on the cards: no magnification there (brain 0023), only the clock-driven growth of running bars.</summary>
    [Parameter] public bool Magnify { get; set; } = true;
    /// <summary>Called once with the key of the picked bar (<c>data-key</c>: actor, running actor or time bucket), not its index (brain 0026).</summary>
    [Parameter] public EventCallback<string> OnPick { get; set; }
    /// <summary>True while the user is aiming at the chart (pointer over it, finger down, keyboard focus), so the owner can hold back new bars (brain 0026).</summary>
    [Parameter] public EventCallback<bool> OnAim { get; set; }
    /// <summary>The user brushed a time frame (drag across the chart, brain 0023 update 3).</summary>
    [Parameter] public EventCallback<BrushRange> OnBrush { get; set; }
    /// <summary>The user pinched the time range with two fingers (brain 0031). The range is not clamped to the run; the owner does that.</summary>
    [Parameter] public EventCallback<BrushRange> OnZoom { get; set; }
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

    [JSInvokable] public Task Pick(string key) => OnPick.InvokeAsync(key);
    [JSInvokable] public Task Aim(bool aiming) => OnAim.InvokeAsync(aiming);
    [JSInvokable] public Task Zoom(double from, double to) => OnZoom.InvokeAsync(new BrushRange(from, to, false));
    [JSInvokable] public Task Brush(double from, double to, bool toEnd) => OnBrush.InvokeAsync(new BrushRange(from, to, toEnd));

    /// <summary>The owner resumes by itself (badge click): the script forgets its aiming state.</summary>
    public async Task ReleaseAsync()
    {
        try { if (module is not null && attached) await module.InvokeVoidAsync("release", element); }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { if (module is not null) { if (attached) await module.InvokeVoidAsync("detach", element); await module.DisposeAsync(); } }
        catch (JSDisconnectedException) { }
        catch (InvalidOperationException) { }
        self?.Dispose();
    }
}
