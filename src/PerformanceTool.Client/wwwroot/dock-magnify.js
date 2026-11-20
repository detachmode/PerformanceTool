// Dock-like magnification for the scenario popup chart (brain 0023; not on cards). All per-frame work happens here (requestAnimationFrame + inline widths),
// Blazor is only told once about a selection (click or touch release). The total width of the chart stays fixed: the bars under
// the pointer grow, the others compress.
const PEAK = 3;        // widest bar is about 3x its resting width (before compression)
const REACH = 4;       // cosine falloff over this many bars on each side
const TALL = 0.12;     // bars also grow a little taller (scaleY, via --dz)
const SMOOTH_MS = 70;  // time constant of the easing

const reduced = () => matchMedia('(prefers-reduced-motion: reduce)').matches;

// Running actors (brain 0018): bars with data-start (ms into the run) grow with the clock. The chart carries data-elapsed (run ms at the
// last render) and data-scale (ms for full height). One shared loop sets the heights, so Blazor does not re-render at frame rate.
const tracked = new Set();
let growRaf = 0;
const growFrame = () => {
    growRaf = 0;
    let any = false;
    const t = performance.now();
    for (const el of tracked) {
        const runs = el.querySelectorAll('[data-start]');
        if (runs.length === 0) continue;
        any = true;
        const e = el.dataset.elapsed;
        if (el.__e !== e) { el.__e = e; el.__seen = t; }
        const now = parseFloat(e) + (t - el.__seen), scale = parseFloat(el.dataset.scale) || 1;
        for (const r of runs) r.style.height = Math.min(100, Math.max(3, (now - parseFloat(r.dataset.start)) / scale * 100)).toFixed(1) + '%';
    }
    if (any) growRaf = requestAnimationFrame(growFrame);
};
const wakeGrow = () => { if (!growRaf) growRaf = requestAnimationFrame(growFrame); };

export function attach(el, ref, options) {
    tracked.add(el);
    const watch = new MutationObserver(wakeGrow);
    watch.observe(el, { childList: true, subtree: true });
    wakeGrow();
    // Cards (brain 0023, update): the chart only grows running bars with the clock, no magnification.
    if (options && options.magnify === false) { el.__dock = () => { tracked.delete(el); watch.disconnect(); }; return; }
    const select = !!(options && options.select);
    const selector = ':scope > .bar-hit, :scope > i';
    let items = [], rest = [], cur = [], width = -1;     // rest layout, current (eased) magnification 0..1 per item
    let layout = [];                                     // last applied {left, width} per item, relative to el
    const tip = el.querySelector(':scope > .dz-label');   // label inside the hovered bar, set per frame (no Blazor render)
    let active = false, px = 0, raf = 0, last = 0, touchDone = -1e9;
    window.__dock = window.__dock || { calls: 0, frames: 0 };

    const clear = () => {
        for (const it of items) { it.style.flex = ''; it.style.minWidth = ''; it.style.maxWidth = ''; it.style.removeProperty('--dz'); }
    };
    const measure = () => {
        clear();
        rest = items.map(it => ({ left: it.offsetLeft, width: it.offsetWidth }));
        width = el.clientWidth;
    };
    const refresh = () => {
        const now = Array.from(el.querySelectorAll(selector));
        if (now.length !== items.length || el.clientWidth !== width || now.some((x, i) => x !== items[i])) {
            const old = cur, keep = now.length === items.length;
            items = now; measure();
            cur = keep ? old : items.map(() => 0);
        }
    };
    // Shows the hovered bar's data-l1 / data-l2 inside the bar: horizontal if wide, vertical if tall enough, else just above the bar.
    const showLabel = i => {
        if (!tip) return;
        const it = i >= 0 ? items[i] : null, l1 = it && it.dataset.l1, l2 = it && it.dataset.l2;
        const bar = it && it.querySelector('.bar-stack, .bar');
        if (!bar || !l1) { tip.className = 'dz-label'; return; }
        if (tip.__t !== l1 + '|' + l2) { tip.__t = l1 + '|' + l2; tip.replaceChildren(...[l1, l2].filter(Boolean).map(t => Object.assign(document.createElement('span'), { textContent: t }))); }
        const br = bar.getBoundingClientRect(), er = el.getBoundingClientRect();
        const bh = br.height, bw = Math.max(0, layout[i].width - 4), top = br.top - er.top - el.clientTop;
        const left = layout[i].left + (layout[i].width - bw) / 2;
        const fit = bw >= 76 && bh >= 36 ? 'in-h' : bw >= 30 && bh >= 72 ? 'in-v two' : bw >= 16 && bh >= 120 ? 'in-v' : '';
        tip.className = 'dz-label ' + (fit || 'above');
        const s = tip.style;
        if (fit) { s.left = left + 'px'; s.top = top + 'px'; s.width = bw + 'px'; s.height = bh + 'px'; }
        else {
            s.width = 'auto'; s.height = 'auto';
            const w = tip.offsetWidth;
            s.left = Math.max(2, Math.min(el.clientWidth - w - 2, layout[i].left + layout[i].width / 2 - w / 2)) + 'px';
            s.top = Math.max(2, top - tip.offsetHeight - 3) + 'px';
        }
    };
    const frame = ts => {
        raf = 0; window.__dock.frames++;
        refresh();
        const n = items.length;
        if (n === 0) return;
        const dt = last ? Math.min(ts - last, 64) : 16; last = ts;
        const k = reduced() ? 1 : 1 - Math.exp(-dt / SMOOTH_MS);
        const span = rest[n - 1].left + rest[n - 1].width - rest[0].left;
        const pitch = span / n;
        let moving = false, sum = 0, total = 0;
        const w = new Array(n);
        for (let i = 0; i < n; i++) {
            const d = Math.abs(rest[i].left + rest[i].width / 2 - px) / (pitch * REACH);
            const t = active && d < 1 ? 0.5 * (1 + Math.cos(Math.PI * d)) : 0;
            let c = cur[i] + (t - cur[i]) * k;
            if (Math.abs(t - c) < 0.002) c = t; else moving = true;
            cur[i] = c;
            w[i] = rest[i].width * (1 + (PEAK - 1) * c);
            sum += w[i]; total += rest[i].width;
        }
        const f = total / sum;                    // keeps the total width fixed
        let x = rest[0].left;
        layout = new Array(n);
        for (let i = 0; i < n; i++) {
            const wi = w[i] * f;
            const gap = i < n - 1 ? rest[i + 1].left - rest[i].left - rest[i].width : 0;
            layout[i] = { left: x, width: wi + gap };
            const it = items[i];
            it.style.flex = `0 0 ${wi}px`; it.style.minWidth = '0'; it.style.maxWidth = 'none';
            it.style.setProperty('--dz', (1 + TALL * cur[i]).toFixed(3));
            x += wi + gap;
        }
        showLabel(active ? hit(px) : -1);
        if (active || moving) raf = requestAnimationFrame(frame);
        else { clear(); layout = []; last = 0; }
    };
    const kick = () => { if (!raf) raf = requestAnimationFrame(frame); };
    const rel = e => e.clientX - el.getBoundingClientRect().left - el.clientLeft;
    // the item visually under x in the magnified layout (-1 if none or an empty slot)
    const hit = x => {
        if (layout.length === 0) { refresh(); layout = rest.map(r => ({ left: r.left, width: r.width })); }
        for (let i = 0; i < layout.length; i++)
            if (x >= layout[i].left && x < layout[i].left + layout[i].width) return items[i].classList.contains('empty') ? -1 : i;
        return -1;
    };
    const pick = e => { const i = hit(rel(e)); if (i >= 0) { window.__dock.calls++; ref.invokeMethodAsync('Pick', i); } };

    const onMove = e => { px = rel(e); active = true; kick(); };
    const onDown = e => { if (e.pointerType === 'touch' && select) onMove(e); };
    const onPointerMove = e => { if (e.pointerType === 'touch' && !select) return; onMove(e); };
    const onLeave = e => { if (e.pointerType !== 'touch') { active = false; kick(); } };
    const onUp = e => {
        if (e.pointerType !== 'touch' || !select) return;
        px = rel(e);
        if (e.type === 'pointerup') { pick(e); touchDone = performance.now(); }
        active = false; kick();
    };
    const onClick = e => {
        if (!select || e.detail === 0) return;      // keyboard activation goes straight to Blazor
        e.stopPropagation(); e.preventDefault();
        if (performance.now() - touchDone < 700) return;   // already handled on touch release
        pick(e);
    };
    el.addEventListener('pointerdown', onDown);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerleave', onLeave);
    el.addEventListener('pointerup', onUp);
    el.addEventListener('pointercancel', onUp);
    el.addEventListener('click', onClick, true);
    el.__dock = () => {
        el.removeEventListener('pointerdown', onDown); el.removeEventListener('pointermove', onPointerMove);
        el.removeEventListener('pointerleave', onLeave); el.removeEventListener('pointerup', onUp);
        el.removeEventListener('pointercancel', onUp); el.removeEventListener('click', onClick, true);
        if (raf) cancelAnimationFrame(raf);
        clear(); if (tip) tip.className = 'dz-label';
        tracked.delete(el); watch.disconnect();
    };
}

export function detach(el) { if (el && el.__dock) { el.__dock(); el.__dock = null; } }
