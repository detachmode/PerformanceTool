// Reports the scroll position and size of a list to VirtualList.razor (throttled to one call per frame).
export function attach(el, ref) {
    let queued = false;
    const send = () => { queued = false; ref.invokeMethodAsync('OnViewport', el.scrollTop, el.clientHeight); };
    const on = () => { if (!queued) { queued = true; requestAnimationFrame(send); } };
    el.addEventListener('scroll', on, { passive: true });
    const ro = new ResizeObserver(on);
    ro.observe(el);
    el.__vlist = () => { el.removeEventListener('scroll', on); ro.disconnect(); };
    send();
}

export function detach(el) { if (el.__vlist) { el.__vlist(); el.__vlist = null; } }

export function scrollToTop(el) { el.scrollTop = 0; }
