// Dock-like magnification for the scenario popup chart (brain 0023; not on cards). All per-frame work happens here (requestAnimationFrame + inline widths),
// Blazor is only told once about a selection (click or touch release). The total width of the chart stays fixed: the bars under
// the pointer grow, the others compress.
const MIN_PEAK = 3;    // widest bar is at least 3x its resting width (before compression)
const NEAR = 5;        // neighbours on each side that may show a faint label
const TALL = 0.12;     // bars also grow a little taller (scaleY, via --dz)
const SMOOTH_MS = 70;  // time constant of the easing
const CW = 6.2;        // width of one monospace character of the neighbour labels (10px font), to avoid measuring per frame

// Adaptive magnification (brain 0023, update 2): the hovered bar should end up about `target` px wide whatever the bar count, so the
// peak factor is computed from the chart width W, the resting pitch r and the falloff radius R (bars, cosine kernel, whose extra width
// sums to (peak-1)*r*R): hovered = peak*r*W / (W + (peak-1)*r*R)  =>  peak*r = target*(W - r*R) / (W - target*R).
function plan(W, r, zs) {
    let target = Math.min(48, Math.max(36, W * 0.055)) * zs;
    let R = Math.round(4 + (10 - r) * 0.45);
    R = Math.max(2, Math.min(8, R, Math.floor(0.65 * W / target)));
    target = Math.min(target, 0.8 * W / R);      // the zoomed area can never need more than the chart
    const solved = target * (W - r * R) / (W - target * R);
    return { R, peak: Math.max(Math.max(1.5, MIN_PEAK * zs), solved / r) };
}
const fmt = (s, tenths) => {
    s = Math.max(0, s);
    const whole = Math.floor(s), f = tenths ? '.' + Math.floor((s - whole) * 10) : '';
    const h = Math.floor(whole / 3600), m = Math.floor(whole / 60) % 60, x = whole % 60;
    return (h ? h + ':' + String(m).padStart(2, '0') : String(Math.floor(whole / 60))) + ':' + String(x).padStart(2, '0') + f;
};
const mk = (cls, tag) => { const e = document.createElement(tag || 'span'); if (cls) e.className = cls; return e; };

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
    let items = [], rest = [], cur = [], width = -1, minW = 0;     // rest layout, current (eased) magnification 0..1 per item
    let layout = [];                                     // last applied {left, width} per item, relative to el
    const tip = el.querySelector(':scope > .dz-label');   // hover card, set per frame (no Blazor render)
    const strip = el.querySelector(':scope > .dz-strip'); // faint labels of the neighbours, under the bars
    const axis = el.querySelector(':scope > .dz-axis');   // run-time axis with the hovered bar's timeframe
    let active = false, px = 0, raf = 0, last = 0, touchDone = -1e9;
    // ---- aiming (brain 0026): while the pointer is over the popup chart, a finger is down or the keyboard focus is in it, Blazor is told
    // once (Aim true) and stops applying new bars. After a short grace period Blazor is told again and the chart catches up; the bars that
    // are still there glide from their old to their new place (snapshot + FLIP in idle).
    let hover = false, down = false, focused = false, notified = false, aimTimer = 0, snap = null, snapAt = 0;
    const aimNow = () => hover || down || focused;
    const keyed = () => Array.from(el.querySelectorAll(':scope > .bar-hit[data-key]'));
    const snapshot = () => {
        const er = el.getBoundingClientRect(); snap = new Map(); snapAt = performance.now();
        for (const it of keyed()) snap.set(it.dataset.key, it.getBoundingClientRect().left - er.left);
    };
    const syncAim = () => {
        if (!select) return;
        if (aimNow()) {
            if (aimTimer) { clearTimeout(aimTimer); aimTimer = 0; }
            if (!notified) { notified = true; ref.invokeMethodAsync('Aim', true); }
        } else if (notified && !aimTimer) {
            aimTimer = setTimeout(() => {
                aimTimer = 0;
                if (aimNow() || !notified) return;
                notified = false; snapshot(); ref.invokeMethodAsync('Aim', false);
            }, 300);
        }
    };
    const glide = () => {
        if (!snap) return;
        const old = snap; snap = null;
        if (performance.now() - snapAt > 1500 || reduced()) return;
        const er = el.getBoundingClientRect(), moved = [];
        for (const it of keyed()) {
            const o = old.get(it.dataset.key), r = it.getBoundingClientRect();
            if (o === undefined) { moved.push([it, null]); continue; }
            const dx = o - (r.left - er.left);
            if (Math.abs(dx) > 0.5) moved.push([it, dx]);
        }
        for (const [it, dx] of moved) { it.style.transition = 'none'; if (dx === null) it.style.opacity = '0'; else it.style.transform = `translateX(${dx}px)`; }
        void el.offsetWidth;
        for (const [it, dx] of moved) { it.style.transition = 'transform 260ms ease-out, opacity 260ms ease-out'; it.style.transform = ''; it.style.opacity = ''; }
        setTimeout(() => { for (const [it] of moved) it.style.transition = ''; }, 300);
    };
    window.__dock = window.__dock || { calls: 0, frames: 0 };

    // ---- hover card: dot, #id, duration, secondary line; tail points at the bar. Built once, filled per hovered bar.
    let card = null, cardKey = '';
    if (tip) {
        card = { dot: mk('dz-dot', 'i'), id: mk('dz-id', 'b'), meta: mk('dz-meta'), sub: mk('dz-sub'), counts: mk('dz-counts') };
        card.cnt = ['bad', 'warn', 'good', 'run'].map(c => { const e = mk(c); e.append(mk('', 'i'), mk('', 'b'), mk('', 'em')); card.counts.append(e); return e; });
        card.cnt[0].lastChild.textContent = ' failed'; card.cnt[1].lastChild.textContent = ' slow'; card.cnt[2].lastChild.textContent = ' ok'; card.cnt[3].lastChild.textContent = ' running';
        const row = mk('dz-row'); row.append(card.dot, card.id, card.meta);
        const body = mk('dz-body'); body.append(row, card.sub, card.counts);
        tip.replaceChildren(body, mk('dz-tail'));
    }
    // ---- neighbour labels (pooled, fixed slots) and axis parts
    const near = [];
    if (strip) for (let j = 0; j < NEAR * 2; j++) { const n = mk('dz-n'); n.append(mk('', 'a'), mk('', 'b')); strip.append(n); near.push(n); }
    let ticks = [], mark = null, readout = null;
    if (axis) { mark = mk('dz-mark'); readout = mk('dz-readout'); axis.append(mark, readout); }

    const clear = () => {
        for (const it of items) { it.style.flex = ''; it.style.minWidth = ''; it.style.maxWidth = ''; it.style.removeProperty('--dz'); }
    };
    const measure = () => {
        clear();
        const er = el.getBoundingClientRect();   // fractional widths (offsetWidth rounds, which matters for 1.5px bars on a phone)
        rest = items.map(it => { const r = it.getBoundingClientRect(); return { left: r.left - er.left - el.clientLeft, width: r.width }; });
        width = el.clientWidth;
        // a bar cannot be narrower than its own padding (border-box): the layout below must not promise less, or the hit test drifts from what is drawn
        const cs = items.length ? getComputedStyle(items[0]) : null;
        minW = cs ? (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0) : 0;
    };
    let axisDirty = true;
    const refresh = () => {
        const now = Array.from(el.querySelectorAll(selector));
        if (now.length !== items.length || el.clientWidth !== width || now.some((x, i) => x !== items[i])) {
            const old = cur, keep = now.length === items.length;
            items = now; measure();
            cur = keep ? old : items.map(() => 0);
            axisDirty = true;
        }
    };
    const t0 = i => parseFloat(items[i].dataset.t0) || 0, t1 = i => parseFloat(items[i].dataset.t1) || 0;

    // ---- x axis: a few ticks at bar positions (start time of the bar there; end time at the right edge), plus a bracket and the exact
    // timeframe of the hovered bar. Ticks follow the magnified layout. For time buckets the axis is linear in time, for single actors it
    // is by bar position (the start time of the actor at that position), a rough guide.
    const drawAxis = (L, h) => {
        if (!axis || L.length === 0 || items.length === 0) return;
        const n = L.length, W = el.clientWidth, nt = W >= 640 ? 5 : W >= 380 ? 4 : 3;
        if (ticks.length !== nt) {
            for (const t of ticks) t.remove();
            ticks = Array.from({ length: nt }, (_, k) => { const t = mk('dz-tick' + (k === 0 ? ' first' : k === nt - 1 ? ' lastt' : '')); t.append(mk('', 'i'), mk('', 'em')); axis.prepend(t); return t; });
            axisDirty = true;
        }
        ticks.forEach((t, k) => {
            const idx = Math.round(k / (nt - 1) * n), end = idx >= n, i = end ? n - 1 : idx;
            t.style.left = (end ? L[i].left + L[i].width : L[i].left) + 'px';
            if (axisDirty) t.lastChild.textContent = fmt(end ? t1(i) : t0(i), false);
        });
        axisDirty = false;
        if (h < 0) { mark.style.opacity = 0; readout.style.opacity = 0; for (const t of ticks) t.style.opacity = ''; return; }
        const it = items[h].dataset, a = t0(h), b = t1(h), bk = !!it.bucket;
        const text = it.run ? fmt(a, !bk) + ' → now' : fmt(a, !bk) + (bk ? ' – ' : ' → ') + fmt(b, !bk);
        if (readout.__t !== text) { readout.__t = text; readout.textContent = text; readout.__w = text.length * CW + 18; }
        const gap = L[h].left + 2, w = Math.max(5, L[h].width - 4), cx = gap + w / 2, rw = readout.__w;
        const rx = Math.max(2, Math.min(W - rw - 2, cx - rw / 2));
        mark.style.opacity = 1; mark.style.transform = `translateX(${gap}px)`; mark.style.width = w + 'px';
        readout.style.opacity = 1; readout.style.transform = `translateX(${rx}px)`; readout.style.setProperty('--tx', (cx - rx) + 'px');
        ticks.forEach(t => { const x = parseFloat(t.style.left); t.style.opacity = x > rx - 34 && x < rx + rw + 34 ? 0 : ''; });
    };

    // ---- hover card + neighbour labels. Reads (bar rect, card size) happen before the frame's writes.
    const readHover = i => {
        if (!tip || !card) return null;
        const it = i >= 0 ? items[i] : null, d = it && it.dataset;
        if (!it || !d.l1) return null;
        const bar = it.querySelector('.bar-stack, .bar');
        if (!bar) return null;
        const key = [d.l1, d.l2, d.l3, d.s, d.bad, d.slow, d.ok, d.runn].join('|');
        if (key !== cardKey) {
            cardKey = key;
            const bk = d.bucket !== undefined;
            card.dot.className = 'dz-dot ' + (d.s || ''); card.id.textContent = d.l1; card.meta.textContent = d.l2 || '';
            card.id.className = 'dz-id' + (!bk && d.s ? ' ' + d.s : '');
            card.counts.style.display = bk ? '' : 'none';
            if (bk) [d.bad, d.slow, d.ok, d.runn].forEach((v, k) => {
                const n = parseInt(v) || 0, e = card.cnt[k];
                e.children[1].textContent = n; e.className = ['bad', 'warn', 'good', 'run'][k] + (n === 0 ? ' zero' : '');
                e.style.display = k === 3 && n === 0 ? 'none' : '';
            });
            card.sub.textContent = d.l3 || ''; card.sub.style.display = d.l3 ? '' : 'none';
        }
        const br = bar.getBoundingClientRect(), er = el.getBoundingClientRect(), sc = el.parentElement;
        const vis = sc && sc.scrollWidth > sc.clientWidth ? [sc.scrollLeft, sc.scrollLeft + sc.clientWidth] : [0, el.clientWidth];
        return { top: br.top - er.top - el.clientTop, h: br.height, w: tip.offsetWidth, ch: tip.offsetHeight, vis };
    };
    const showCard = (i, info) => {
        if (!tip) return;
        if (!info) { tip.classList.remove('show'); return; }
        const L = layout[i], bw = L.width - 4, cx = L.left + L.width / 2, s = tip.style;
        const inside = bw >= info.w + 8 && info.h >= info.ch + 14;
        tip.classList.toggle('inside', inside);
        tip.classList.add('show');
        let x, y;
        if (inside) { x = L.left + 2 + (bw - info.w) / 2; y = info.top + 6; }
        else { x = Math.max(info.vis[0] + 2, Math.min(info.vis[1] - info.w - 2, cx - info.w / 2)); y = Math.max(1, info.top - info.ch - 9); }
        s.transform = `translate(${x.toFixed(1)}px, ${y.toFixed(1)}px)`;
        s.setProperty('--tx', Math.max(12, Math.min(info.w - 12, cx - x)).toFixed(1) + 'px');
    };
    // Greedy outwards from the hovered bar on each side: a label is centred under its bar, shown only if it does not overlap the previous
    // one (or the hovered bar), so labels never overlap; the opacity fades with the distance.
    const showNear = h => {
        if (!strip) return;
        const W = el.clientWidth;
        let slot = 0;
        for (const dir of [-1, 1]) {
            let edge = h >= 0 ? (dir < 0 ? layout[h].left : layout[h].left + layout[h].width) : 0;
            for (let a = 1; a <= NEAR; a++) {
                const n = near[slot++], j = h + dir * a;
                let shown = false;
                if (h >= 0 && j >= 0 && j < items.length && !items[j].classList.contains('empty')) {
                    const ds = items[j].dataset, L = layout[j];
                    const w = Math.max((ds.n1 || '').length, (ds.n2 || '').length) * CW + 2, cx = L.left + L.width / 2, x0 = cx - w / 2;
                    const ok = dir < 0 ? cx + w / 2 <= edge - 1 && x0 >= 0 : x0 >= edge + 1 && x0 + w <= W;
                    if (ok) {
                        shown = true;
                        edge = dir < 0 ? x0 - 3 : x0 + w + 3;
                        n.style.transform = `translateX(${x0.toFixed(1)}px)`; n.style.width = w.toFixed(1) + 'px';
                        n.style.opacity = Math.max(0.22, 0.8 - 0.14 * (a - 1)).toFixed(2);
                        if (n.firstChild.textContent !== ds.n1) n.firstChild.textContent = ds.n1;
                        const nc = ds.ns || ''; if (n.firstChild.className !== nc) n.firstChild.className = nc;
                        if (n.lastChild.textContent !== ds.n2) n.lastChild.textContent = ds.n2;
                    }
                }
                if (!shown && n.style.opacity !== '0') n.style.opacity = 0;
            }
        }
    };
    const frame = ts => {
        raf = 0; window.__dock.frames++;
        refresh();
        const n = items.length;
        if (n === 0) return;
        const hov = active ? hit(px) : -1;               // from the previous layout
        const info = hov >= 0 ? readHover(hov) : null;   // reads first, then this frame's writes
        const dt = last ? Math.min(ts - last, 64) : 16; last = ts;
        const k = reduced() ? 1 : 1 - Math.exp(-dt / SMOOTH_MS);
        const span = rest[n - 1].left + rest[n - 1].width - rest[0].left;
        const pitch = span / n;
        const { R, peak } = plan(span, pitch, Math.min(2.5, Math.max(0.5, parseFloat(el.dataset.zoom) || 1)));
        let moving = false, sum = 0, total = 0;
        const w = new Array(n);
        for (let i = 0; i < n; i++) {
            const d = Math.abs(rest[i].left + rest[i].width / 2 - px) / (pitch * R);
            const t = active && d < 1 ? 0.5 * (1 + Math.cos(Math.PI * d)) : 0;
            let c = cur[i] + (t - cur[i]) * k;
            if (Math.abs(t - c) < 0.002) c = t; else moving = true;
            cur[i] = c;
            w[i] = rest[i].width * (1 + (peak - 1) * c);
            sum += w[i]; total += rest[i].width;
        }
        // keeps the total width fixed; bars that would get narrower than minW stay at minW and the others share the rest
        const fin = new Array(n); let f = total / sum;
        for (let pass = 0; pass < 4; pass++) {
            let fixed = 0, free = 0, again = false;
            for (let i = 0; i < n; i++) { if (fin[i] === minW) fixed++; else free += w[i]; }
            f = free > 0 ? Math.max(0, total - fixed * minW) / free : 1;
            for (let i = 0; i < n; i++) if (fin[i] !== minW && w[i] * f < minW) { fin[i] = minW; again = true; }
            if (!again) break;
        }
        let x = rest[0].left;
        layout = new Array(n);
        for (let i = 0; i < n; i++) {
            const wi = fin[i] === minW ? minW : w[i] * f;
            const gap = i < n - 1 ? rest[i + 1].left - rest[i].left - rest[i].width : 0;
            layout[i] = { left: x, width: wi + gap };
            const it = items[i];
            it.style.flex = `0 0 ${wi}px`; it.style.minWidth = '0'; it.style.maxWidth = 'none';
            it.style.setProperty('--dz', (1 + TALL * cur[i]).toFixed(3));
            x += wi + gap;
        }
        const h = info ? hov : -1;
        showCard(h, info); showNear(h); drawAxis(layout, h);
        if (active || moving) raf = requestAnimationFrame(frame);
        else { clear(); layout = []; last = 0; showCard(-1, null); showNear(-1); drawAxis(rest, -1); }
    };
    // static axis at rest, redrawn when bars change (Blazor re-render) or the chart is resized
    let idleRaf = 0;
    const idle = () => { if (idleRaf) return; idleRaf = requestAnimationFrame(() => { idleRaf = 0; if (active || raf) return; refresh(); drawAxis(rest, -1); glide(); }); };
    const attrWatch = new MutationObserver(idle);
    attrWatch.observe(el, { childList: true, attributes: true, attributeFilter: ['data-t0', 'data-t1', 'data-key'], subtree: true });
    const sizeWatch = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => { axisDirty = true; idle(); }) : null;
    if (sizeWatch) sizeWatch.observe(el);
    idle();
    const kick = () => { if (!raf) raf = requestAnimationFrame(frame); };
    const rel = e => e.clientX - el.getBoundingClientRect().left - el.clientLeft;
    // the item visually under x in the magnified layout (-1 if none or an empty slot)
    const hit = x => {
        if (layout.length === 0) { refresh(); layout = rest.map(r => ({ left: r.left, width: r.width })); }
        for (let i = 0; i < layout.length; i++)
            if (x >= layout[i].left && x < layout[i].left + layout[i].width) return items[i].classList.contains('empty') ? -1 : i;
        return -1;
    };
    // Blazor gets the key of the bar (actor or time bucket), not its index, so a render between pointer-down and the call cannot change the target.
    const pickKey = key => { if (key) { window.__dock.calls++; window.__dock.lastKey = key; ref.invokeMethodAsync('Pick', key); } };
    const pick = e => { const i = hit(rel(e)); pickKey(i >= 0 ? items[i].dataset.key : null); };

    // ---- brush (brain 0023, update 3): a mouse drag of more than 6 px is a brush, less is a click (the bar under the pointer at press time is
    // picked on release). On touch, press and hold (~0.4 s) without moving, then drag; moving at once keeps the magnifying finger.
    // The brush covers the bars it overlaps in the resting layout; Blazor gets the time frame (data-t0 .. data-t1 of those bars).
    const THRESH = 6, HOLD = 380;
    const brushEl = select ? el.querySelector(':scope > .dz-brush') : null;
    let drag = null, holdTimer = 0, swallow = -1e9;
    const spanOf = (a, b) => {
        refresh(); const lo = Math.min(a, b), hi = Math.max(a, b), idx = [];
        rest.forEach((q, i) => { if (q.left + q.width > lo && q.left < hi) idx.push(i); });
        return idx;
    };
    const rangeOf = idx => { let a = Infinity, b = -Infinity; for (const i of idx) { a = Math.min(a, t0(i)); b = Math.max(b, t1(i)); } return [a, b]; };
    const startBrush = () => {
        if (!drag || drag.brushing || !brushEl) return;
        drag.brushing = true; active = false; kick();
        el.dataset.brushing = '1';
        try { el.setPointerCapture(drag.id); } catch (_) { }
        try { if (drag.touch && navigator.vibrate) navigator.vibrate(12); } catch (_) { }
    };
    const updateBrush = x => {
        x = Math.max(0, Math.min(el.clientWidth, x)); drag.x = x;
        const lo = Math.min(drag.x0, x), hi = Math.max(drag.x0, x), idx = spanOf(drag.x0, x);
        brushEl.classList.add('show'); brushEl.style.left = lo + 'px'; brushEl.style.width = (hi - lo) + 'px';
        if (idx.length) {
            const [a, b] = rangeOf(idx), tenths = !items[idx[0]].dataset.bucket;
            brushEl.firstChild.textContent = fmt(a, tenths) + ' – ' + fmt(b, tenths);
        }
    };
    const finishBrush = apply => {
        const d = drag; drag = null;
        if (holdTimer) { clearTimeout(holdTimer); holdTimer = 0; }
        if (!d || !d.brushing) return false;
        delete el.dataset.brushing; brushEl.classList.remove('show');
        try { el.releasePointerCapture(d.id); } catch (_) { }
        const idx = apply ? spanOf(d.x0, d.x) : [];
        if (idx.length) { const [a, b] = rangeOf(idx); window.__dock.brushes = (window.__dock.brushes || 0) + 1; ref.invokeMethodAsync('Brush', a, b, idx[idx.length - 1] >= items.length - 1); }
        swallow = performance.now();
        return true;
    };

    const onMove = e => { px = rel(e); active = true; kick(); };
    const onDown = e => {
        if (!select) return;
        const x = rel(e);
        if (e.pointerType === 'touch') {
            down = true; onMove(e);
            drag = { id: e.pointerId, x0: x, x, cx: e.clientX, cy: e.clientY, touch: true, brushing: false };
            if (holdTimer) clearTimeout(holdTimer);
            holdTimer = setTimeout(() => { holdTimer = 0; if (drag && !drag.moved) startBrush(); }, HOLD);
            syncAim();
        } else if (e.button === 0) {
            const i = hit(x);
            drag = { id: e.pointerId, x0: x, x, cx: e.clientX, cy: e.clientY, touch: false, brushing: false, key: i >= 0 ? items[i].dataset.key : null };
        }
    };
    const onPointerMove = e => {
        const touch = e.pointerType === 'touch';
        if (drag && !touch && e.buttons === 0) drag = null;
        if (drag && select) {
            const dist = Math.hypot(e.clientX - drag.cx, e.clientY - drag.cy);
            if (!drag.brushing && dist >= THRESH) {
                if (touch) { drag.moved = true; if (holdTimer) { clearTimeout(holdTimer); holdTimer = 0; } }
                else startBrush();
            }
            if (drag.brushing) { updateBrush(rel(e)); return; }
        }
        if (touch) { if (!select) return; }
        else if (!hover) { hover = true; syncAim(); }
        onMove(e);
    };
    const onEnter = e => { if (e.pointerType !== 'touch' && !hover) { hover = true; syncAim(); } };
    const onLeave = e => {
        if (e.pointerType === 'touch') return;
        if (drag && drag.brushing) return;
        drag = null; active = false; hover = false; kick(); syncAim();
    };
    const onUp = e => {
        if (!select) return;
        const apply = e.type === 'pointerup';
        if (e.pointerType !== 'touch') {
            const d = drag;
            if (d && d.brushing) { finishBrush(apply); active = false; kick(); }
            else if (d && apply && e.button === 0) { drag = null; pickKey(d.key); }
            return;
        }
        px = rel(e);
        if (!finishBrush(apply) && apply) { pick(e); touchDone = performance.now(); }
        down = false; active = false; kick(); syncAim();
    };
    const onClick = e => {
        if (!select || e.detail === 0) return;      // keyboard activation goes straight to Blazor
        e.stopPropagation(); e.preventDefault();    // mouse/pen picked on pointer-down, touch on release
    };
    const onFocusIn = e => { if (e.target.matches && e.target.matches(':focus-visible')) { focused = true; syncAim(); } };
    const onFocusOut = () => { focused = false; syncAim(); };
    // Blazor resumes (badge click): forget the aiming state without telling Blazor again.
    el.__release = () => { if (aimTimer) { clearTimeout(aimTimer); aimTimer = 0; } if (notified) { notified = false; snapshot(); } hover = down = focused = false; };
    el.addEventListener('pointerdown', onDown);
    el.addEventListener('pointerenter', onEnter);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerleave', onLeave);
    el.addEventListener('pointerup', onUp);
    el.addEventListener('pointercancel', onUp);
    el.addEventListener('click', onClick, true);
    el.addEventListener('focusin', onFocusIn);
    el.addEventListener('focusout', onFocusOut);
    el.__dock = () => {
        el.removeEventListener('pointerdown', onDown); el.removeEventListener('pointermove', onPointerMove);
        el.removeEventListener('pointerleave', onLeave); el.removeEventListener('pointerup', onUp);
        el.removeEventListener('pointercancel', onUp); if (holdTimer) clearTimeout(holdTimer); el.removeEventListener('click', onClick, true);
        el.removeEventListener('pointerenter', onEnter); el.removeEventListener('focusin', onFocusIn); el.removeEventListener('focusout', onFocusOut);
        if (aimTimer) clearTimeout(aimTimer);
        if (raf) cancelAnimationFrame(raf);
        clear(); attrWatch.disconnect(); if (sizeWatch) sizeWatch.disconnect();
        if (idleRaf) cancelAnimationFrame(idleRaf);
        if (tip) tip.classList.remove('show');
        tracked.delete(el); watch.disconnect();
    };
}

export function release(el) { if (el && el.__release) el.__release(); }

export function detach(el) { if (el && el.__dock) { el.__dock(); el.__dock = null; } }
