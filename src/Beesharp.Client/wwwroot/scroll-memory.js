// Remembers the page scroll position for one key (the overview, brain 0057): saved when a card opens the scenario page, restored when the overview shows again
// (the browser's back button or the Overview tab). The position is used once.
export function save(key) {
    try { sessionStorage.setItem("scroll:" + key, String(Math.round(window.scrollY))); } catch { }
}

export function restore(key) {
    let value = null;
    try { value = sessionStorage.getItem("scroll:" + key); sessionStorage.removeItem("scroll:" + key); } catch { }
    if (value === null) return false;
    const y = Number(value);
    const go = (tries) => {
        window.scrollTo(0, y);
        if (tries > 0 && Math.abs(window.scrollY - y) > 2) setTimeout(() => go(tries - 1), 60);   // the cards may not have their height yet
    };
    requestAnimationFrame(() => go(10));
    return true;
}
