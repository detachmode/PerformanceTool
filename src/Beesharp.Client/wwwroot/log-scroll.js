// Auto-scrolls a log container to the bottom on new lines, pausing automatically when the user scrolls up
// and resuming when the user scrolls back near the bottom.
export function attach(el) {
    if (!el) return { follow() {}, dispose() {} };
    let following = true;

    const isNearBottom = () => (el.scrollHeight - el.scrollTop - el.clientHeight) <= 28;

    const onScroll = () => {
        following = isNearBottom();
    };

    el.addEventListener("scroll", onScroll, { passive: true });
    el.scrollTop = el.scrollHeight;

    return {
        follow() {
            if (following) el.scrollTop = el.scrollHeight;
        },
        dispose() {
            el.removeEventListener("scroll", onScroll);
        }
    };
}
