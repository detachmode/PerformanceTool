// Space bar toggles play/pause (brain 0027) unless focus is in something that uses the key itself.
let handler = null;
export function listen(dotnet) {
    unlisten();
    handler = (e) => {
        if ((e.key !== ' ' && e.code !== 'Space') || e.ctrlKey || e.metaKey || e.altKey || e.defaultPrevented) return;
        const t = e.target;
        if (t instanceof Element && t.closest('input, textarea, select, button, a[href], summary, [contenteditable], [role="button"], [role="textbox"], [role="combobox"], [role="menuitem"]')) return;
        e.preventDefault();
        dotnet.invokeMethodAsync('ToggleFromKey');
    };
    document.addEventListener('keydown', handler);
}
export function unlisten() {
    if (handler) document.removeEventListener('keydown', handler);
    handler = null;
}
