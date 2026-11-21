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
const fmt = (s, dec) => {      // dec: false/0 whole seconds, true/1 tenths, 2 hundredths
    s = Math.max(0, s);
    const d = dec === true ? 1 : dec || 0;
    const whole = Math.floor(s), f = d ? '.' + String(Math.floor((s - whole) * 10 ** d + 1e-6)).padStart(d, '0') : '';
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
    const box = el.querySelector(':scope > .dz-bars') || el;      // the bars; in a zoomed chart this strip is wider than the chart and slid by a transform
    const kids = () => Array.from(box.querySelectorAll(selector));
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
    const keyed = () => Array.from(box.querySelectorAll(':scope > .bar-hit[data-key]'));
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

    // ---- stable time grid (brain 0031 update 2). A zoomed chart (data-grid = bucket width in s, data-buf = start time of the first bar, data-win =
    // the view in run seconds) holds bars for a buffer around the view. The script owns the pan: the view is moved here at frame rate by sliding
    // the bar strip (transform), and Blazor is told the view only now and then. It rebuilds the buffer when the view nears its edge.
    let grid = null, off = 0, Wc = el.clientWidth;
    let view = null, owns = false, ownTimer = 0;          // the view while the script drives it (edge pan, overview strip), until Blazor has caught up
    let edge = 0, edgeRaf = 0, edgeLast = 0, zoomNext = null, zoomTimer = 0, zoomAt = 0, strip2 = null;
    const runEnd = () => (parseFloat(el.dataset.elapsed) || 0) / 1000;
    const readGrid = () => {
        const bw = parseFloat(el.dataset.grid), buf = parseFloat(el.dataset.buf), wv = (el.dataset.win || '').split(' ').map(parseFloat);
        grid = bw > 0 && buf >= 0 && wv.length === 2 && wv[1] > wv[0] ? { bw, buf, a: wv[0], b: wv[1] } : null;
    };
    const curView = () => !grid ? null : owns && view ? view : [grid.a, grid.b];
    const gridDec = () => !grid ? 0 : grid.bw < 0.1 ? 2 : grid.bw < 1 ? 1 : 0;
    const own = () => {
        owns = true; if (ownTimer) clearTimeout(ownTimer);
        ownTimer = setTimeout(() => { ownTimer = 0; owns = false; view = null; syncGrid(); }, 700);
    };
    const releaseOwn = () => { owns = false; view = null; if (ownTimer) { clearTimeout(ownTimer); ownTimer = 0; } };
    const over = select ? el.querySelector(':scope > .dz-over') : null;
    let ovWin = null;
    if (over) { ovWin = mk('ov-win'); over.append(mk('ov-track'), ovWin); }
    const updateOver = () => {
        if (!over) return;
        const v = curView(), E = runEnd();
        if (!v || E <= 0) return;
        const a = Math.max(0, Math.min(1, v[0] / E)), b = Math.max(a, Math.min(1, v[1] / E));
        ovWin.style.left = (a * 100).toFixed(3) + '%'; ovWin.style.width = ((b - a) * 100).toFixed(3) + '%';
    };
    const applyOff = () => {
        const v = curView(), o = grid && v ? -(v[0] - grid.buf) / (v[1] - v[0]) * Wc : 0;
        if (o !== off) { off = o; box.style.transform = o ? `translateX(${o}px)` : ''; if (active) kick(); else idle(); }
        updateOver();
    };
    const syncGrid = () => {
        readGrid();
        if (!grid) releaseOwn();
        else if (owns && view && !edgeRaf && !strip2 && Math.abs(grid.a - view[0]) < 0.03 * (view[1] - view[0])) releaseOwn();
        applyOff(); idle();
    };
    // the view goes to Blazor at most every 90 ms, and once more at the end of a gesture; Blazor clamps it and follows the live edge
    const queueZoom = (a, b, force, gap) => {
        zoomNext = [a, b];
        const now = performance.now();
        const g = gap || 90;
        if (!force && now - zoomAt < g) { if (!zoomTimer) zoomTimer = setTimeout(() => { zoomTimer = 0; if (zoomNext) queueZoom(zoomNext[0], zoomNext[1], true); }, g); return; }
        if (zoomTimer) { clearTimeout(zoomTimer); zoomTimer = 0; }
        zoomAt = now; const z = zoomNext; zoomNext = null;
        window.__dock.zooms = (window.__dock.zooms || 0) + 1;
        ref.invokeMethodAsync('Zoom', z[0], z[1]);
    };
    const flushZoom = () => { if (zoomNext) queueZoom(zoomNext[0], zoomNext[1], true); };
    // the time under chart x (the chart's current layout L)
    const timeAt = (L, x) => {
        let lo = 0, hi = L.length - 1;
        if (hi >= 0 && x >= L[0].left && x < L[hi].left + L[hi].width) {
            while (lo < hi) { const m = (lo + hi + 1) >> 1; if (L[m].left <= x) lo = m; else hi = m - 1; }
            return grid.buf + (lo + (x - L[lo].left) / L[lo].width) * grid.bw;
        }
        const v = curView(); return v[0] + x / Wc * (v[1] - v[0]);
    };
    const restEl = () => rest.map(r => ({ left: r.left + off, width: r.width }));

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
        for (const it of items) { it.style.flex = ''; it.style.minWidth = ''; it.style.maxWidth = ''; it.style.removeProperty('--dz'); it.classList.remove('mag'); it.__fl = it.__dz = it.__mg = undefined; }
    };
    const measure = () => {
        clear();
        // fractional widths (offsetWidth rounds, which matters for 1.5px bars on a phone). rest is in chart coordinates at offset 0: the strip's own
        // transform cancels out because the box rect moves with it.
        const er = box.getBoundingClientRect(), base = box === el ? el.clientLeft : (parseFloat(getComputedStyle(el).paddingLeft) || 0) + el.clientLeft;
        rest = items.map(it => { const r = it.getBoundingClientRect(); return { left: r.left - er.left + (box === el ? -el.clientLeft : base), width: r.width }; });
        width = el.clientWidth;
        // a bar cannot be narrower than its own padding (border-box): the layout below must not promise less, or the hit test drifts from what is drawn
        const cs = items.length ? getComputedStyle(items[0]) : null;
        minW = cs ? (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0) : 0;
    };
    let axisDirty = true, psig = '';
    const refresh = () => {
        const now = kids();
        const ps = el.style.getPropertyValue('--p');
        if (now.length !== items.length || el.clientWidth !== width || ps !== psig || now.some((x, i) => x !== items[i])) {
            psig = ps;
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
            if (grid) {      // time grid: ticks at fixed fractions of the chart, the time under them follows the pan
                const x = k / (nt - 1) * W, text = fmt(timeAt(L, x), gridDec());
                t.style.left = x + 'px';
                if (t.__s !== text) { t.__s = text; t.lastChild.textContent = text; }
                return;
            }
            const idx = Math.round(k / (nt - 1) * n), end = idx >= n, i = end ? n - 1 : idx;
            t.style.left = (end ? L[i].left + L[i].width : L[i].left) + 'px';
            if (axisDirty) t.lastChild.textContent = fmt(end ? t1(i) : t0(i), false);
        });
        axisDirty = false;
        if (h < 0) { mark.style.opacity = 0; readout.style.opacity = 0; for (const t of ticks) t.style.opacity = ''; return; }
        const it = items[h].dataset, a = t0(h), b = t1(h), bk = !!it.bucket;
        const dc = bk ? gridDec() : 1;
        const text = it.run ? fmt(a, dc) + ' → now' : fmt(a, dc) + (bk ? ' – ' : ' → ') + fmt(b, dc);
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
        const { R, peak } = plan(grid ? Wc : span, pitch, Math.min(2.5, Math.max(0.5, parseFloat(el.dataset.zoom) || 1)));
        const ps = px - off;      // pointer in strip coordinates
        // the bars that are in view: the total width of those stays fixed (the rest of a buffered strip is left alone)
        let lo = 0, hi = n - 1;
        if (grid) {
            lo = n; hi = -1;
            for (let i = 0; i < n; i++) if (rest[i].left + rest[i].width > -off && rest[i].left < -off + Wc) { if (i < lo) lo = i; hi = i; }
        }
        let moving = false, sum = 0, total = 0;
        const w = new Array(n);
        for (let i = 0; i < n; i++) {
            const d = Math.abs(rest[i].left + rest[i].width / 2 - ps) / (pitch * R);
            const t = active && d < 1 ? 0.5 * (1 + Math.cos(Math.PI * d)) : 0;
            let c = cur[i] + (t - cur[i]) * k;
            if (Math.abs(t - c) < 0.002) c = t; else moving = true;
            cur[i] = c;
            w[i] = rest[i].width * (1 + (peak - 1) * c);
            if (i >= lo && i <= hi) { sum += w[i]; total += rest[i].width; }
        }
        // keeps the total width fixed; bars that would get narrower than minW stay at minW and the others share the rest
        const fin = new Array(n); let f = sum > 0 ? total / sum : 1;
        for (let pass = 0; pass < 4; pass++) {
            let fixed = 0, free = 0, again = false;
            for (let i = lo; i <= hi; i++) { if (fin[i] === minW) fixed++; else free += w[i]; }
            f = free > 0 ? Math.max(0, total - fixed * minW) / free : 1;
            for (let i = lo; i <= hi; i++) if (fin[i] !== minW && w[i] * f < minW) { fin[i] = minW; again = true; }
            if (!again) break;
        }
        let x = rest[0].left;
        layout = new Array(n);
        for (let i = 0; i < n; i++) {
            const inr = i >= lo && i <= hi;
            const wi = !inr ? rest[i].width : fin[i] === minW ? minW : w[i] * f;
            const gap = i < n - 1 ? rest[i + 1].left - rest[i].left - rest[i].width : 0;
            layout[i] = { left: x + off, width: wi + gap };
            const it = items[i];
            const fl = `0 0 ${wi.toFixed(2)}px`, dz = (1 + TALL * cur[i]).toFixed(3);      // only touch what changed (a zoomed chart holds several hundred bars)
            if (it.__fl !== fl) { it.__fl = fl; it.style.flex = fl; it.style.minWidth = '0'; it.style.maxWidth = 'none'; }
            if (it.__dz !== dz) { it.__dz = dz; it.style.setProperty('--dz', dz); }
            const mg = cur[i] > 0.5; if (it.__mg !== mg) { it.__mg = mg; it.classList.toggle('mag', mg); }   // group badges show on magnified bars (brain 0032)
            x += wi + gap;
        }
        const h = info ? hov : -1;
        showCard(h, info); showNear(h); drawAxis(layout, h);
        if (active || moving) raf = requestAnimationFrame(frame);
        else { clear(); layout = []; last = 0; showCard(-1, null); showNear(-1); drawAxis(restEl(), -1); }
    };
    // static axis at rest, redrawn when bars change (Blazor re-render) or the chart is resized
    let idleRaf = 0;
    const idle = () => { if (idleRaf) return; idleRaf = requestAnimationFrame(() => { idleRaf = 0; if (active || raf) return; refresh(); drawAxis(restEl(), -1); glide(); }); };
    const attrWatch = new MutationObserver(idle);
    attrWatch.observe(el, { childList: true, attributes: true, attributeFilter: ['data-t0', 'data-t1', 'data-key'], subtree: true });
    const sizeWatch = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => { axisDirty = true; Wc = el.clientWidth; applyOff(); idle(); }) : null;
    const gridWatch = new MutationObserver(syncGrid);     // runs before the next paint: a rebuilt buffer never shows at the wrong offset
    gridWatch.observe(el, { attributes: true, attributeFilter: ['data-grid', 'data-buf', 'data-win', 'data-elapsed', 'data-zoomed', 'style'] });
    if (box !== el) gridWatch.observe(box, { childList: true });
    syncGrid();
    if (sizeWatch) sizeWatch.observe(el);
    idle();
    function kick() { if (!raf) raf = requestAnimationFrame(frame); }
    const rel = e => e.clientX - el.getBoundingClientRect().left - el.clientLeft;
    // the item visually under x in the magnified layout (-1 if none or an empty slot)
    const hit = x => {
        if (layout.length === 0) { refresh(); layout = restEl(); }
        for (let i = 0; i < layout.length && i < items.length; i++)
            if (x >= layout[i].left && x < layout[i].left + layout[i].width) return items[i].classList.contains('empty') ? -1 : i;
        return -1;
    };
    // Blazor gets the key of the bar (actor or time bucket), not its index, so a render between pointer-down and the call cannot change the target.
    const pickKey = key => { if (key) { window.__dock.calls++; window.__dock.lastKey = key; ref.invokeMethodAsync('Pick', key); } };
    const pick = e => { const i = hit(rel(e)); pickKey(i >= 0 ? items[i].dataset.key : null); };

    // ---- brush (brain 0023, update 3): a mouse drag of more than 6 px is a brush, less is a click (the bar under the pointer at press time is
    // picked on release). The brush covers the bars it overlaps in the resting layout; Blazor gets the time frame (data-t0 .. data-t1 of those bars).
    // ---- touch (brain 0031, update 2): a tap picks the bar (no magnification), a second tap close by zooms in 2x; a sideways swipe turns the
    // magnifier on under the finger and moves with it, lifting selects the bar under the finger; while zoomed, a finger held at the left or right
    // edge pans the view that way (faster the nearer the edge); two fingers pinch the time range; a vertical swipe is left to the browser (scroll).
    // The chart only stops the page from scrolling while a swipe-magnify or pinch is going on (touch-action: pan-y does the rest).
    const THRESH = 6, SLOP = 9, MINSPAN = 0.5, DT = 350, DD = 24;
    const brushEl = select ? el.querySelector(':scope > .dz-brush') : null;
    let drag = null;
    const pts = new Map();            // touch pointers on the chart: id -> {x, y}
    let tmode = '';                    // '' | 'wait' (finger down, may become a tap) | 'scroll' | 'mag' (swipe-magnifier) | 'pinch' | 'done' (gesture over, fingers still down)
    let tkey = null, tdown = null, pinch = null, lastTap = null;
    const spanOf = (a, b) => {
        refresh(); const lo = Math.min(a, b), hi = Math.max(a, b), idx = [];
        rest.forEach((q, i) => { if (q.left + off + q.width > lo && q.left + off < hi) idx.push(i); });
        return idx;
    };
    const rangeOf = idx => { let a = Infinity, b = -Infinity; for (const i of idx) { a = Math.min(a, t0(i)); b = Math.max(b, t1(i)); } return [a, b]; };
    const startBrush = () => {
        if (!drag || drag.brushing || !brushEl) return;
        drag.brushing = true; active = false; kick();
        el.dataset.brushing = '1';
        try { el.setPointerCapture(drag.id); } catch (_) { }
    };
    const updateBrush = x => {
        x = Math.max(0, Math.min(el.clientWidth, x)); drag.x = x;
        const lo = Math.min(drag.x0, x), hi = Math.max(drag.x0, x), idx = spanOf(drag.x0, x);
        brushEl.classList.add('show'); brushEl.style.left = lo + 'px'; brushEl.style.width = (hi - lo) + 'px';
        if (idx.length) {
            const [a, b] = rangeOf(idx), dc = items[idx[0]].dataset.bucket ? gridDec() : 1;
            brushEl.firstChild.textContent = fmt(a, dc) + ' – ' + fmt(b, dc);
        }
    };
    const finishBrush = apply => {
        const d = drag; drag = null;
        if (!d || !d.brushing) return false;
        delete el.dataset.brushing; brushEl.classList.remove('show');
        try { el.releasePointerCapture(d.id); } catch (_) { }
        const idx = apply ? spanOf(d.x0, d.x) : [];
        if (idx.length) { const [a, b] = rangeOf(idx); window.__dock.brushes = (window.__dock.brushes || 0) + 1; ref.invokeMethodAsync('Brush', a, b, idx[idx.length - 1] >= items.length - 1); }
        return true;
    };

    // The time range in view: the zoom window of a zoomed chart, else the extent of the bars.
    const curRange = () => {
        const v = curView(); if (v) return v;
        refresh(); let a = Infinity, b = -Infinity;
        for (let i = 0; i < items.length; i++) { a = Math.min(a, t0(i)); b = Math.max(b, t1(i)); }
        return [a, b];
    };
    // pinch: spreading the fingers zooms the time range in, pinching zooms out; the time under the fingers' midpoint stays under it.
    // Blazor gets the range live (at most every 90 ms) and once more at the end; it clamps it to the run and follows the live edge.
    const pinchStart = () => {
        if (items.length === 0) return false;
        const [a, b] = curRange();
        const [p, q] = Array.from(pts.values()), r = el.getBoundingClientRect();
        pinch = { a, b, d0: Math.max(10, Math.hypot(p.x - q.x, p.y - q.y)), frac0: ((p.x + q.x) / 2 - r.left) / Math.max(1, el.clientWidth) };
        return b > a;
    };
    const pinchMove = () => {
        const [p, q] = Array.from(pts.values()), r = el.getBoundingClientRect();
        const d = Math.max(10, Math.hypot(p.x - q.x, p.y - q.y)), frac = ((p.x + q.x) / 2 - r.left) / Math.max(1, el.clientWidth);
        const span0 = pinch.b - pinch.a, span = Math.max(MINSPAN, span0 * pinch.d0 / d);
        const tc = pinch.a + pinch.frac0 * span0, from = tc - frac * span;
        queueZoom(from, from + span);
    };
    // double tap: zoom in 2x around the tapped time
    const doubleTap = x => {
        const [a, b] = curRange(), E = runEnd();
        if (!(b > a) || E <= 0) return;
        const i = hit(x), tc = grid ? timeAt(layout.length ? layout : restEl(), x) : i >= 0 ? (t0(i) + t1(i)) / 2 : a + x / Math.max(1, Wc) * (b - a);
        const span = Math.max(MINSPAN, (b - a) / 2), from = Math.max(0, Math.min(tc - span / 2, Math.max(0, E - span)));
        window.__dock.doubleTaps = (window.__dock.doubleTaps || 0) + 1;
        queueZoom(from, from + span, true);
    };
    // edge pan: with a finger held in the outer ~10 % (at least 32 px) of the chart, the view moves that way, faster the nearer the edge
    const edgeStep = ts => {
        edgeRaf = 0;
        if (!edge || tmode !== 'mag') { edge = 0; return; }
        const v = curView(); if (!v) { edge = 0; return; }
        const dt = edgeLast ? Math.min(0.05, (ts - edgeLast) / 1000) : 0.016; edgeLast = ts;
        const span = v[1] - v[0], speed = Math.sign(edge) * Math.pow(Math.abs(edge), 1.6) * 0.7 * span;      // view widths per second
        const from = Math.max(0, Math.min(v[0] + speed * dt, Math.max(0, runEnd() - span)));
        view = [from, from + span]; own(); applyOff();
        queueZoom(from, from + span, false, 200);
        edgeRaf = requestAnimationFrame(edgeStep);
    };
    const edgeCheck = () => {
        let t = 0;
        if (grid && tmode === 'mag') {
            const Z = Math.max(32, Math.min(80, Wc * 0.1));
            if (px < Z) t = -(Z - px) / Z; else if (px > Wc - Z) t = (px - (Wc - Z)) / Z;
        }
        edge = Math.max(-1, Math.min(1, t));
        if (edge && !edgeRaf) { edgeLast = 0; edgeRaf = requestAnimationFrame(edgeStep); window.__dock.edgePans = (window.__dock.edgePans || 0) + 1; }
    };
    const endTouch = () => {      // all fingers up (or the gesture was taken away)
        edge = 0; if (pinch) pinch = null;
        flushZoom();
        pts.clear(); tmode = ''; tkey = null; tdown = null; down = false; active = false; kick(); syncAim();
    };
    const onTouchDown = e => {
        pts.set(e.pointerId, { x: e.clientX, y: e.clientY });
        down = true;
        if (pts.size === 1) {
            tmode = 'wait'; tkey = null; tdown = { x: e.clientX, y: e.clientY };
            const i = hit(rel(e)); tkey = i >= 0 ? items[i].dataset.key : null;
            px = rel(e);
        } else if (pts.size === 2) {
            edge = 0; active = false; kick(); lastTap = null;
            tmode = pinchStart() ? 'pinch' : 'done';
        } else tmode = 'done';
        syncAim();
    };
    const onTouchMove = e => {
        const p = pts.get(e.pointerId); if (!p) return;
        p.x = e.clientX; p.y = e.clientY;
        if (tmode === 'wait') {
            const dx = e.clientX - tdown.x, dy = e.clientY - tdown.y;
            if (Math.hypot(dx, dy) >= SLOP) {
                lastTap = null;
                if (Math.abs(dx) > Math.abs(dy)) {
                    tmode = 'mag'; px = rel(e); active = true; kick(); edgeCheck();
                    window.__dock.mags = (window.__dock.mags || 0) + 1;
                    try { el.setPointerCapture(e.pointerId); } catch (_) { }
                } else tmode = 'scroll';
            }
        } else if (tmode === 'mag') { px = rel(e); active = true; kick(); edgeCheck(); }
        else if (tmode === 'pinch') pinchMove();
    };
    const onTouchUp = e => {
        const was = pts.has(e.pointerId); pts.delete(e.pointerId);
        if (!was) return;
        const up = e.type === 'pointerup';
        if (tmode === 'wait' && up) {
            const k = tkey, now = e.timeStamp, x = rel(e);
            const dbl = lastTap && now - lastTap.t < DT && Math.hypot(e.clientX - lastTap.x, e.clientY - lastTap.y) < DD;
            endTouch();
            if (dbl) { lastTap = null; doubleTap(x); } else { lastTap = { t: now, x: e.clientX, y: e.clientY }; pickKey(k); }
            return;
        }
        if (tmode === 'mag' && up) {      // lifting selects the bar under the finger, like a tap
            const i = hit(px), k = i >= 0 ? items[i].dataset.key : null;
            endTouch(); pickKey(k); return;
        }
        if (tmode === 'pinch' && pts.size === 1) { flushZoom(); pinch = null; tmode = 'done'; active = false; kick(); }
        if (pts.size === 0) endTouch();
    };
    // Stop the page from scrolling or zooming while a swipe-magnify or pinch is going on (needs a non-passive listener); a vertical swipe still scrolls.
    const onTouchMoveEv = e => { if (tmode === 'mag' || tmode === 'pinch' || tmode === 'done' || e.touches.length > 1) { if (e.cancelable) e.preventDefault(); } };
    const onContext = e => { if (tmode) e.preventDefault(); };

    // ---- overview strip (brain 0035): the whole run as a thin line, the lit part is the view. Tap or drag (mouse or touch) moves the view.
    if (over) {
        const moveStrip = e => {
            const E = runEnd(), from = Math.max(0, Math.min((e.clientX - strip2.r.left) / strip2.r.width * E - strip2.grab, Math.max(0, E - strip2.span)));
            view = [from, from + strip2.span]; own(); applyOff(); queueZoom(from, from + strip2.span, false, 120);
        };
        over.addEventListener('pointerdown', e => {
            const v = curView(), E = runEnd();
            if (!v || E <= 0) return;
            e.stopPropagation(); e.preventDefault();
            const r = over.getBoundingClientRect(), t = (e.clientX - r.left) / r.width * E, span = v[1] - v[0];
            strip2 = { id: e.pointerId, r, span, grab: t >= v[0] && t <= v[1] ? t - v[0] : span / 2 };
            try { over.setPointerCapture(e.pointerId); } catch (_) { }
            active = false; kick(); down = true; syncAim(); moveStrip(e);
            window.__dock.stripDrags = (window.__dock.stripDrags || 0) + 1;
        });
        over.addEventListener('pointermove', e => { e.stopPropagation(); if (strip2 && strip2.id === e.pointerId) moveStrip(e); });
        const endStrip = e => { e.stopPropagation(); if (!strip2 || strip2.id !== e.pointerId) return; strip2 = null; flushZoom(); down = false; syncAim(); };
        over.addEventListener('pointerup', endStrip); over.addEventListener('pointercancel', endStrip);
        over.addEventListener('pointerenter', e => { e.stopPropagation(); active = false; hover = false; kick(); syncAim(); });
        over.addEventListener('click', e => e.stopPropagation());
    }

    const onMove = e => { px = rel(e); active = true; kick(); };
    const onDown = e => {
        if (!select) return;
        if (e.pointerType === 'touch') { onTouchDown(e); return; }
        if (e.button === 0) {
            const x = rel(e), i = hit(x);
            drag = { id: e.pointerId, x0: x, x, cx: e.clientX, cy: e.clientY, brushing: false, key: i >= 0 ? items[i].dataset.key : null };
        }
    };
    const onPointerMove = e => {
        if (e.pointerType === 'touch') { if (select) onTouchMove(e); return; }
        if (drag && e.buttons === 0) drag = null;
        if (drag && select) {
            if (!drag.brushing && Math.hypot(e.clientX - drag.cx, e.clientY - drag.cy) >= THRESH) startBrush();
            if (drag.brushing) { updateBrush(rel(e)); return; }
        }
        if (!hover) { hover = true; syncAim(); }
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
        if (e.pointerType === 'touch') { onTouchUp(e); return; }
        const apply = e.type === 'pointerup', d = drag;
        if (d && d.brushing) { finishBrush(apply); active = false; kick(); }
        else if (d && apply && e.button === 0) { drag = null; pickKey(d.key); }
    };
    const onClick = e => {
        if (!select || e.detail === 0) return;      // keyboard activation goes straight to Blazor
        e.stopPropagation(); e.preventDefault();    // mouse/pen picked on pointer-down, touch on release
    };
    const onFocusIn = e => { if (e.target.matches && e.target.matches(':focus-visible')) { focused = true; syncAim(); } };
    const onFocusOut = () => { focused = false; syncAim(); };
    // Blazor resumes (badge click): forget the aiming state without telling Blazor again.
    el.__release = () => { if (aimTimer) { clearTimeout(aimTimer); aimTimer = 0; } if (notified) { notified = false; snapshot(); } hover = down = focused = false; pts.clear(); tmode = ''; };
    el.addEventListener('pointerdown', onDown);
    el.addEventListener('pointerenter', onEnter);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerleave', onLeave);
    el.addEventListener('pointerup', onUp);
    el.addEventListener('pointercancel', onUp);
    el.addEventListener('click', onClick, true);
    el.addEventListener('touchmove', onTouchMoveEv, { passive: false });
    el.addEventListener('contextmenu', onContext);
    el.addEventListener('focusin', onFocusIn);
    el.addEventListener('focusout', onFocusOut);
    el.__dock = () => {
        el.removeEventListener('pointerdown', onDown); el.removeEventListener('pointermove', onPointerMove);
        el.removeEventListener('pointerleave', onLeave); el.removeEventListener('pointerup', onUp);
        el.removeEventListener('pointercancel', onUp); el.removeEventListener('click', onClick, true); el.removeEventListener('touchmove', onTouchMoveEv); el.removeEventListener('contextmenu', onContext); if (zoomTimer) clearTimeout(zoomTimer);
        el.removeEventListener('pointerenter', onEnter); el.removeEventListener('focusin', onFocusIn); el.removeEventListener('focusout', onFocusOut);
        if (aimTimer) clearTimeout(aimTimer);
        if (raf) cancelAnimationFrame(raf);
        clear(); attrWatch.disconnect(); gridWatch.disconnect(); if (edgeRaf) cancelAnimationFrame(edgeRaf); if (ownTimer) clearTimeout(ownTimer); box.style.transform = ''; if (sizeWatch) sizeWatch.disconnect();
        if (idleRaf) cancelAnimationFrame(idleRaf);
        if (tip) tip.classList.remove('show');
        tracked.delete(el); watch.disconnect();
    };
}

export function release(el) { if (el && el.__release) el.__release(); }

export function detach(el) { if (el && el.__dock) { el.__dock(); el.__dock = null; } }
