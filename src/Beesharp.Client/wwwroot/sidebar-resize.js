// Resizable desktop sidebars: drag the border between a sidebar and the main area, double-click to reset, or use Left/Right arrows when focused.
// Widths are stored as CSS custom properties on <html> and persisted in localStorage under "beesharp.sidebars".
const STORAGE_KEY = "beesharp.sidebars";

function loadSaved() {
    try {
        const raw = localStorage.getItem(STORAGE_KEY);
        if (!raw) return {};
        const parsed = JSON.parse(raw);
        return parsed && typeof parsed === "object" ? parsed : {};
    } catch {
        return {};
    }
}

function saveWidth(cssVar, width) {
    try {
        const map = loadSaved();
        if (width == null) delete map[cssVar];
        else map[cssVar] = width;
        if (Object.keys(map).length === 0) localStorage.removeItem(STORAGE_KEY);
        else localStorage.setItem(STORAGE_KEY, JSON.stringify(map));
    } catch { }
}

export function applySaved() {
    const map = loadSaved();
    for (const [k, v] of Object.entries(map)) {
        if (typeof k === "string" && k.startsWith("--") && typeof v === "number" && Number.isFinite(v) && v > 0) {
            document.documentElement.style.setProperty(k, Math.round(v) + "px");
        }
    }
}

applySaved();

export function attach(handle, opts) {
    if (!handle || !opts || !opts.cssVar) return { dispose() {} };
    const { cssVar, side = "left", min = 180, max = 600, minMain = 280, sidebarSelector = null } = opts;

    const findSidebar = () => {
        if (!sidebarSelector) return null;
        return handle.closest(sidebarSelector) || handle.parentElement?.querySelector(sidebarSelector) || null;
    };

    const measureWidth = () => {
        const sb = findSidebar();
        if (sb) {
            const w = sb.getBoundingClientRect().width;
            if (w > 0) return w;
        }
        const css = parseFloat(getComputedStyle(document.documentElement).getPropertyValue(cssVar));
        return Number.isFinite(css) && css > 0 ? css : min;
    };

    const effectiveMax = () => {
        if (cssVar === "--asst-w") {
            return Math.max(min, Math.min(max, window.innerWidth - minMain));
        }
        const parent = handle.parentElement;
        if (!parent) return max;
        const containerW = parent.clientWidth;
        if (!containerW) return max;
        let otherW = 0;
        if (parent.classList.contains("scn")) {
            const other = parent.querySelector(side === "left" ? ".scn-right" : ".scn-left");
            if (other) otherW = other.getBoundingClientRect().width;
        }
        return Math.max(min, Math.min(max, containerW - minMain - otherW));
    };

    const setWidth = (w, persist) => {
        const clamped = Math.round(Math.max(min, Math.min(effectiveMax(), w)));
        document.documentElement.style.setProperty(cssVar, clamped + "px");
        handle.setAttribute("aria-valuenow", String(clamped));
        if (persist) saveWidth(cssVar, clamped);
        return clamped;
    };

    const resetWidth = () => {
        document.documentElement.style.removeProperty(cssVar);
        handle.removeAttribute("aria-valuenow");
        saveWidth(cssVar, null);
    };

    const saved = loadSaved()[cssVar];
    if (typeof saved === "number" && Number.isFinite(saved) && saved > 0) {
        handle.setAttribute("aria-valuenow", String(Math.round(saved)));
    }

    let drag = null;

    const onDown = e => {
        if (e.pointerType === "mouse" && e.button !== 0) return;
        e.preventDefault();
        const startW = measureWidth();
        drag = { id: e.pointerId, startX: e.clientX, startW, w: startW };
        handle.classList.add("resizing");
        document.body.classList.add("sidebar-resizing");
        try { handle.setPointerCapture(e.pointerId); } catch { }
    };

    const onMove = e => {
        if (!drag || e.pointerId !== drag.id) return;
        const dx = e.clientX - drag.startX;
        const raw = side === "left" ? drag.startW + dx : drag.startW - dx;
        drag.w = setWidth(raw, false);
    };

    const onUp = e => {
        if (!drag || e.pointerId !== drag.id) return;
        const finalW = drag.w;
        drag = null;
        handle.classList.remove("resizing");
        document.body.classList.remove("sidebar-resizing");
        try { handle.releasePointerCapture(e.pointerId); } catch { }
        saveWidth(cssVar, Math.round(finalW));
    };

    const onDblClick = e => {
        e.preventDefault();
        resetWidth();
    };

    const onKeyDown = e => {
        if (e.key !== "ArrowLeft" && e.key !== "ArrowRight") return;
        e.preventDefault();
        const step = e.shiftKey ? 48 : 16;
        const dir = (e.key === "ArrowRight" ? 1 : -1) * (side === "left" ? 1 : -1);
        setWidth(measureWidth() + dir * step, true);
    };

    handle.addEventListener("pointerdown", onDown);
    handle.addEventListener("pointermove", onMove);
    handle.addEventListener("pointerup", onUp);
    handle.addEventListener("pointercancel", onUp);
    handle.addEventListener("dblclick", onDblClick);
    handle.addEventListener("keydown", onKeyDown);

    return {
        dispose() {
            if (drag) {
                handle.classList.remove("resizing");
                document.body.classList.remove("sidebar-resizing");
                drag = null;
            }
            handle.removeEventListener("pointerdown", onDown);
            handle.removeEventListener("pointermove", onMove);
            handle.removeEventListener("pointerup", onUp);
            handle.removeEventListener("pointercancel", onUp);
            handle.removeEventListener("dblclick", onDblClick);
            handle.removeEventListener("keydown", onKeyDown);
        }
    };
}
