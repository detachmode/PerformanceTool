// Reports the scroll position and size of a list to VirtualList.razor (throttled to one call per frame), measures its rendered rows
// and keeps the first visible row where it is when rows are rendered above it (variable row heights, brain 0022).
export function attach(el, ref) {
    let queued = false;
    let scrolled = false; // the position is only remembered after the reader (or a restore) scrolled, not at the first layout
    let follow = false; // stick to the bottom (a log that grows at its end), as long as the reader is at the bottom
    let pinned = true;  // whether the reader is at the bottom right now
    const atEnd = () => el.scrollHeight - el.scrollTop - el.clientHeight <= 8;
    let rec = null; // the first visible row: its index, its top in the list viewport and the scroll position at that time
    const rowSel = ':scope > div > div > .vrow';
    // A width change (a side panel opens next to the list) reflows every row. The first width change seen before rec is overwritten keeps the row that was first in view, so C# can bring that very row back.
    let lastWidth = el.clientWidth;
    const widthCheck = () => {
        const w = el.clientWidth;
        if (Math.abs(w - lastWidth) < 1) return;
        lastWidth = w;
        if (rec && !el.__vpre) el.__vpre = { i: +rec.i, off: -rec.top };
    };
    // A restore (brain 0069) jumps to an estimated place; until the rows of that place are rendered, rec must stay the remembered row, so keep() then puts exactly that row back.
    let hold = null;
    const record = () => {
        widthCheck();
        if (hold) {
            if (el.querySelector(`${rowSel}[data-i="${hold.i}"]`) || Date.now() > hold.until) hold = null;
            else return;
        }
        const lt = el.getBoundingClientRect().top;
        rec = null;
        for (const r of el.querySelectorAll(rowSel)) {
            const t = r.getBoundingClientRect();
            if (t.bottom > lt + 1) { rec = { i: r.dataset.i, top: t.top - lt, st: el.scrollTop }; return; }
        }
    };
    // scroll memory (brain 0069): the first visible row and how far it is scrolled off, so the list comes back to the same row even when row heights are estimated again.
    // Written after a scroll and again after Blazor rendered the rows of the new place (a big jump is a frame ahead of its rows).
    const remember = () => {
        if (!scrolled || !el.__vkey || !rec) return;
        try { sessionStorage.setItem('scroll:' + el.__vkey, JSON.stringify({ i: +rec.i, off: Math.max(0, Math.round(-rec.top)), end: atEnd() })); } catch { }
    };
    // Runs right after Blazor changed the DOM, before the browser paints.
    const keep = () => {
        widthCheck();
        if (follow && pinned) {
            if (el.scrollHeight - el.scrollTop - el.clientHeight > 1) el.scrollTop = el.scrollHeight;
            record(); remember();
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
        record(); remember();
    };
    const mo = new MutationObserver(keep);
    mo.observe(el, { childList: true, subtree: true, attributes: true, attributeFilter: ['style'] });
    const send = () => {
        queued = false; record(); remember();
        // the row that was first in view before a width change reflowed the list (a side panel opened), so C# can bring that very row back
        const pre = el.__vpre; el.__vpre = null;
        ref.invokeMethodAsync('OnViewport', el.scrollTop, el.clientHeight, el.clientWidth, atEnd(), pre ? pre.i : -1, pre ? pre.off : 0);
    };
    const on = () => { if (!queued) { queued = true; requestAnimationFrame(send); } };
    const onScroll = () => { pinned = atEnd(); scrolled = true; on(); };
    el.addEventListener('scroll', onScroll, { passive: true });
    const ro = new ResizeObserver(on);
    ro.observe(el);
    el.__vlist = () => { el.removeEventListener('scroll', onScroll); ro.disconnect(); mo.disconnect(); };
    el.__vrecord = record;
    el.__vhold = (row, off) => { rec = { i: String(row), top: -off, st: el.scrollTop }; hold = { i: row, until: Date.now() + 2500 }; };
    el.__vfollow = v => { follow = v; if (v && pinned) keep(); };
    el.__vunpin = () => { pinned = false; };
    el.__vpin = () => { pinned = true; el.scrollTop = el.scrollHeight; record(); on(); };
    send();
}

// The remembered position of a list: [first visible row, pixels scrolled off that row, 1 when it was at the bottom], or null.
export function recall(key) {
    try {
        const v = JSON.parse(sessionStorage.getItem('scroll:' + key) ?? 'null');
        return v && Number.isFinite(v.i) ? [v.i, v.off ?? 0, v.end ? 1 : 0] : null;
    } catch { return null; }
}
export function setMemoryKey(el, key) { el.__vkey = key; }
export function unpin(el) { el.__vunpin?.(); }

export function detach(el) { if (el.__vlist) { el.__vlist(); el.__vlist = null; } }

export function setFollow(el, follow) { el.__vfollow?.(follow); }
export function pinToEnd(el) { el.__vpin?.(); }
export function scrollToTop(el) { el.scrollTop = 0; el.__vrecord?.(); }
// Scrolls to y when the list has a size (it may be hidden, e.g. on another tab of the phone page); true when it did.
export function restoreTo(el, y, row, off) { if (el.clientHeight <= 0) return false; el.scrollTop = y; el.__vhold?.(row, off); return true; }
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
