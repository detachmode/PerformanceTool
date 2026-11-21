// Small helpers of the assistant side panel: Enter sends (Shift+Enter is a new line), the composer grows with its text, and the transcript follows the newest message.
export function attach(textarea, dotnet) {
    if (!textarea) return { resize() {}, focus() {}, dispose() {} };
    const resize = () => {
        textarea.style.height = "auto";
        textarea.style.height = Math.min(textarea.scrollHeight, 160) + "px";
    };
    const onKey = e => {
        if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
            e.preventDefault();
            dotnet.invokeMethodAsync("OnEnter");
        }
    };
    textarea.addEventListener("keydown", onKey);
    textarea.addEventListener("input", resize);
    resize();
    return {
        resize,
        focus() { textarea.focus(); },
        dispose() {
            textarea.removeEventListener("keydown", onKey);
            textarea.removeEventListener("input", resize);
        }
    };
}

export function scrollToEnd(el) {
    if (el) el.scrollTop = el.scrollHeight;
}
