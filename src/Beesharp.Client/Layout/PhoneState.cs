using Microsoft.JSInterop;

namespace Beesharp.Client.Layout;

/// <summary>
/// Whether the page is shown with the phone layouts (brain 0059): up to 760 px wide, or a phone turned sideways (touch and at most 500 px high).
/// The browser decides (<c>window.beesharpPhone</c> in index.html, which also puts the class <c>phone</c> on the html element for the CSS); this class only
/// mirrors it, so components can render another structure (bottom sheets, tabs) and re-render when the window changes. Without a browser (tests) it is false.
/// </summary>
public sealed class PhoneState : IDisposable
{
    private DotNetObjectReference<PhoneState>? self;

    public PhoneState(IJSRuntime js)
    {
        try
        {
            if (js is not IJSInProcessRuntime sync) return;
            IsPhone = sync.Invoke<bool>("beesharpPhone.is");
            self = DotNetObjectReference.Create(this);
            sync.InvokeVoid("beesharpPhone.listen", self);
        }
        catch (JSException) { }
        catch (InvalidOperationException) { }
    }

    public bool IsPhone { get; private set; }

    public event Action? Changed;

    [JSInvokable]
    public void OnChanged(bool phone)
    {
        if (phone == IsPhone) return;
        IsPhone = phone;
        Changed?.Invoke();
    }

    public void Dispose() => self?.Dispose();
}
