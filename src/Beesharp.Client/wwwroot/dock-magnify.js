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

// Running bees (brain 0018): bars with data-start (ms into the run) grow with the clock. The chart carries data-elapsed (run ms at the
// last render) and data-scale (ms for full height). One shared loop sets the heights, so Blazor does not re-render at frame rate.
const tracked = new Set();
let growRaf = 0;
const growFrame = () => {
    growRaf = 0;
    let any = false;
    const t = performance.now();
    for (const el of tracked) {
        if (!el || !el.isConnected) { tracked.delete(el); continue; }
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
    if (!el) return;
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
    // Unfolded bursts (brain 0038, 0040): the bars of a burst share the empty grid slots the burst borrowed (data-k = slots per bar), so x stays linear
    // in time over the whole chart. slotAt[i] is the slot offset of bar i's left edge from the buffer start, slotW[i] its width in slots (1 for slot bars);
    // bursts maps data-ub to the first and last bar of the burst (for its label).
    let slotAt = [], slotW = [], bursts = new Map(), strBase = 0;
    const buildSlots = () => {
        slotAt = new Array(items.length); slotW = new Array(items.length); bursts = new Map();
        let s = 0;
        for (let i = 0; i < items.length; i++) {
            const d = items[i].dataset, k = d.k !== undefined ? (parseFloat(d.k) || 1) : 1, ub = d.ub;
            slotAt[i] = s; slotW[i] = k; s += k;
            if (ub !== undefined) { if (!bursts.has(ub)) bursts.set(ub, [i, i]); else bursts.get(ub)[1] = i; }
        }
    };
    // x (strip coordinates, resting layout) of a time: linear, also across unfolded bursts
    const xOfTime = (t, span) => (t - grid.buf) / grid.bw * (Wc * grid.bw / span);
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
    // The strip shows where failed (red) and slow (amber) bees were over the whole run (brain 0035 update 1): data-ov has one character per
    // bin of the run ('f' failed, 's' slow, '.' neither), drawn on a canvas under the lit window. Bounded: the number of bins, not of bees.
    let ovCanvas = null, ovKey = '', ovDimL = null, ovDimR = null, ovPin = null, ovGeom = null;
    if (over) {
        ovWin = mk('ov-win'); ovWin.append(mk('ov-grip'), mk('ov-grip r'));
        ovCanvas = mk('ov-marks', 'canvas'); ovDimL = mk('ov-dim'); ovDimR = mk('ov-dim'); ovPin = mk('ov-pin');
        over.append(mk('ov-track'), ovCanvas, ovDimL, ovDimR, ovWin, ovPin);
    }
    const drawOv = () => {
        if (!ovCanvas) return;
        const d = dpr(), w = Math.round(over.clientWidth * d), h = Math.round((ovCanvas.clientHeight || 12) * d), data = el.dataset.ov || '';
        const key = data + '|' + w + '|' + h;
        if (key === ovKey || w <= 0) return;
        ovKey = key;
        ovCanvas.width = w; ovCanvas.height = h;
        const c = ovCanvas.getContext('2d'), cs = getComputedStyle(el), n = data.length;
        c.clearRect(0, 0, w, h);
        if (!n) return;
        const col = { f: cs.getPropertyValue('--failed').trim() || '#e5484d', s: cs.getPropertyValue('--slow').trim() || '#f5a524' };
        for (const kind of ['s', 'f']) {      // red on top of amber
            c.fillStyle = col[kind];
            for (let i = 0; i < n; i++) if (data[i] === kind) { const x0 = Math.floor(i / n * w), x1 = Math.max(x0 + Math.round(1.5 * d), Math.floor((i + 1) / n * w)); c.fillRect(x0, 0, x1 - x0, h); }
        }
    };
    // the view's window on the strip, in strip pixels: at least 16 px (touch 24 px) wide, centred on the view and kept inside the strip; a pin marks the
    // exact spot when the window had to be widened (brain 0040)
    const updateOver = () => {
        if (!over) return;
        drawOv();
        const v = curView(), E = runEnd(), W = over.clientWidth;
        if (!v || E <= 0 || W <= 0) {
            ovGeom = null; over.classList.remove('on');
            return;
        }
        const a = Math.max(0, Math.min(1, v[0] / E)) * W, b = Math.max(0, Math.min(1, v[1] / E)) * W, minW = coarse() ? 24 : 16;
        let l = a, w = Math.max(0, b - a);
        const tiny = w < minW;
        if (tiny) { w = minW; l = Math.max(0, Math.min(W - w, (a + b) / 2 - w / 2)); }
        ovGeom = { l, w, a, b, W };
        over.classList.add('on'); over.classList.toggle('tiny', tiny);
        ovWin.style.transform = `translateX(${l.toFixed(2)}px)`; ovWin.style.width = w.toFixed(2) + 'px';
        ovWin.classList.toggle('grips', w >= 30);
        ovDimL.style.width = Math.max(0, l).toFixed(2) + 'px';
        ovDimR.style.transform = `translateX(${(l + w).toFixed(2)}px)`; ovDimR.style.width = Math.max(0, W - l - w).toFixed(2) + 'px';
        if (tiny) ovPin.style.transform = `translateX(${((a + b) / 2).toFixed(2)}px)`;
    };
    // Even gaps (brain 0037): the strip starts on a whole device pixel (the offset is snapped, which also aligns an unzoomed chart), every bar edge
    // is snapped to a device pixel (cumulative rounding, so the total width stays the same) and each bar keeps the same gap (--gap, whole device
    // pixels) on its right. Browsers round every edge on their own, which made gaps of 1 and 2 device pixels alternate.
    const dpr = () => window.devicePixelRatio || 1;
    const applyOff = () => {
        if (grid) refresh();      // unfolded bursts (brain 0038) make the strip's own layout the truth: it has to be current
        const v = curView(), raw = grid && v ? -xOfTime(v[0], v[1] - v[0]) : 0;
        const d = dpr(), a0 = box === el ? 0 : box.getBoundingClientRect().left - off;      // the strip's page x without our transform
        const o = box === el ? 0 : Math.round((a0 + raw) * d) / d - a0;
        if (Math.abs(o - off) > 1e-4) {
            off = o; box.style.transform = Math.abs(o) > 1e-4 ? `translateX(${o.toFixed(4)}px)` : '';
            if (active || raf) kick();
            else { if (rest.length === items.length && items.length) { const L = restEl(); placeSel(L); placeTags(L); drawAxis(L, -1); } idle(); }      // the caret and the selection pill move with the bars in the same frame
        }
        updateOver();
    };
    const syncGrid = () => {
        readGrid();
        if (!grid) releaseOwn();
        else if (owns && view && !edgeRaf && !strip2 && tmode !== 'pan' && Math.abs(grid.a - view[0]) < 0.03 * (view[1] - view[0])) releaseOwn();
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
            // by slots, so a bar of an unfolded burst counts as the part of the borrowed room it covers (brain 0040)
            const s0 = slotAt.length === L.length ? slotAt[lo] : lo, sw = slotW.length === L.length ? slotW[lo] : 1;
            return grid.buf + (s0 + (x - L[lo].left) / L[lo].width * sw) * grid.bw;
        }
        const v = curView(); return v[0] + x / Wc * (v[1] - v[0]);
    };
    const restEl = () => rest.map(r => ({ left: r.left + off, width: r.width }));

    // ---- hover card: dot, #id, duration, secondary line; tail points at the bar. Built once, filled per hovered bar.
    let card = null, cardKey = '';
    if (tip) {
        card = { dot: mk('dz-dot', 'i'), id: mk('dz-id', 'b'), meta: mk('dz-meta'), sub: mk('dz-sub'), err: mk('dz-err'), counts: mk('dz-counts') };
        card.cnt = ['bad', 'warn', 'good', 'run'].map(c => { const e = mk(c); e.append(mk('', 'i'), mk('', 'b'), mk('', 'em')); card.counts.append(e); return e; });
        card.cnt[0].lastChild.textContent = ' failed'; card.cnt[1].lastChild.textContent = ' slow'; card.cnt[2].lastChild.textContent = ' ok'; card.cnt[3].lastChild.textContent = ' running';
        const row = mk('dz-row'); row.append(card.dot, card.id, card.meta);
        const body = mk('dz-body'); body.append(row, card.sub, card.err, card.counts);
        tip.replaceChildren(body, mk('dz-tail'));
    }
    // ---- neighbour labels (pooled, fixed slots) and axis parts
    const near = [];
    if (strip) for (let j = 0; j < NEAR * 2; j++) { const n = mk('dz-n'); n.append(mk('', 'a'), mk('', 'b')); strip.append(n); near.push(n); }
    let ticks = [], mark = null, readout = null;
    if (axis) { mark = mk('dz-mark'); readout = mk('dz-readout'); axis.append(mark, readout); }
    // the selection's mark and pill (brain 0040), next to the hover ones; the pill takes pointer events (grab, click)
    const selMk = select && axis ? mk('dz-mark sel') : null, selRd = select && axis ? mk('dz-readout sel', 'button') : null;
    if (selRd) { selRd.type = 'button'; selRd.tabIndex = -1; axis.append(selMk, selRd); }
    // ---- selection marker (brain 0037): an accent caret under the selected bar, in the strip below the bars, pointing up at it. It follows the
    // bar's current layout (magnifier, pan) every frame.
    const selEl = select && axis ? mk('dz-sel') : null;
    if (selEl) el.append(selEl);
    let selKey = null, selShown = false, selTimer = 0;
    // ---- burst labels (brain 0038): "burst · N bees" over an unfolded stretch, in the strip so it slides with the bars; width and place follow the
    // current layout (magnifier, pan). A click or tap on it picks the burst's bees as a group (key b<slot>).
    const placeTags = L => {
        if (bursts.size === 0 || L.length !== items.length) return;
        for (const t of box.querySelectorAll(':scope > .burst-tag')) {
            const r = bursts.get(t.dataset.ub);
            if (!r || !L[r[0]] || !L[r[1]]) { t.style.visibility = 'hidden'; continue; }
            const x = L[r[0]].left - off - strBase, w = Math.max(4, L[r[1]].left + L[r[1]].width - L[r[0]].left - gapPx);
            const tr = `translateX(${x.toFixed(2)}px)`;
            if (t.__tr !== tr) { t.__tr = tr; t.style.transform = tr; }
            const ww = w.toFixed(2) + 'px';
            if (t.__w !== ww) { t.__w = ww; t.style.width = ww; }
            if (t.style.visibility) t.style.visibility = '';
        }
    };
    if (select) {
        let tagDown = null;
        el.addEventListener('pointerdown', e => { const t = e.target.closest && e.target.closest('.burst-tag'); if (!t) return; e.stopPropagation(); tagDown = { id: e.pointerId, x: e.clientX, y: e.clientY, ub: t.dataset.ub }; }, true);
        el.addEventListener('pointerup', e => {
            const d = tagDown; if (!d || d.id !== e.pointerId) return; tagDown = null;
            e.stopPropagation();
            if (Math.hypot(e.clientX - d.x, e.clientY - d.y) < 8) pickKey('b' + d.ub);
        }, true);
        el.addEventListener('pointercancel', () => { tagDown = null; }, true);
    }
    const selIndex = () => { for (let i = 0; i < items.length; i++) if (items[i].classList.contains('on')) return i; return -1; };
    const placeSel = L => {
        if (!selEl) return;
        const i = selIndex(), q = i >= 0 ? L[i] : null;
        const cx = q ? q.left + Math.max(1, q.width - gapPx) / 2 : NaN;
        if (!q || !(cx > -1 && cx < el.clientWidth + 1)) { if (selShown) { selShown = false; selEl.classList.remove('show'); } return; }
        const key = items[i].dataset.key, d = dpr(), x = Math.round(cx * d) / d;
        if (key !== selKey && selShown && !reduced()) {      // a new selection: the caret glides to it
            selEl.style.transition = 'transform 200ms ease, opacity 150ms ease';
            if (selTimer) clearTimeout(selTimer);
            selTimer = setTimeout(() => { selTimer = 0; selEl.style.transition = ''; }, 230);
        }
        selKey = key;
        const tr = `translateX(${x.toFixed(3)}px)`;
        if (selEl.__tr !== tr) { selEl.__tr = tr; selEl.style.transform = tr; }
        if (!selShown) { selShown = true; selEl.classList.add('show'); }
    };
    // ---- striped groups (brain 0037): a magnified (or hovered) group bar shows its make-up as vertical stripes. data-st (from Blazor) lists the
    // segments in order failed, slow, ok, running with their share in percent; the stripes are built here only when a group is highlighted.
    const stripes = it => {
        const st = it.dataset.st, g = st ? it.querySelector(':scope > .grp') : null;
        if (!g) return;
        let s = g.querySelector(':scope > .grp-st');
        if (!s) { s = mk('grp-st'); s.setAttribute('aria-hidden', 'true'); g.append(s); }
        if (s.__st === st) return;
        s.__st = st;
        s.replaceChildren(...st.split(' ').map(p => { const [k, v, t] = p.split(':'), e = mk(t ? k + ' t' + t : k, 'i'); e.style.flexGrow = v; return e; }));
    };

    let gapPx = 0;
    const setW = (it, w) => {
        const fl = `0 0 ${w.toFixed(6)}px`;
        if (it.__fl !== fl) { it.__fl = fl; it.style.flex = fl; it.style.minWidth = '0'; it.style.maxWidth = 'none'; }
    };
    // Widths from the start of the strip, snapped so that every edge lands on a device pixel. Each edge is also a multiple of 1/64 px (the
    // browser's layout unit), so the widths are exact and rounding does not add up along several hundred bars.
    const q64 = v => Math.round(v * 64) / 64;
    const snapW = ws => {
        const d = dpr(), out = new Array(ws.length);
        let acc = 0, prev = 0;
        for (let i = 0; i < ws.length; i++) { acc += ws[i]; const e = q64(Math.round(acc * d) / d); out[i] = e - prev; prev = e; }
        return out;
    };
    // one gap for the whole chart, from the resting pitch, in whole device pixels; it stays the same while magnifying and panning
    const gapFor = pitch => {
        const d = dpr(), g = Math.min(3.5, Math.max(0.5, pitch * 0.3));
        let dev = Math.max(1, Math.round(g * d));
        if (pitch * d < 3) dev = pitch * d >= 2 ? 1 : 0;
        return q64(dev / d);
    };
    const settle = () => {      // back to the resting layout (snapped widths stay inline)
        for (let i = 0; i < items.length; i++) {
            const it = items[i];
            if (rest[i]) setW(it, rest[i].width);
            if (it.__dz !== undefined) { it.style.removeProperty('--dz'); it.__dz = undefined; }
            if (it.__mg) { it.classList.remove('mag'); it.__mg = undefined; }
        }
    };
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
        strBase = box === el ? 0 : (parseFloat(getComputedStyle(el).paddingLeft) || 0) + el.clientLeft;
        const n = rest.length;
        if (n && box !== el) {
            const pitch = grid ? Wc * grid.bw / Math.max(1e-9, grid.b - grid.a) : (rest[n - 1].left + rest[n - 1].width - rest[0].left) / n;
            gapPx = gapFor(pitch);
            box.style.setProperty('--gap', gapPx.toFixed(6) + 'px');
            const sw = snapW(rest.map(r => r.width));
            let x = rest[0].left;
            rest = sw.map(w => { const r = { left: x, width: w }; x += w; return r; });
            for (let i = 0; i < n; i++) setW(items[i], rest[i].width);
        }
        buildSlots();
        // a bar cannot be narrower than its own gap (padding, border-box) plus a device pixel: the layout below must not promise less, or the hit
        // test drifts from what is drawn
        minW = box !== el ? gapPx + 1 / dpr() : (() => { const cs = items.length ? getComputedStyle(items[0]) : null; return cs ? (parseFloat(cs.paddingLeft) || 0) + (parseFloat(cs.paddingRight) || 0) : 0; })();
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
    // timeframe of the hovered bar. Ticks follow the magnified layout. For time buckets the axis is linear in time, for single bees it
    // is by bar position (the start time of the bee at that position), a rough guide.
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
        const hidden = [];      // pill extents the ticks keep clear of
        // the selected bar's pill (brain 0040): always there while something is selected; its stem stays on the lilac mark under the bar
        const s = selRd ? selIndex() : -1;
        if (s >= 0 && s < L.length) {
            const q = L[s], text0 = rangeText(s), gap = q.left + 2, w = Math.max(5, q.width - 4 - gapPx), cx = gap + w / 2;
            const side = cx < 0 ? -1 : cx > W ? 1 : 0, text = side < 0 ? '‹ ' + text0 : side > 0 ? text0 + ' ›' : text0;
            if (selRd.__t !== text) { selRd.__t = text; selRd.textContent = text; selRd.__w = text.length * CW + 20; }
            const rw = selRd.__w, rx = Math.max(2, Math.min(W - rw - 2, cx - rw / 2));
            if (selRd.__x !== rx) { selRd.__x = rx; selRd.style.transform = `translateX(${rx.toFixed(2)}px)`; }
            selRd.style.setProperty('--tx', Math.max(8, Math.min(rw - 8, cx - rx)).toFixed(2) + 'px');
            selRd.classList.toggle('off', side !== 0); selRd.classList.toggle('dim', h >= 0 && h !== s);
            selRd.title = side ? 'The selection is outside the view: click to bring it back' : (items[s].dataset.bucket !== undefined ? 'Zoom into this group (drag to move the selection along the bars)' : "Show this bee's calls (drag to move the selection along the bars)");
            selRd.setAttribute('aria-label', (side ? 'Selection outside the view, ' : 'Selected: ') + text0);
            if (!selRd.__on) { selRd.__on = true; selRd.classList.add('show'); selRd.tabIndex = 0; }
            if (side === 0) { selMk.style.opacity = 1; selMk.style.transform = `translateX(${gap.toFixed(2)}px)`; selMk.style.width = w.toFixed(2) + 'px'; }
            else selMk.style.opacity = 0;
            hidden.push([rx, rx + rw]);
        } else if (selRd && selRd.__on) { selRd.__on = false; selRd.classList.remove('show'); selRd.tabIndex = -1; selMk.style.opacity = 0; }
        if (h < 0 || h === s) { mark.style.opacity = 0; readout.style.opacity = 0; }
        else {
            const text = rangeText(h);
            if (readout.__t !== text) { readout.__t = text; readout.textContent = text; readout.__w = text.length * CW + 18; }
            const gap = L[h].left + 2, w = Math.max(5, L[h].width - 4), cx = gap + w / 2, rw = readout.__w;
            const rx = Math.max(2, Math.min(W - rw - 2, cx - rw / 2));
            mark.style.opacity = 1; mark.style.transform = `translateX(${gap}px)`; mark.style.width = w + 'px';
            readout.style.opacity = 1; readout.style.transform = `translateX(${rx}px)`; readout.style.setProperty('--tx', (cx - rx) + 'px');
            // while the hover pill of another bar would cover it, the selection pill steps back (its mark and caret stay)
            if (s >= 0 && hidden.length) { const [p, q] = hidden[0]; selRd.classList.toggle('under', rx < q + 4 && rx + rw > p - 4); }
            hidden.push([rx, rx + rw]);
        }
        if (selRd && (h < 0 || h === s)) selRd.classList.remove('under');
        ticks.forEach(t => { const x = parseFloat(t.style.left); t.style.opacity = hidden.some(([p, q]) => x > p - 34 && x < q + 34) ? 0 : ''; });
    };
    const rangeText = i => {
        const it = items[i].dataset, a = t0(i), b = t1(i), bk = it.bucket !== undefined, dc = bk ? gridDec() : 1;
        return it.run ? fmt(a, dc) + ' → now' : fmt(a, dc) + (bk ? ' – ' : ' → ') + fmt(b, dc);
    };

    // ---- hover card + neighbour labels. Reads (bar rect, card size) happen before the frame's writes.
    const readHover = i => {
        if (!tip || !card) return null;
        const it = i >= 0 ? items[i] : null, d = it && it.dataset;
        if (!it || !d.l1) return null;
        const bar = it.querySelector('.bar-stack, .bar');
        if (!bar) return null;
        const key = [d.l1, d.l2, d.l3, d.err, d.s, d.bad, d.slow, d.ok, d.runn].join('|');
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
            card.err.textContent = d.err || ''; card.err.style.display = d.err ? '' : 'none';
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
        const W = el.clientWidth, sel = selShown ? selIndex() : -1;      // the selected bar's caret sits where its label would be
        let slot = 0;
        for (const dir of [-1, 1]) {
            let edge = h >= 0 ? (dir < 0 ? layout[h].left : layout[h].left + layout[h].width) : 0;
            for (let a = 1; a <= NEAR; a++) {
                const n = near[slot++], j = h + dir * a;
                let shown = false;
                if (h >= 0 && j >= 0 && j < items.length && j !== sel && !items[j].classList.contains('empty')) {
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
                if (j === sel && h >= 0 && layout[j]) { const c = layout[j].left + layout[j].width / 2; edge = dir < 0 ? Math.min(edge, c - 9) : Math.max(edge, c + 9); }
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
        const tw = new Array(n);
        for (let i = 0; i < n; i++) {
            const inr = i >= lo && i <= hi;
            tw[i] = !inr ? rest[i].width : fin[i] === minW ? minW : w[i] * f;
        }
        const sw = box !== el ? snapW(tw) : tw;      // even gaps: every edge on a device pixel (brain 0037)
        let x = rest[0].left;
        layout = new Array(n);
        for (let i = 0; i < n; i++) {
            const wi = sw[i];
            layout[i] = { left: x + off, width: wi };
            const it = items[i];
            const dz = (1 + TALL * cur[i]).toFixed(3);      // only touch what changed (a zoomed chart holds several hundred bars)
            setW(it, wi);
            if (it.__dz !== dz) { it.__dz = dz; it.style.setProperty('--dz', dz); }
            const mg = cur[i] > 0.5; if (it.__mg !== mg) { it.__mg = mg; it.classList.toggle('mag', mg); }   // group badges show on magnified bars (brain 0032)
            if (mg) stripes(it);      // and their make-up as stripes (brain 0037)
            x += wi;
        }
        const h = info ? hov : -1;
        showCard(h, info); showNear(h); drawAxis(layout, h); placeSel(layout); placeTags(layout);
        if (active || moving) raf = requestAnimationFrame(frame);
        else { settle(); layout = []; last = 0; showCard(-1, null); showNear(-1); const L = restEl(); drawAxis(L, -1); placeSel(L); placeTags(L); }
    };
    // static axis at rest, redrawn when bars change (Blazor re-render) or the chart is resized
    let idleRaf = 0;
    const idle = () => { if (idleRaf) return; idleRaf = requestAnimationFrame(() => { idleRaf = 0; if (active || raf) return; refresh(); applyOff(); const L = restEl(); drawAxis(L, -1); placeSel(L); placeTags(L); glide(); }); };
    const attrWatch = new MutationObserver(idle);
    attrWatch.observe(el, { childList: true, attributes: true, attributeFilter: ['data-t0', 'data-t1', 'data-key', 'aria-pressed'], subtree: true });
    // the chart's width goes to Blazor (brain 0040: bursts unfold when their bees fit), once at the start and when it changes by a pixel or more
    let sentW = 0;
    const sendSize = () => { const w = el.clientWidth; if (select && w > 0 && Math.abs(w - sentW) >= 1) { sentW = w; ref.invokeMethodAsync('Size', w); } };
    const sizeWatch = typeof ResizeObserver !== 'undefined' ? new ResizeObserver(() => { axisDirty = true; ovKey = ''; Wc = el.clientWidth; sendSize(); applyOff(); idle(); }) : null;
    const gridWatch = new MutationObserver(syncGrid);     // runs before the next paint: a rebuilt buffer never shows at the wrong offset
    gridWatch.observe(el, { attributes: true, attributeFilter: ['data-grid', 'data-buf', 'data-win', 'data-elapsed', 'data-zoomed', 'data-ov', 'style'] });
    if (box !== el) gridWatch.observe(box, { childList: true });
    syncGrid();
    if (sizeWatch) sizeWatch.observe(el);
    sendSize();
    const scroller = el.parentElement, onScroll = () => applyOff();      // a scrolled chart moves on the page: keep the bars on whole device pixels
    if (scroller) scroller.addEventListener('scroll', onScroll, { passive: true });
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
    // Blazor gets the key of the bar (bee or time bucket), not its index, so a render between pointer-down and the call cannot change the target.
    const pickKey = key => { if (key) { window.__dock.calls++; window.__dock.lastKey = key; ref.invokeMethodAsync('Pick', key); } };
    const pick = e => { const i = hit(rel(e)); pickKey(i >= 0 ? items[i].dataset.key : null); };

    // ---- brush (brain 0023, update 3): a mouse drag of more than 6 px is a brush, less is a click (the bar under the pointer at press time is
    // picked on release). The brush covers the bars it overlaps in the resting layout; Blazor gets the time frame (data-t0 .. data-t1 of those bars).
    // ---- touch (brain 0031 update 2, 0037, made robust by 0040): a tap picks the bar, a second tap close by zooms in 2x. A sideways swipe that starts
    // in the axis strip (from 14 px under the bars) is the magnifier (lifting selects, edge pan while zoomed); a sideways swipe on the bars of a zoomed
    // chart scrolls the timeline. Two fingers pinch the time range or, moved together, scroll it. A sideways swipe never zooms (brain 0040):
    // - the gesture is decided once: a finger that lands after a one-finger gesture has started is ignored (no pinch from a resting thumb);
    // - two fingers zoom only after their distance changed by 15 %; moved together first, they scroll and stay a scroll;
    // - a tap is only a tap if the finger lifted near where it landed, quickly; a swipe whose moves were not delivered still scrolls;
    // - pointers are forgotten wherever they end (also outside the chart), and a new first finger drops any stale one.
    const THRESH = 6, SLOP = 9, DSLOP = 5, MINSPAN = 0.05, DT = 350, DD = 24, TAP_MS = 600, AXIS_GRACE = 14, PINCH_ON = 1.15, PAN2_ON = 24;
    const brushEl = select ? el.querySelector(':scope > .dz-brush') : null;
    let drag = null;
    const pts = new Map();            // touch pointers on the chart: id -> {x, y, ign}; ign: landed after the gesture was decided, not part of it
    let tmode = '';                    // '' | 'wait' | 'scroll' | 'mag' | 'pan' | 'two' (two fingers, undecided) | 'pinch' | 'pan2' | 'done'
    let tkey = null, tdown = null, pinch = null, lastTap = null, tpan = null, two = null;
    const live = () => Array.from(pts.values()).filter(p => !p.ign);
    const spanOf = (a, b) => {
        refresh(); const lo = Math.min(a, b), hi = Math.max(a, b), idx = [];
        rest.forEach((q, i) => { if (q.left + off + q.width > lo && q.left + off < hi) idx.push(i); });
        return idx;
    };
    // the time frame of some bars; a bar of an unfolded burst stands for its grid slot (brain 0038: the burst is one instant)
    const rangeOf = idx => {
        let a = Infinity, b = -Infinity;
        for (const i of idx) {
            const d = items[i].dataset, u = d.ts !== undefined;
            a = Math.min(a, u ? parseFloat(d.ts) : t0(i)); b = Math.max(b, u ? parseFloat(d.te) : t1(i));
        }
        return [a, b];
    };
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
    // moves the view to start at `from` (same span), clamped to the run; the script owns the view until Blazor caught up
    const panTo = (from, span, gap) => {
        const E = runEnd();
        from = Math.max(0, Math.min(from, Math.max(0, E - span)));
        view = [from, from + span]; own(); applyOff();
        queueZoom(from, from + span, false, gap || 120);
    };
    // pinch: spreading the fingers zooms the time range in, pinching zooms out; the time under the fingers' midpoint stays under it.
    // Blazor gets the range live (at most every 90 ms) and once more at the end; it clamps it to the run and follows the live edge.
    const midDist = () => { const [p, q] = live(); return { mx: (p.x + q.x) / 2, d: Math.max(10, Math.hypot(p.x - q.x, p.y - q.y)) }; };
    const pinchStart = () => {
        if (items.length === 0) return false;
        const [a, b] = curRange(), r = el.getBoundingClientRect(), m = midDist();
        pinch = { a, b, d0: m.d, frac0: (m.mx - r.left) / Math.max(1, el.clientWidth) };
        return b > a;
    };
    const pinchMove = () => {
        const r = el.getBoundingClientRect(), m = midDist(), frac = (m.mx - r.left) / Math.max(1, el.clientWidth);
        const span0 = pinch.b - pinch.a, span = Math.max(MINSPAN, span0 * pinch.d0 / m.d);
        const tc = pinch.a + pinch.frac0 * span0, from = tc - frac * span;
        window.__dock.pinchZooms = (window.__dock.pinchZooms || 0) + 1;
        queueZoom(from, from + span);
    };
    // two fingers, undecided: a clear change of their distance is a pinch, moving them together sideways scrolls (and stays a scroll)
    const twoMove = id => {
        if (tmode === 'two') {      // decide only once both fingers reported their move (events come one finger at a time)
            two.seen.add(id);
            if (two.seen.size < 2) return;
            two.seen.clear();
        }
        const m = midDist(), ratio = m.d / two.d0;
        if (tmode === 'two') {
            if (ratio > PINCH_ON || ratio < 1 / PINCH_ON) { tmode = pinchStart() ? 'pinch' : 'done'; return; }      // starts from here: no jump
            if (Math.abs(m.mx - two.mx0) >= PAN2_ON) { tmode = grid && two.v ? 'pan2' : 'done'; window.__dock.pans2 = (window.__dock.pans2 || 0) + 1; }
            else return;
        }
        if (tmode === 'pinch') pinchMove();
        else if (tmode === 'pan2') { const span = two.v[1] - two.v[0]; panTo(two.v[0] - (m.mx - two.mx0) / Math.max(1, Wc) * span, span); }
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
        if (!edge || (tmode !== 'mag' && !scrub)) { edge = 0; return; }
        const v = curView(); if (!v) { edge = 0; return; }
        const dt = edgeLast ? Math.min(0.05, (ts - edgeLast) / 1000) : 0.016; edgeLast = ts;
        const span = v[1] - v[0], speed = Math.sign(edge) * Math.pow(Math.abs(edge), 1.6) * 0.7 * span;      // view widths per second
        panTo(v[0] + speed * dt, span, 200);
        edgeRaf = requestAnimationFrame(edgeStep);
    };
    const edgeCheck = () => {
        let t = 0;
        if (grid && (tmode === 'mag' || scrub)) {
            const Z = Math.max(32, Math.min(80, Wc * 0.1));
            if (px < Z) t = -(Z - px) / Z; else if (px > Wc - Z) t = (px - (Wc - Z)) / Z;
        }
        edge = Math.max(-1, Math.min(1, t));
        if (edge && !edgeRaf) { edgeLast = 0; edgeRaf = requestAnimationFrame(edgeStep); window.__dock.edgePans = (window.__dock.edgePans || 0) + 1; }
    };
    const endTouch = () => {      // all fingers up (or the gesture was taken away)
        edge = 0; pinch = null; two = null;
        flushZoom();
        pts.clear(); tmode = ''; tkey = null; tdown = null; tpan = null; down = false; active = false; kick(); syncAim();
    };
    // Two swipe areas (brain 0037): the strip under the bars (time axis and neighbour labels, the chart's bottom padding) is the magnifier's
    // track; a sideways swipe on the bars scrolls the zoomed timeline. The first 14 px under the bars count as bars (brain 0040): a finger
    // aimed at the short bars often lands just under them.
    const inAxis = y => {
        const r = el.getBoundingClientRect(), pb = parseFloat(getComputedStyle(el).paddingBottom) || 0;
        return pb > 0 && y - r.top - el.clientTop >= el.clientHeight - pb + AXIS_GRACE;
    };
    const panMove = x => {
        const v0 = tpan.v, span = v0[1] - v0[0];
        panTo(v0[0] - (x - tdown.x) / Math.max(1, Wc) * span, span);
    };
    const onTouchDown = e => {
        // a new first finger: any pointer still listed is stale (its end never reached us), forget it (brain 0040)
        if (e.isPrimary && pts.size) { window.__dock.stale = (window.__dock.stale || 0) + 1; endTouch(); }
        const n = live().length, late = !tdown || e.timeStamp - tdown.t > 150;
        if (n >= 1 && !(n === 1 && (tmode === 'wait' || (tmode === 'pan' && !late)))) {
            pts.set(e.pointerId, { x: e.clientX, y: e.clientY, ign: true });      // the gesture is decided: this finger is not part of it
            window.__dock.ignored = (window.__dock.ignored || 0) + 1;
            return;
        }
        pts.set(e.pointerId, { x: e.clientX, y: e.clientY, ign: false });
        down = true;
        if (pts.size === 1) {
            tmode = 'wait'; tkey = null; tdown = { x: e.clientX, y: e.clientY, t: e.timeStamp, axis: inAxis(e.clientY) };
            const i = hit(rel(e)); tkey = i >= 0 ? items[i].dataset.key : null;
            px = rel(e);
        } else {
            // two fingers before a gesture was decided (or within 150 ms of a pan): a pinch or a two-finger scroll, decided by how they move
            edge = 0; active = false; kick(); lastTap = null;
            const m = midDist();
            two = { d0: m.d, mx0: m.mx, v: curView() ? curView().slice() : null, seen: new Set() };
            tmode = 'two';
        }
        syncAim();
    };
    const onTouchMove = e => {
        const p = pts.get(e.pointerId); if (!p) return;
        p.x = e.clientX; p.y = e.clientY;
        if (p.ign) return;
        if (tmode === 'wait') {
            const dx = e.clientX - tdown.x, dy = e.clientY - tdown.y;
            if (Math.hypot(dx, dy) >= SLOP) {
                lastTap = null;
                if (Math.abs(dx) > Math.abs(dy) && tdown.axis) {      // axis strip: magnifier under the finger
                    tmode = 'mag'; px = rel(e); active = true; kick(); edgeCheck();
                    window.__dock.mags = (window.__dock.mags || 0) + 1;
                    try { el.setPointerCapture(e.pointerId); } catch (_) { }
                } else if (Math.abs(dx) > Math.abs(dy) && grid && curView()) {      // bars, zoomed in: scroll the timeline
                    tmode = 'pan'; tpan = { v: curView().slice() }; active = false; kick(); panMove(e.clientX);
                    window.__dock.pans = (window.__dock.pans || 0) + 1;
                    try { el.setPointerCapture(e.pointerId); } catch (_) { }
                } else tmode = 'scroll';      // vertical, or sideways on the bars of an unzoomed chart: the page's own behaviour
            }
        } else if (tmode === 'mag') { px = rel(e); active = true; kick(); edgeCheck(); }
        else if (tmode === 'pan') panMove(e.clientX);
        else if (tmode === 'two' || tmode === 'pinch' || tmode === 'pan2') twoMove(e.pointerId);
    };
    const onTouchUp = e => {
        const p = pts.get(e.pointerId);
        if (!p) return;
        pts.delete(e.pointerId);
        if (p.ign) { if (pts.size === 0) endTouch(); return; }
        const up = e.type === 'pointerup' && !e.__dzLost;
        if (tmode === 'wait' && up) {
            const k = tkey, now = e.timeStamp, x = rel(e), dx = e.clientX - tdown.x, dy = e.clientY - tdown.y;
            const isTap = Math.hypot(dx, dy) < SLOP && now - tdown.t < TAP_MS;
            if (!isTap) {
                // lifted far from where it landed without the moves reaching us (a fast flick): it was a swipe, never a tap (brain 0040)
                lastTap = null;
                if (Math.abs(dx) > Math.abs(dy) && Math.abs(dx) >= SLOP && grid && curView() && !tdown.axis) {
                    tpan = { v: curView().slice() }; panMove(e.clientX);
                    window.__dock.flicks = (window.__dock.flicks || 0) + 1;
                }
                endTouch(); return;
            }
            // a double tap needs two still taps (under 5 px each): two short nudges of a swipe are not one (brain 0040)
            const still = Math.hypot(dx, dy) < DSLOP;
            const dbl = still && lastTap && now - lastTap.t < DT && Math.hypot(e.clientX - lastTap.x, e.clientY - lastTap.y) < DD;
            endTouch();
            if (dbl) { lastTap = null; doubleTap(x); } else { lastTap = still ? { t: now, x: e.clientX, y: e.clientY } : null; pickKey(k); }
            return;
        }
        lastTap = null;
        if (tmode === 'mag' && up) {      // lifting selects the bar under the finger, like a tap
            const i = hit(px), k = i >= 0 ? items[i].dataset.key : null;
            endTouch(); pickKey(k); return;
        }
        if ((tmode === 'two' || tmode === 'pinch' || tmode === 'pan2') && live().length < 2) { flushZoom(); pinch = null; two = null; tmode = 'done'; active = false; kick(); }
        if (pts.size === 0) endTouch();
    };
    // Stop the page from scrolling or zooming while a swipe-magnify, pan or pinch is going on (needs a non-passive listener); a vertical swipe still scrolls.
    const onTouchMoveEv = e => { if (tmode === 'mag' || tmode === 'pan' || tmode === 'pinch' || tmode === 'pan2' || tmode === 'two' || tmode === 'done' || scrub || e.touches.length > 1) { if (e.cancelable) e.preventDefault(); } };
    const onContext = e => { if (tmode) e.preventDefault(); };
    // a touch that ends where the chart does not hear it (outside it, after its target was re-rendered away): forget it all the same (brain 0040)
    const onLostEnd = e => {
        if (e.pointerType !== 'touch' || e.__dzSeen || !pts.has(e.pointerId)) return;
        e.__dzLost = true; window.__dock.lostEnds = (window.__dock.lostEnds || 0) + 1;
        onTouchUp(e);
    };

    // ---- wheel (brain 0040): a sideways wheel or trackpad swipe (or Shift + wheel) scrolls a zoomed chart's timeline and never zooms; a trackpad
    // pinch (the browser sends it as Ctrl + wheel) or Ctrl + wheel zooms the chart around the pointer instead of the page. A plain vertical wheel
    // scrolls the page as always.
    let wz = null;
    const onWheel = e => {
        if (!select) return;
        const unit = e.deltaMode === 1 ? 16 : e.deltaMode === 2 ? Wc : 1;
        if (e.ctrlKey) {
            e.preventDefault();
            const now = performance.now(), base = wz && now - wz.t < 300 ? wz.r : curRange(), E = runEnd();
            if (!(base[1] > base[0]) || E <= 0) return;
            const x = rel(e), f = Math.max(0.5, Math.min(2, Math.exp(e.deltaY * unit * 0.01)));
            const tc = base[0] + x / Math.max(1, Wc) * (base[1] - base[0]);
            const span = Math.max(MINSPAN, Math.min(E, (base[1] - base[0]) * f)), from = Math.max(0, Math.min(tc - x / Math.max(1, Wc) * span, Math.max(0, E - span)));
            wz = { t: now, r: [from, from + span] };
            window.__dock.wheelZooms = (window.__dock.wheelZooms || 0) + 1;
            queueZoom(from, from + span);
            return;
        }
        const v = curView();
        const dx = (e.shiftKey && !e.deltaX ? e.deltaY : e.deltaX) * unit, dy = e.shiftKey ? 0 : e.deltaY * unit;
        if (!v || Math.abs(dx) <= Math.abs(dy)) return;      // unzoomed, or mostly vertical: the page scrolls
        e.preventDefault();
        const span = v[1] - v[0];
        window.__dock.wheelPans = (window.__dock.wheelPans || 0) + 1;
        panTo(v[0] + dx / Math.max(1, Wc) * span, span);
    };

    // ---- overview strip (brain 0035, redesigned in 0040): the whole run, muted failed/slow marks, and the view as an outlined window with the run
    // outside it dimmed. The window is at least 16 px (touch 24 px) wide, so a tiny view on a long run stays visible and grabbable; a pin then marks the
    // exact spot. Drag the window (or anywhere on the strip) to move the view, tap the strip to centre the view there, arrow keys move it too. Unzoomed,
    // a tap zooms into an eighth of the run around that spot (drill-down, 0029). The strip never changes the zoom by dragging.
    let sd = null;
    const coarse = () => matchMedia('(pointer: coarse)').matches;
    if (over) {
        const atX = (x, W) => x / Math.max(1, W) * runEnd();
        const moveTo = (cx, W) => {      // centre the view at strip x cx
            const v = curView(); if (!v) return;
            const span = v[1] - v[0];
            panTo(atX(cx, W) - span / 2, span);
        };
        const zoomAt = (x, W) => {
            const E = runEnd(); if (E <= 0) return;
            const span = E / 8, from = Math.max(0, Math.min(atX(x, W) - span / 2, E - span));
            window.__dock.stripZooms = (window.__dock.stripZooms || 0) + 1;
            queueZoom(from, from + span, true);
        };
        over.addEventListener('pointerdown', e => {
            e.stopPropagation();
            if (runEnd() <= 0 || (e.pointerType === 'mouse' && e.button !== 0)) return;
            const r = over.getBoundingClientRect(), x = e.clientX - r.left, g = ovGeom, v = curView();
            const touch = e.pointerType === 'touch', slop = touch ? 14 : 6;
            const onWin = !!(v && g && x >= g.l - slop && x <= g.l + g.w + slop);
            sd = { id: e.pointerId, x0: e.clientX, y0: e.clientY, t0: e.timeStamp, r, touch, onWin, drag: false, dx: onWin ? x - (g.l + g.w / 2) : 0 };
            active = false; kick();
            if (!touch) {
                e.preventDefault();
                if (v) { sd.drag = true; try { over.setPointerCapture(e.pointerId); } catch (_) { } down = true; syncAim(); over.classList.add('drag'); if (!onWin) moveTo(x, r.width); }
            }
            window.__dock.stripDowns = (window.__dock.stripDowns || 0) + 1;
        });
        over.addEventListener('pointermove', e => {
            e.stopPropagation();
            if (!sd || sd.id !== e.pointerId) return;
            const dx = e.clientX - sd.x0, dy = e.clientY - sd.y0;
            if (!sd.drag) {
                if (!sd.touch || Math.hypot(dx, dy) < SLOP) return;
                if (Math.abs(dy) > Math.abs(dx) || !curView()) { sd = null; return; }      // vertical: the page scrolls; unzoomed: nothing to drag
                sd.drag = true; down = true; syncAim(); over.classList.add('drag');
                try { over.setPointerCapture(e.pointerId); } catch (_) { }
                window.__dock.stripDrags = (window.__dock.stripDrags || 0) + 1;
            }
            moveTo(e.clientX - sd.r.left - sd.dx, sd.r.width);
        });
        const endStrip = e => {
            e.stopPropagation();
            const d = sd; if (!d || d.id !== e.pointerId) return;
            sd = null; over.classList.remove('drag');
            if (d.drag) { flushZoom(); down = false; syncAim(); return; }
            if (e.type !== 'pointerup' || Math.hypot(e.clientX - d.x0, e.clientY - d.y0) >= SLOP || e.timeStamp - d.t0 > TAP_MS) return;
            const x = e.clientX - d.r.left;
            if (!curView()) zoomAt(x, d.r.width);      // unzoomed: drill into that spot
            else if (!d.onWin) { moveTo(x, d.r.width); flushZoom(); }
        };
        over.addEventListener('pointerup', endStrip); over.addEventListener('pointercancel', endStrip);
        over.addEventListener('pointerenter', e => { e.stopPropagation(); active = false; hover = false; kick(); syncAim(); });
        over.addEventListener('click', e => e.stopPropagation());
        over.addEventListener('keydown', e => {
            const v = curView(); if (!v) return;
            const span = v[1] - v[0], E = runEnd();
            let from;
            switch (e.key) {
                case 'ArrowLeft': case 'ArrowDown': from = v[0] - span / 4; break;
                case 'ArrowRight': case 'ArrowUp': from = v[0] + span / 4; break;
                case 'PageUp': from = v[0] - span; break;
                case 'PageDown': from = v[0] + span; break;
                case 'Home': from = 0; break;
                case 'End': from = E - span; break;
                default: return;
            }
            e.preventDefault(); e.stopPropagation();
            window.__dock.stripKeys = (window.__dock.stripKeys || 0) + 1;
            panTo(from, span);
        });
    }

    // ---- the selection pill (brain 0040): the selected bar's time frame stays under the axis, its stem on the lilac mark under the bar, until the
    // selection is cleared. It is grabbable: drag it sideways to move the selection along the bars (the magnifier follows, lifting selects the bar
    // under it, edge pan while zoomed); a click or tap drills down (a group: zoom into its time frame; a bee: its calls). When the selected bar is
    // outside the view the pill waits at that edge with an arrow; a click or tap brings the selection back into view.
    let scrub = null;
    if (select && selRd) {
        const onPill = e => e.target.closest && e.target.closest('.dz-readout.sel');
        el.addEventListener('pointerdown', e => {
            if (!onPill(e) || (e.pointerType === 'mouse' && e.button !== 0)) return;
            e.stopPropagation(); e.preventDefault();
            scrub = { id: e.pointerId, x0: e.clientX, y0: e.clientY, t0: e.timeStamp, moving: false };
            try { el.setPointerCapture(e.pointerId); } catch (_) { }
            down = true; syncAim();
        }, true);
        el.addEventListener('pointermove', e => {
            if (!scrub || scrub.id !== e.pointerId) return;
            e.stopPropagation();
            if (!scrub.moving && Math.abs(e.clientX - scrub.x0) < SLOP) return;
            if (!scrub.moving) { scrub.moving = true; window.__dock.pillDrags = (window.__dock.pillDrags || 0) + 1; }
            px = rel(e); active = true; kick(); edgeCheck();
        }, true);
        const endScrub = e => {
            if (!scrub || scrub.id !== e.pointerId) return;
            e.stopPropagation();
            const s = scrub; scrub = null; edge = 0; down = false; active = false; kick(); syncAim();
            try { el.releasePointerCapture(e.pointerId); } catch (_) { }
            if (e.type !== 'pointerup') return;
            if (s.moving) { const i = hit(px); pickKey(i >= 0 ? items[i].dataset.key : null); flushZoom(); return; }
            if (Math.hypot(e.clientX - s.x0, e.clientY - s.y0) < SLOP && e.timeStamp - s.t0 < TAP_MS) pillClick();
        };
        el.addEventListener('pointerup', endScrub, true); el.addEventListener('pointercancel', endScrub, true);
        selRd.addEventListener('keydown', e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); e.stopPropagation(); pillClick(); } });
    }
    function pillClick() {
        const i = selIndex(); if (i < 0) return;
        window.__dock.pillClicks = (window.__dock.pillClicks || 0) + 1;
        const v = curView(), q = (layout.length === items.length ? layout : restEl())[i];
        if (v && q && (q.left + q.width / 2 < 0 || q.left + q.width / 2 > Wc)) {      // out of view (as the pill shows it): bring it back, centred
            const d = items[i].dataset, a = d.ts !== undefined ? parseFloat(d.ts) : t0(i), span = v[1] - v[0];
            panTo(a - span / 2, span); flushZoom(); return;
        }
        if (items[i].dataset.bucket !== undefined) {      // a group: zoom into its time frame
            const [a, b] = rangeOf([i]);
            if (b > a) { window.__dock.brushes = (window.__dock.brushes || 0) + 1; ref.invokeMethodAsync('Brush', a, b, false); }
            return;
        }
        const panel = document.querySelector('.bee-panel');      // a bee: its calls
        if (panel) { panel.scrollIntoView({ block: 'nearest', behavior: reduced() ? 'auto' : 'smooth' }); panel.classList.add('flash'); setTimeout(() => panel.classList.remove('flash'), 700); }
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
        // over the selection pill the magnifier rests, so the pill stays where it is and can be grabbed or clicked
        if (e.target.closest && e.target.closest('.dz-readout.sel')) { if (active) { active = false; kick(); } return; }
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
        e.__dzSeen = true;
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
    el.__release = () => { if (aimTimer) { clearTimeout(aimTimer); aimTimer = 0; } if (notified) { notified = false; snapshot(); } hover = down = focused = false; pts.clear(); tmode = ''; two = null; pinch = null; scrub = null; };
    el.addEventListener('pointerdown', onDown);
    el.addEventListener('pointerenter', onEnter);
    el.addEventListener('pointermove', onPointerMove);
    el.addEventListener('pointerleave', onLeave);
    el.addEventListener('pointerup', onUp);
    el.addEventListener('pointercancel', onUp);
    el.addEventListener('click', onClick, true);
    el.addEventListener('touchmove', onTouchMoveEv, { passive: false });
    el.addEventListener('wheel', onWheel, { passive: false });
    window.addEventListener('pointerup', onLostEnd); window.addEventListener('pointercancel', onLostEnd);
    el.addEventListener('contextmenu', onContext);
    el.addEventListener('focusin', onFocusIn);
    el.addEventListener('focusout', onFocusOut);
    el.__dock = () => {
        el.removeEventListener('pointerdown', onDown); el.removeEventListener('pointermove', onPointerMove);
        el.removeEventListener('pointerleave', onLeave); el.removeEventListener('pointerup', onUp);
        el.removeEventListener('pointercancel', onUp); el.removeEventListener('click', onClick, true); el.removeEventListener('touchmove', onTouchMoveEv); el.removeEventListener('wheel', onWheel); window.removeEventListener('pointerup', onLostEnd); window.removeEventListener('pointercancel', onLostEnd); el.removeEventListener('contextmenu', onContext); if (zoomTimer) clearTimeout(zoomTimer);
        el.removeEventListener('pointerenter', onEnter); el.removeEventListener('focusin', onFocusIn); el.removeEventListener('focusout', onFocusOut);
        if (aimTimer) clearTimeout(aimTimer);
        if (raf) cancelAnimationFrame(raf);
        clear(); attrWatch.disconnect(); gridWatch.disconnect(); if (edgeRaf) cancelAnimationFrame(edgeRaf); if (ownTimer) clearTimeout(ownTimer); box.style.transform = ''; if (sizeWatch) sizeWatch.disconnect();
        if (scroller) scroller.removeEventListener('scroll', onScroll); if (selEl) selEl.remove(); if (selTimer) clearTimeout(selTimer);
        if (idleRaf) cancelAnimationFrame(idleRaf);
        if (tip) tip.classList.remove('show');
        tracked.delete(el); watch.disconnect();
    };
}

export function release(el) { if (el && el.__release) el.__release(); }

export function detach(el) { if (el && el.__dock) { el.__dock(); el.__dock = null; } }
