// Reports the scroll position and size of a list to VirtualList.razor (throttled to one call per frame), measures its rendered rows
// and keeps the first visible row where it is when rows are rendered above it (variable row heights, brain 0022).
export function attach(el, ref) {
    let queued = false;
    let follow = false; // stick to the bottom (a log that grows at its end), as long as the reader is at the bottom
    let pinned = true;  // whether the reader is at the bottom right now
    const atEnd = () => el.scrollHeight - el.scrollTop - el.clientHeight <= 8;
    let rec = null; // the first visible row: its index, its top in the list viewport and the scroll position at that time
    const rowSel = ':scope > div > div > .vrow';
    const record = () => {
        const lt = el.getBoundingClientRect().top;
        rec = null;
        for (const r of el.querySelectorAll(rowSel)) {
            const t = r.getBoundingClientRect();
            if (t.bottom > lt + 1) { rec = { i: r.dataset.i, top: t.top - lt, st: el.scrollTop }; return; }
        }
    };
    // Runs right after Blazor changed the DOM, before the browser paints.
    const keep = () => {
        if (follow && pinned) {
            if (el.scrollHeight - el.scrollTop - el.clientHeight > 1) el.scrollTop = el.scrollHeight;
            record();
            return;
        }
        if (rec) {
            const lt = el.getBoundingClientRect().top;
            const r = el.querySelector(`${rowSel}[data-i="${rec.i}"]`);
            if (r) {
                const shift = (r.getBoundingClientRect().top - lt - rec.top) + (el.scrollTop - rec.st);
                if (Math.abs(shift) >= 0.5) el.scrollTop += shift;
            }
        }
        record();
    };
    const mo = new MutationObserver(keep);
    mo.observe(el, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
    const send = () => { queued = false; record(); ref.invokeMethodAsync('OnViewport', el.scrollTop, el.clientHeight, el.clientWidth, atEnd()); };
    const on = () => { if (!queued) { queued = true; requestAnimationFrame(send); } };
    const onScroll = () => { pinned = atEnd(); on(); };
    el.addEventListener('scroll', onScroll, { passive: true });
    const ro = new ResizeObserver(on);
    ro.observe(el);
    el.__vlist = () => { el.removeEventListener('scroll', onScroll); ro.disconnect(); mo.disconnect(); };
    el.__vrecord = record;
    el.__vfollow = v => { follow = v; if (v && pinned) keep(); };
    el.__vpin = () => { pinned = true; el.scrollTop = el.scrollHeight; record(); on(); };
    send();
}

export function detach(el) { if (el.__vlist) { el.__vlist(); el.__vlist = null; } }

export function setFollow(el, follow) { el.__vfollow?.(follow); }
export function pinToEnd(el) { el.__vpin?.(); }
export function scrollToTop(el) { el.scrollTop = 0; el.__vrecord?.(); }
export function scrollTo(el, y) { el.scrollTop = y; el.__vrecord?.(); }

// [scrollTop, index, height, index, height, ...] of the rendered rows
export function measure(el) {
    const rows = el.querySelectorAll(':scope > div > div > .vrow');
    const out = new Array(1 + rows.length * 2);
    out[0] = el.scrollTop;
    let k = 1;
    for (const r of rows) { out[k++] = +r.dataset.i; out[k++] = r.getBoundingClientRect().height; }
    return out;
}
