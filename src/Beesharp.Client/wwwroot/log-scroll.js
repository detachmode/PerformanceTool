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

// Watches a top-newest feed container: notifies .NET when scrolled away from the top so new items can be paused.
export function watchTop(el, dotnet) {
    if (!el) return { scrollToTop() {}, dispose() {} };
    let away = false;

    const onScroll = () => {
        const nowAway = el.scrollTop > 12;
        if (nowAway !== away) {
            away = nowAway;
            dotnet.invokeMethodAsync("OnLiveFeedScroll", away);
        }
    };

    el.addEventListener("scroll", onScroll, { passive: true });
    return {
        scrollToTop() {
            el.scrollTo({ top: 0, behavior: "smooth" });
        },
        dispose() {
            el.removeEventListener("scroll", onScroll);
        }
    };
}
