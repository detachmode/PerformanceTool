// Scroll memory (brain 0059, 0068): a list or a page lands where the user left it when they come back from drilling down (Back, a tab, a link).
// bind(key, selector) restores the saved position of the page (no selector: the window) or of the scroll container the selector finds, then keeps
// saving it while the user scrolls. The position lives in sessionStorage under "scroll:<key>", so it survives the page being re-created, not a new tab.
// Lists that have rows of their own (VirtualList) remember the first visible ROW instead (VirtualList.razor.js, same sessionStorage), which stays right when
// row heights are estimated again. Keys are "<page>" or "<page>:<list>"; a key that includes the filter keeps a filtered list apart from the unfiltered one.

const storageKey = key => "scroll:" + key;

export function bind(key, selector) {
    let el = null;
    let stop = null;
    let timer = 0;
    let disposed = false;

    const position = () => (el ? el.scrollTop : window.scrollY);
    const moveTo = y => { if (el) el.scrollTop = y; else window.scrollTo(0, y); };
    const alive = () => !disposed && (el === null || el.isConnected);

    const listen = () => {
        if (!alive()) return;
        const target = el ?? window;
        const onScroll = () => {
            if (!alive()) { stop?.(); return; }
            try { sessionStorage.setItem(storageKey(key), String(Math.round(position()))); } catch { }
        };
        target.addEventListener("scroll", onScroll, { passive: true });
        stop = () => target.removeEventListener("scroll", onScroll);
    };

    const restore = () => {
        let saved = null;
        try { saved = sessionStorage.getItem(storageKey(key)); } catch { }
        const y = saved === null ? 0 : Number(saved);
        if (!(y >= 1)) { listen(); return; }
        let tries = 0;
        const go = () => {
            if (!alive()) return;
            moveTo(y);
            // the content may not have its height yet (cards, rows): try again for a moment, then take what is there
            if (Math.abs(position() - y) > 2 && ++tries < 14) timer = setTimeout(go, 70);
            else listen();
        };
        requestAnimationFrame(go);
    };

    if (!selector) restore();
    else {
        let waited = 0;
        const find = () => {
            if (disposed) return;
            el = document.querySelector(selector);
            if (el) restore();
            else if (waited < 1500) { waited += 50; timer = setTimeout(find, 50); }
        };
        find();
    }

    return { dispose() { disposed = true; clearTimeout(timer); stop?.(); } };
}
