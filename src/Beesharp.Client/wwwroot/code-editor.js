// Lightweight scroll-sync, line-number alignment, and Tab-indentation helper for CSharpCodeEditor.
export function attach(textarea, pre, gutter) {
    if (!textarea || !pre) return { sync() {}, dispose() {} };

    const syncScroll = () => {
        pre.scrollTop = textarea.scrollTop;
        pre.scrollLeft = textarea.scrollLeft;
        if (gutter) gutter.scrollTop = textarea.scrollTop;
    };

    const syncGutter = () => {
        if (!gutter) return;
        const rows = pre.querySelectorAll(".hl-row");
        const count = rows.length;
        // Reuse existing gutter nodes where possible to avoid DOM churn while typing.
        while (gutter.children.length > count) gutter.removeChild(gutter.lastChild);
        while (gutter.children.length < count) {
            const el = document.createElement("div");
            el.className = "cs-ln";
            gutter.appendChild(el);
        }
        for (let i = 0; i < count; i++) {
            const el = gutter.children[i];
            const num = String(i + 1);
            if (el.textContent !== num) el.textContent = num;
            const h = rows[i].getBoundingClientRect().height;
            if (h > 0) el.style.height = h + "px";
        }
        gutter.scrollTop = textarea.scrollTop;
    };

    const onKeyDown = (e) => {
        if (e.key === "Tab" && !e.ctrlKey && !e.metaKey && !e.altKey) {
            e.preventDefault();
            const start = textarea.selectionStart;
            const end = textarea.selectionEnd;
            const val = textarea.value;
            textarea.value = val.substring(0, start) + "    " + val.substring(end);
            textarea.selectionStart = textarea.selectionEnd = start + 4;
            textarea.dispatchEvent(new Event("input", { bubbles: true }));
        }
    };

    const ro = typeof ResizeObserver !== "undefined" ? new ResizeObserver(() => { syncGutter(); syncScroll(); }) : null;
    if (ro) ro.observe(pre);

    textarea.addEventListener("scroll", syncScroll, { passive: true });
    textarea.addEventListener("keydown", onKeyDown);
    syncGutter();
    syncScroll();

    return {
        sync() {
            syncGutter();
            syncScroll();
        },
        dispose() {
            if (ro) ro.disconnect();
            textarea.removeEventListener("scroll", syncScroll);
            textarea.removeEventListener("keydown", onKeyDown);
        }
    };
}
