using Beesharp.Client.Chart;
using Microsoft.AspNetCore.Components;

namespace Beesharp.Client.Tests;

/// <summary>
/// Story for <see cref="DockZoom"/> JS callbacks: picking a bar, aiming to freeze live updates,
/// pinching to zoom, brushing a time range, resizing, and releasing/disposing safely.
/// </summary>
public sealed class DockZoomTests
{
    [Fact]
    public async Task Dock_zoom_forwards_js_gesture_callbacks_and_disposes_cleanly_even_without_module()
    {
        string? picked = null;
        bool? aiming = null;
        BrushRange? zoomed = null;
        BrushRange? brushed = null;
        double? width = null;

        await using var dock = new DockZoom();
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(DockZoom.Tag)] = "section",
            [nameof(DockZoom.Select)] = true,
            [nameof(DockZoom.Magnify)] = true,
            [nameof(DockZoom.OnPick)] = EventCallback.Factory.Create<string>(this, k => picked = k),
            [nameof(DockZoom.OnAim)] = EventCallback.Factory.Create<bool>(this, a => aiming = a),
            [nameof(DockZoom.OnZoom)] = EventCallback.Factory.Create<BrushRange>(this, z => zoomed = z),
            [nameof(DockZoom.OnBrush)] = EventCallback.Factory.Create<BrushRange>(this, b => brushed = b),
            [nameof(DockZoom.OnSize)] = EventCallback.Factory.Create<double>(this, w => width = w),
        }).SetParameterProperties(dock);

        await dock.Pick("b42");
        await dock.Aim(true);
        await dock.Zoom(10.0, 25.0);
        await dock.Brush(5.0, 30.0, toEnd: true);
        await dock.Size(640.0);
        await dock.ReleaseAsync();
        await dock.DisposeAsync();

        Assert.Equal("b42", picked);
        Assert.True(aiming);
        Assert.Equal(new BrushRange(10.0, 25.0, false), zoomed);
        Assert.Equal(new BrushRange(5.0, 30.0, true), brushed);
        Assert.Equal(640.0, width);
    }
}
