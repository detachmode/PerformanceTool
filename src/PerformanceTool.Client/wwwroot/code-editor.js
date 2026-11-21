// Lightweight scroll-sync and Tab-indentation helper for CSharpCodeEditor.
export function attach(textarea, pre) {
    if (!textarea || !pre) return { dispose() {} };

    const syncScroll = () => {
        pre.scrollTop = textarea.scrollTop;
        pre.scrollLeft = textarea.scrollLeft;
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

    textarea.addEventListener("scroll", syncScroll, { passive: true });
    textarea.addEventListener("keydown", onKeyDown);
    syncScroll();

    return {
        sync: syncScroll,
        dispose() {
            textarea.removeEventListener("scroll", syncScroll);
            textarea.removeEventListener("keydown", onKeyDown);
        }
    };
}
