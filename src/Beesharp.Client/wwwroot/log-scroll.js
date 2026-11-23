// Auto-scrolls a log/steps container to the bottom with a smooth entrance animation,
// pausing automatically when the user scrolls up and resuming when they scroll back near the bottom.
const smoothBehavior = () =>
    window.matchMedia?.("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth";

export function attach(el) {
    if (!el) return { follow() {}, dispose() {} };
    let following = true;
    let initialScroll = true;

    const isNearBottom = () => (el.scrollHeight - el.scrollTop - el.clientHeight) <= 28;

    const stopInitial = () => { initialScroll = false; };
    const onScroll = () => {
        if (initialScroll) {
            if (isNearBottom()) initialScroll = false;
            return;
        }
        following = isNearBottom();
    };
    const onUserInterrupt = () => {
        if (initialScroll) {
            initialScroll = false;
            following = isNearBottom();
        }
    };

    el.addEventListener("scroll", onScroll, { passive: true });
    el.addEventListener("wheel", onUserInterrupt, { passive: true });
    el.addEventListener("touchstart", onUserInterrupt, { passive: true });
    el.addEventListener("pointerdown", onUserInterrupt, { passive: true });

    const timer = setTimeout(stopInitial, 900);
    requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            if (!el) return;
            if (el.scrollHeight > el.clientHeight + 2) {
                el.scrollTo({ top: el.scrollHeight, behavior: smoothBehavior() });
            } else {
                initialScroll = false;
            }
        });
    });

    return {
        follow() {
            if (!following) return;
            if (initialScroll) {
                el.scrollTo({ top: el.scrollHeight, behavior: smoothBehavior() });
            } else {
                el.scrollTo({ top: el.scrollHeight, behavior: smoothBehavior() });
            }
        },
        dispose() {
            clearTimeout(timer);
            el.removeEventListener("scroll", onScroll);
            el.removeEventListener("wheel", onUserInterrupt);
            el.removeEventListener("touchstart", onUserInterrupt);
            el.removeEventListener("pointerdown", onUserInterrupt);
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
            el.scrollTo({ top: 0, behavior: smoothBehavior() });
        },
        dispose() {
            el.removeEventListener("scroll", onScroll);
        }
    };
}

export function scrollToBottom(el) {
    if (!el) return;
    requestAnimationFrame(() => {
        requestAnimationFrame(() => {
            if (!el) return;
            el.scrollTo({ top: el.scrollHeight, behavior: smoothBehavior() });
        });
    });
}
