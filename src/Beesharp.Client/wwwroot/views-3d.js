// Zero-dependency Canvas 2D isometric/perspective 3D renderer for Beesharp /3d views.
// Ported from docs/demos/3d-views-prototype.html with ResizeObserver, pointer orbit, wheel zoom,
// camera presets, lane isolation, hover tooltip, and click-to-select callback into Blazor.

const CAM_PRESETS = {
  iso:   { yaw: -0.62, pitch: 0.56, zoom: 1.0 },
  top:   { yaw: 0.0,   pitch: 1.54, zoom: 1.08 },
  front: { yaw: 0.0,   pitch: 0.08, zoom: 1.05 },
  side:  { yaw: -1.52, pitch: 0.14, zoom: 1.05 },
};

function rgb(c, alpha = 1) {
  return `rgba(${c[0]},${c[1]},${c[2]},${alpha})`;
}

function shade(c, factor) {
  return [
    Math.min(255, Math.max(0, Math.round(c[0] * factor))),
    Math.min(255, Math.max(0, Math.round(c[1] * factor))),
    Math.min(255, Math.max(0, Math.round(c[2] * factor))),
  ];
}

function esc(s) {
  if (s == null) return '';
  return String(s)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

const LABEL_PRI = {
  SELECTED_HOVERED: 1,
  ALERT: 2,
  AXIS_TITLE: 3,
  LANE: 4,
  TICK: 5,
  CELL_BADGE: 6,
};

function truncateText(str, maxChars) {
  if (str == null) return '';
  const s = String(str);
  if (s.length <= maxChars) return s;
  return maxChars <= 1 ? s.slice(0, 1) : s.slice(0, maxChars - 1).trimEnd() + '…';
}

function parseFormattedMs(str) {
  if (!str) return null;
  const s = String(str).trim();
  const msMatch = s.match(/([\d,.]+)\s*ms\b/i);
  if (msMatch) return parseFloat(msMatch[1].replace(/,/g, ''));
  const sMatch = s.match(/([\d,.]+)\s*s\b/i);
  if (sMatch) return parseFloat(sMatch[1].replace(/,/g, '')) * 1000;
  const hmsMatch = s.match(/\b(\d+):(\d{2}):(\d{2})\b/);
  if (hmsMatch) return ((+hmsMatch[1]) * 3600 + (+hmsMatch[2]) * 60 + (+hmsMatch[3])) * 1000;
  const msTimeMatch = s.match(/\b(\d+):(\d{2})\b/);
  if (msTimeMatch) return ((+msTimeMatch[1]) * 60 + (+msTimeMatch[2])) * 1000;
  return null;
}

function parseFormattedNum(str) {
  if (!str) return null;
  const m = String(str).match(/([\d,]+(?:\.\d+)?)/);
  if (!m) return null;
  const v = parseFloat(m[1].replace(/,/g, ''));
  return Number.isFinite(v) ? v : null;
}

function formatTickMs(ms) {
  if (ms <= 0.5) return '0';
  if (ms < 1000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) {
    const sec = ms / 1000;
    return `${sec >= 10 ? Math.round(sec) : sec.toFixed(1)} s`;
  }
  const totalSec = Math.round(ms / 1000);
  const m = Math.floor(totalSec / 60);
  const s = totalSec % 60;
  return `${m}:${String(s).padStart(2, '0')}`;
}

function formatTickCount(val) {
  if (val <= 0.5) return '0';
  if (val >= 1000) {
    const k = val / 1000;
    return Number.isInteger(k) ? `${k}k` : `${k.toFixed(1)}k`;
  }
  return Math.round(val).toLocaleString('en-US');
}

function niceStep(range, targetSteps, integerOnly) {
  if (!(range > 0)) return 1;
  const rough = range / Math.max(1, targetSteps);
  const pow = Math.pow(10, Math.floor(Math.log10(rough)));
  const frac = rough / pow;
  let nice = 1;
  if (frac <= 1.2) nice = 1;
  else if (frac <= 2.3) nice = 2;
  else if (frac <= 5.5) nice = 5;
  else nice = 10;
  const step = nice * pow;
  return integerOnly ? Math.max(1, Math.round(step)) : step;
}

export function mount(canvas, tooltip, dotnet) {
  if (!canvas) return null;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;

  let width = 0;
  let height = 0;
  let dpr = 1;
  let dprX = 1;
  let dprY = 1;
  let scene = null;
  let isolatedLane = null;
  let selected = null; // { laneIndex, bucketIndex }
  let hovered = null;  // { laneIndex, bucketIndex }
  let showJumpArcs = true;
  let swarmTime = 0;
  let lastFrameTs = performance.now();
  let hitTargets = [];
  let placedBoxes = [];
  let labelItems = [];
  let dirty = true;
  let rafId = 0;
  let disposed = false;

  const cam = {
    yaw: CAM_PRESETS.iso.yaw,
    pitch: CAM_PRESETS.iso.pitch,
    zoom: CAM_PRESETS.iso.zoom,
    targetYaw: CAM_PRESETS.iso.yaw,
    targetPitch: CAM_PRESETS.iso.pitch,
    targetZoom: CAM_PRESETS.iso.zoom,
  };

  function resize() {
    if (disposed) return;
    const host = canvas.parentElement;
    if (!host) return;
    const rect = host.getBoundingClientRect();
    if (rect.width < 1 || rect.height < 1) return;
    dpr = Math.min(window.devicePixelRatio || 1, 2);
    width = Math.max(1, Math.min(4096, rect.width));
    height = Math.max(1, Math.min(4096, rect.height));
    const pw = Math.min(4096, Math.max(1, Math.round(width * dpr)));
    const ph = Math.min(4096, Math.max(1, Math.round(height * dpr)));
    if (canvas.width !== pw || canvas.height !== ph) {
      canvas.width = pw;
      canvas.height = ph;
    }
    dprX = pw / width;
    dprY = ph / height;
    ctx.setTransform(dprX, 0, 0, dprY, 0, 0);
    dirty = true;
    scheduleFrame();
  }

  let ro = null;
  if (typeof ResizeObserver !== 'undefined' && canvas.parentElement) {
    ro = new ResizeObserver(() => resize());
    ro.observe(canvas.parentElement);
  }
  window.addEventListener('resize', resize);
  resize();

  function project(x, y, z) {
    const cy = Math.cos(cam.yaw), sy = Math.sin(cam.yaw);
    const cp = Math.cos(cam.pitch), sp = Math.sin(cam.pitch);

    // Rotate around Y (yaw)
    const x1 = x * cy - z * sy;
    const z1 = x * sy + z * cy;
    // Rotate around X (pitch > 0 looks down from above; +z2 is away from camera)
    const y2 = y * cp + z1 * sp;
    const z2 = z1 * cp - y * sp;

    const fov = 44;
    const dist = 34;
    const scale = (fov / Math.max(6, dist + z2)) * Math.min(width, height) * 0.025 * cam.zoom;
    return {
      sx: width * 0.5 + x1 * scale,
      sy: height * 0.54 - y2 * scale,
      depth: z2,
      scale,
    };
  }

  // ---------- 2D Screen-Space Collision Avoidance ----------
  function quadOfBox(b) {
    return b.quad || [
      { x: b.minX, y: b.minY },
      { x: b.maxX, y: b.minY },
      { x: b.maxX, y: b.maxY },
      { x: b.minX, y: b.maxY },
    ];
  }

  function quadsOverlapSat(qA, qB, pad) {
    const quads = [qA, qB];
    for (let qi = 0; qi < 2; qi++) {
      const q = quads[qi];
      for (let i = 0; i < 2; i++) {
        const p0 = q[i];
        const p1 = q[i + 1];
        const ex = p1.x - p0.x;
        const ey = p1.y - p0.y;
        const len = Math.hypot(ex, ey);
        if (len < 1e-6) continue;
        const nx = -ey / len;
        const ny = ex / len;

        let minA = Infinity, maxA = -Infinity;
        for (let k = 0; k < 4; k++) {
          const d = qA[k].x * nx + qA[k].y * ny;
          if (d < minA) minA = d;
          if (d > maxA) maxA = d;
        }
        let minB = Infinity, maxB = -Infinity;
        for (let k = 0; k < 4; k++) {
          const d = qB[k].x * nx + qB[k].y * ny;
          if (d < minB) minB = d;
          if (d > maxB) maxB = d;
        }
        if (maxA + pad <= minB || maxB + pad <= minA) return false;
      }
    }
    return true;
  }

  function boxesOverlap(a, b, pad) {
    if (a.maxX + pad <= b.minX || b.maxX + pad <= a.minX || a.maxY + pad <= b.minY || b.maxY + pad <= a.minY) {
      return false;
    }
    if (!a.quad && !b.quad) return true;
    return quadsOverlapSat(quadOfBox(a), quadOfBox(b), pad);
  }

  function tryPlaceBox(x, y, w, h, pad = 6, quad = null) {
    if (!Number.isFinite(x) || !Number.isFinite(y) || w <= 0 || h <= 0) return false;
    if (x < -4 || x + w > width + 4 || y < 2 || y + h > height - 2) return false;

    const candidate = { minX: x, minY: y, maxX: x + w, maxY: y + h, pad, quad };
    for (let i = 0; i < placedBoxes.length; i++) {
      const existing = placedBoxes[i];
      const gap = Math.max(pad, existing.pad ?? 6);
      if (boxesOverlap(candidate, existing, gap)) return false;
    }
    placedBoxes.push(candidate);
    return true;
  }

  // ---------- Label Helpers (Floor-projected, Halo, Pill) ----------
  function drawHaloText(text, x, y, fillStyle, align = 'center', baseline = 'middle', strokeStyle = 'rgba(15, 14, 13, 0.9)', lineWidth = 3.2) {
    ctx.textAlign = align;
    ctx.textBaseline = baseline;
    ctx.lineJoin = 'round';
    ctx.miterLimit = 2;
    ctx.strokeStyle = strokeStyle;
    ctx.lineWidth = lineWidth;
    ctx.strokeText(text, x, y);
    ctx.fillStyle = fillStyle;
    ctx.fillText(text, x, y);
  }

  function roundRectPath(x, y, w, h, r) {
    const rad = Math.min(r, w / 2, h / 2);
    ctx.beginPath();
    if (typeof ctx.roundRect === 'function') {
      ctx.roundRect(x, y, w, h, rad);
    } else {
      ctx.moveTo(x + rad, y);
      ctx.lineTo(x + w - rad, y);
      ctx.quadraticCurveTo(x + w, y, x + w, y + rad);
      ctx.lineTo(x + w, y + h - rad);
      ctx.quadraticCurveTo(x + w, y + h, x + w - rad, y + h);
      ctx.lineTo(x + rad, y + h);
      ctx.quadraticCurveTo(x, y + h, x, y + h - rad);
      ctx.lineTo(x, y + rad);
      ctx.quadraticCurveTo(x, y, x + rad, y);
      ctx.closePath();
    }
  }

  function computeFloorTransform(x, z, ux, uz, vx, vz, outX, outZ, alignMode) {
    const p0 = project(x, 0, z);
    if (p0.depth < -26) return null;
    const step = 0.5;
    const pu = project(x + ux * step, 0, z + uz * step);
    const pv = project(x + vx * step, 0, z + vz * step);
    const inv = 1 / (step * Math.max(4, p0.scale));

    let e1 = { x: (pu.sx - p0.sx) * inv, y: (pu.sy - p0.sy) * inv };
    let e2 = { x: (pv.sx - p0.sx) * inv, y: (pv.sy - p0.sy) * inv };

    // Flip reading direction automatically when projected U points left (or purely downward)
    if (e1.x < -0.02 || (Math.abs(e1.x) <= 0.02 && e1.y > 0)) {
      e1 = { x: -e1.x, y: -e1.y };
    }

    // Ensure positive determinant so glyphs are never mirrored/upside-down
    let det = e1.x * e2.y - e1.y * e2.x;
    if (det < 0) {
      e2 = { x: -e2.x, y: -e2.y };
      det = -det;
    }

    const len1 = Math.hypot(e1.x, e1.y);
    const len2 = Math.hypot(e2.x, e2.y);
    if (len1 < 0.24 || len2 < 0.20 || det < 0.18) return null;

    let align = alignMode || 'center';
    if (alignMode === 'outward') {
      const pn = project(x + outX * step, 0, z + outZ * step);
      const outSx = pn.sx - p0.sx;
      const outSy = pn.sy - p0.sy;
      const dot = outSx * e1.x + outSy * e1.y;
      align = dot >= 0 ? 'left' : 'right';
    }

    return { p0, e1, e2, align, len1, len2 };
  }

  function queueFloorLabel(text, x, z, ux, uz, vx, vz, opts) {
    if (!text) return;
    labelItems.push({
      priority: opts.priority,
      order: opts.order ?? 0,
      draw() {
        const ft = computeFloorTransform(x, z, ux, uz, vx, vz, opts.outX ?? 0, opts.outZ ?? 0, opts.align ?? 'center');
        if (!ft) return;

        const fontSize = opts.size ?? 11;
        const weight = opts.weight ?? 500;
        const font = `${weight} ${fontSize}px Inter, system-ui, sans-serif`;
        ctx.font = font;
        const textW = ctx.measureText(text).width;
        const textH = fontSize * 1.15;

        let lx0, lx1;
        if (ft.align === 'left') { lx0 = 0; lx1 = textW; }
        else if (ft.align === 'right') { lx0 = -textW; lx1 = 0; }
        else { lx0 = -textW / 2; lx1 = textW / 2; }
        const ly0 = -textH / 2;
        const ly1 = textH / 2;

        const corners = [
          { x: lx0, y: ly0 },
          { x: lx1, y: ly0 },
          { x: lx1, y: ly1 },
          { x: lx0, y: ly1 },
        ].map(c => ({
          x: ft.p0.sx + c.x * ft.e1.x + c.y * ft.e2.x,
          y: ft.p0.sy + c.x * ft.e1.y + c.y * ft.e2.y,
        }));

        const minX = Math.min(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        const maxX = Math.max(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        const minY = Math.min(corners[0].y, corners[1].y, corners[2].y, corners[3].y);
        const maxY = Math.max(corners[0].y, corners[1].y, corners[2].y, corners[3].y);

        if (!tryPlaceBox(minX, minY, maxX - minX, maxY - minY, opts.pad ?? 3.5, corners)) return;

        ctx.save();
        ctx.setTransform(dprX, 0, 0, dprY, 0, 0);
        ctx.transform(ft.e1.x, ft.e1.y, ft.e2.x, ft.e2.y, ft.p0.sx, ft.p0.sy);
        ctx.font = font;
        drawHaloText(
          text,
          0,
          0,
          opts.color ?? '#a19c94',
          ft.align,
          'middle',
          opts.strokeColor ?? 'rgba(15, 14, 13, 0.85)',
          2.6
        );
        ctx.restore();
      },
    });
  }

  function queuePillBadge(text, sx, sy, opts) {
    if (!text) return;
    labelItems.push({
      priority: opts.priority,
      order: opts.order ?? 0,
      draw() {
        const fontSize = opts.size ?? 10.5;
        const weight = opts.weight ?? 600;
        const font = `${weight} ${fontSize}px Inter, system-ui, sans-serif`;
        ctx.font = font;
        const textW = ctx.measureText(text).width;
        const padX = opts.padX ?? 6.5;
        const padY = opts.padY ?? 3;
        const w = textW + padX * 2;
        const h = fontSize + padY * 2;
        const bx = sx - w / 2;
        const by = sy - h / 2;

        if (!tryPlaceBox(bx, by, w, h, opts.pad ?? 6)) return;

        ctx.save();
        roundRectPath(bx, by, w, h, 5.5);
        ctx.fillStyle = opts.bg ?? 'rgba(15, 14, 13, 0.86)';
        ctx.fill();
        ctx.strokeStyle = opts.border ?? 'rgba(255, 255, 255, 0.18)';
        ctx.lineWidth = 1;
        ctx.stroke();

        ctx.font = font;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        ctx.fillStyle = opts.color ?? '#ffffff';
        ctx.fillText(text, sx, sy + 0.5);
        ctx.restore();
      },
    });
  }

  function queueHaloBadge(text, sx, sy, opts) {
    if (!text) return;
    labelItems.push({
      priority: opts.priority,
      order: opts.order ?? 0,
      draw() {
        const fontSize = opts.size ?? 10;
        const weight = opts.weight ?? 500;
        const font = `${weight} ${fontSize}px Inter, system-ui, sans-serif`;
        ctx.font = font;
        const textW = ctx.measureText(text).width;
        const textH = fontSize * 1.15;
        const align = opts.align ?? 'center';
        const bx = align === 'left' ? sx : align === 'right' ? sx - textW : sx - textW / 2;
        const by = sy - textH / 2;

        if (!tryPlaceBox(bx, by, textW, textH, opts.pad ?? 6)) return;

        ctx.save();
        ctx.font = font;
        drawHaloText(text, sx, sy, opts.color ?? '#e5e2dc', align, 'middle', 'rgba(15, 14, 13, 0.9)', 3.2);
        ctx.restore();
      },
    });
  }

  function flushLabels() {
    labelItems.sort((a, b) => (a.priority - b.priority) || ((a.order ?? 0) - (b.order ?? 0)));
    for (let i = 0; i < labelItems.length; i++) {
      labelItems[i].draw();
    }
  }

  // ---------- Focus & Axis Helpers ----------
  function isLaneSoftDim(laneIdx) {
    if (isolatedLane !== null) return false;
    if (!selected && !hovered) return false;
    if (selected && selected.laneIndex === laneIdx) return false;
    if (hovered && hovered.laneIndex === laneIdx) return false;
    return true;
  }

  function getFloorEdges(xSpan, zSpan) {
    const pZNeg = project(0, 0, -zSpan / 2);
    const pZPos = project(0, 0, zSpan / 2);
    const zEdgeSign = pZNeg.depth <= pZPos.depth ? -1 : 1;

    const pXNeg = project(-xSpan / 2, 0, 0);
    const pXPos = project(xSpan / 2, 0, 0);
    let xEdgeSign;
    if (Math.abs(pXNeg.depth - pXPos.depth) < 0.25) {
      xEdgeSign = pXNeg.sx <= pXPos.sx ? -1 : 1;
    } else {
      xEdgeSign = pXPos.depth < pXNeg.depth ? 1 : -1;
    }
    return { xEdgeSign, zEdgeSign };
  }

  function inferYScale(scn) {
    if (!scn || !scn.cells || scn.cells.length === 0) return null;
    const kind = scn.kind;

    if (kind === 'swarm') {
      let best = null;
      for (const c of scn.cells) {
        if (c.isEmptyPad || !(c.height > 0.25) || !c.tooltipRows) continue;
        if (!best || c.height > best.height) best = c;
      }
      if (!best) return null;
      const actCount = parseFormattedNum(best.tooltipRows[0]?.value);
      const failCount = parseFormattedNum(best.tooltipRows[1]?.value);
      let slope = 5.6;
      let val = actCount;
      if (!(actCount > 0) && failCount > 0 && !(best.secondaryHeight > 0)) {
        slope = 4.4;
        val = failCount;
      }
      if (!(val > 0)) return null;
      const scaleVal = val * slope / Math.max(0.01, best.height - 0.24);
      return {
        unit: 'count',
        valAt: (y) => Math.max(0, (Math.max(0, y - 0.24) / slope) * scaleVal),
        yAt: (v) => v <= 0 ? 0 : 0.24 + (v / scaleVal) * slope,
      };
    }

    const base = kind === 'surface' ? 0.22 : kind === 'errors' ? 0.24 : 0.28;
    const slope = 6.8;
    let best = null;
    for (const c of scn.cells) {
      if (c.isEmptyPad || !(c.height > base + 0.015)) continue;
      if (!best || (c.height < 7.16 && (best.height >= 7.16 || c.height > best.height))) {
        best = c;
      }
    }
    if (!best) return null;

    let val = null;
    let unit = 'ms';
    if (kind === 'terrain') {
      const r0 = best.tooltipRows?.[0];
      if (r0?.label === 'Avg duration') val = parseFormattedMs(r0.value);
      else val = parseFormattedMs(best.tooltipRows?.[1]?.value);
    } else if (kind === 'history') {
      const txt = best.tooltipRows?.[0]?.value || '';
      const part = txt.includes('longest ') ? txt.split('longest ').pop() : txt.split('·').pop();
      val = parseFormattedMs(part);
    } else if (kind === 'calls') {
      val = parseFormattedMs(best.badgeText);
    } else if (kind === 'surface') {
      unit = 'count';
      val = parseFormattedNum(best.tooltipRows?.[0]?.value);
    } else if (kind === 'errors') {
      unit = 'count';
      const txt = best.tooltipRows?.[0]?.value || '';
      val = parseFormattedNum(txt.split(/[\s(/]/)[0]);
    }

    if (!(val > 0)) return null;
    const scaleVal = val * slope / Math.max(0.01, best.height - base);
    return {
      unit,
      valAt: (y) => Math.max(0, (Math.max(0, y - base) / slope) * scaleVal),
      yAt: (v) => v <= 0 ? 0 : base + (v / scaleVal) * slope,
    };
  }

  function renderYAxis(xSpan, zSpan, maxCellTop, yScale, yTitle) {
    const corners = [
      { x: -xSpan / 2, z: -zSpan / 2 },
      { x: xSpan / 2, z: -zSpan / 2 },
      { x: xSpan / 2, z: zSpan / 2 },
      { x: -xSpan / 2, z: zSpan / 2 },
    ].map(c => ({ ...c, p: project(c.x, 0, c.z) }));

    corners.sort((a, b) => (a.p.sx - b.p.sx) || (a.p.depth - b.p.depth));
    const anchor = corners[0];

    const yTopWorld = Math.max(1.8, maxCellTop);
    const pBot = project(anchor.x, 0, anchor.z);
    const pTop = project(anchor.x, yTopWorld, anchor.z);
    const axisPxLen = Math.hypot(pTop.sx - pBot.sx, pTop.sy - pBot.sy);
    if (axisPxLen < 32) return;

    ctx.save();
    ctx.strokeStyle = 'rgba(255, 255, 255, 0.16)';
    ctx.lineWidth = 1;
    ctx.beginPath();
    ctx.moveTo(pBot.sx, pBot.sy);
    ctx.lineTo(pTop.sx, pTop.sy);
    ctx.stroke();
    ctx.restore();

    if (yTitle) {
      labelItems.push({
        priority: LABEL_PRI.AXIS_TITLE,
        order: 2,
        draw() {
          const font = '600 9.5px Inter, system-ui, sans-serif';
          ctx.font = font;
          const textW = ctx.measureText(yTitle).width;
          const tx = pTop.sx - 6;
          const ty = pTop.sy - 14;
          if (!tryPlaceBox(tx - textW, ty - 6, textW, 12, 6)) return;
          ctx.save();
          ctx.font = font;
          drawHaloText(yTitle, tx, ty, '#8e887f', 'right', 'middle', 'rgba(15, 14, 13, 0.9)', 3.2);
          ctx.restore();
        },
      });
    }

    if (!yScale) return;

    const maxTicks = Math.max(2, Math.min(5, Math.floor(axisPxLen / 52) + 1));
    const maxVal = yScale.valAt(yTopWorld);
    if (!(maxVal > 0)) return;

    const isInt = yScale.unit === 'count';
    const step = niceStep(maxVal, maxTicks - 1, isInt);
    const tickValues = [0];
    for (let v = step; v <= maxVal * 1.15; v += step) {
      if (yScale.yAt(v) <= yTopWorld + 0.35) tickValues.push(v);
    }

    let lastTickPt = null;
    for (let i = 0; i < tickValues.length; i++) {
      const v = tickValues[i];
      const y = v === 0 ? 0 : yScale.yAt(v);
      const pt = project(anchor.x, y, anchor.z);
      if (lastTickPt && Math.hypot(pt.sx - lastTickPt.sx, pt.sy - lastTickPt.sy) < 52) continue;
      lastTickPt = pt;

      const label = yScale.unit === 'ms' ? formatTickMs(v) : formatTickCount(v);
      ctx.save();
      ctx.strokeStyle = 'rgba(255, 255, 255, 0.24)';
      ctx.lineWidth = 1;
      ctx.beginPath();
      ctx.moveTo(pt.sx - 4, pt.sy);
      ctx.lineTo(pt.sx, pt.sy);
      ctx.stroke();
      ctx.restore();

      labelItems.push({
        priority: LABEL_PRI.TICK,
        order: 100 + i,
        draw() {
          const font = '500 10.5px Inter, system-ui, sans-serif';
          ctx.font = font;
          const textW = ctx.measureText(label).width;
          const lx = pt.sx - 7;
          const ly = pt.sy;
          if (!tryPlaceBox(lx - textW, ly - 6.5, textW, 13, 6)) return;
          ctx.save();
          ctx.font = font;
          drawHaloText(label, lx, ly, '#9c968d', 'right', 'middle', 'rgba(15, 14, 13, 0.9)', 3.2);
          ctx.restore();
        },
      });
    }
  }

  function queueLaneLabels(lanes, xSpan, zSpan, xEdgeSign, colorFn) {
    const isNarrow = width < 600;
    const maxChars = isNarrow ? 14 : 22;
    const xPos = xEdgeSign * (xSpan / 2 + 0.65);

    for (let l = 0; l < lanes.length; l++) {
      const lane = lanes[l];
      const z = -zSpan / 2 + ((l + 0.5) / lanes.length) * zSpan;
      const isIso = isolatedLane === l;
      const dim = isolatedLane !== null && !isIso;
      const softDim = isLaneSoftDim(l);
      let col = colorFn(lane, l, isIso, dim);
      if (dim) col = 'rgba(161,156,148,0.24)';
      else if (softDim) col = 'rgba(161,156,148,0.46)';

      const shortName = truncateText(lane.name, maxChars);
      queueFloorLabel(shortName, xPos, z, 1, 0, 0, -1, {
        priority: LABEL_PRI.LANE,
        order: (isIso ? -100 : 0) + l,
        color: col,
        size: isNarrow ? 10.5 : 11,
        weight: isIso ? 600 : 500,
        align: 'outward',
        outX: xEdgeSign,
        outZ: 0,
        pad: 3,
      });
    }
  }

  function queueTimeTicksFloor(ticks, xSpan, zSpan, zEdgeSign) {
    if (!ticks || ticks.length === 0) return;
    const zPos = zEdgeSign * (zSpan / 2 + 0.8);
    const pStart = project(-xSpan / 2, 0, zPos);
    const pEnd = project(xSpan / 2, 0, zPos);
    const totalPx = Math.hypot(pEnd.sx - pStart.sx, pEnd.sy - pStart.sy);
    const avgStepPx = ticks.length > 1 ? totalPx / (ticks.length - 1) : totalPx;
    const stride = avgStepPx > 0.1 ? Math.max(1, Math.ceil(52 / avgStepPx)) : ticks.length;

    let lastPt = null;
    for (let i = 0; i < ticks.length; i++) {
      const isLast = i === ticks.length - 1;
      if (i % stride !== 0 && !isLast) continue;
      const tick = ticks[i];
      const x = -xSpan / 2 + tick.fraction * xSpan;
      const pt = project(x, 0, zPos);
      if (lastPt && Math.hypot(pt.sx - lastPt.sx, pt.sy - lastPt.sy) < (isLast ? 48 : 52)) continue;
      lastPt = pt;

      queueFloorLabel(tick.label, x, zPos, 1, 0, 0, -1, {
        priority: LABEL_PRI.TICK,
        order: i,
        color: '#8e887f',
        size: 10.5,
        weight: 500,
        align: 'center',
        pad: 4,
      });
    }
  }

  function queueColumnHeadersFloor(headers, numCols, xSpan, zSpan, zEdgeSign, preferAngled) {
    if (numCols <= 0) return;
    const isNarrow = width < 600;
    const maxChars = isNarrow ? 12 : 18;
    const zPos = zEdgeSign * (zSpan / 2 + 0.72);

    const cellX = (k) => -xSpan / 2 + ((k + 0.5) / numCols) * xSpan;
    const pFirst = project(cellX(0), 0, zPos);
    const pLast = project(cellX(Math.max(0, numCols - 1)), 0, zPos);
    const colSpacingPx = numCols > 1
      ? Math.hypot(pLast.sx - pFirst.sx, pLast.sy - pFirst.sy) / (numCols - 1)
      : 160;

    // Check if Z axis is unforeshortened enough for angled endpoint headers when columns are dense
    let useAngled = false;
    if (preferAngled && numCols >= 4 && colSpacingPx < 105) {
      const testFt = computeFloorTransform(0, zPos, 0, 1, 1, 0, 0, zEdgeSign, 'outward');
      if (testFt && testFt.len1 >= 0.42) useAngled = true;
    }

    const minSpacing = useAngled ? 28 : 52;
    let lastPt = null;
    for (let k = 0; k < numCols; k++) {
      const raw = (headers && headers[k]) ? headers[k] : `Col ${k + 1}`;
      const label = truncateText(raw, maxChars);
      const x = cellX(k);
      const pt = project(x, 0, zPos);
      if (lastPt && Math.hypot(pt.sx - lastPt.sx, pt.sy - lastPt.sy) < minSpacing) continue;
      lastPt = pt;

      if (useAngled) {
        queueFloorLabel(label, x, zPos, 0, 1, 1, 0, {
          priority: LABEL_PRI.TICK,
          order: k,
          color: '#a19c94',
          size: isNarrow ? 10 : 10.5,
          weight: 500,
          align: 'outward',
          outX: 0,
          outZ: zEdgeSign,
          pad: 3,
        });
      } else {
        queueFloorLabel(label, x, zPos, 1, 0, 0, -1, {
          priority: LABEL_PRI.TICK,
          order: k,
          color: '#a19c94',
          size: isNarrow ? 10 : 10.5,
          weight: 500,
          align: 'center',
          pad: 4,
        });
      }
    }
    return useAngled;
  }

  function queueFloorAxisTitles(xTitle, zTitle, xSpan, zSpan, xEdgeSign, zEdgeSign, angledX) {
    if (xTitle) {
      const zTitlePos = zEdgeSign * (zSpan / 2 + (angledX ? 4.8 : 2.05));
      queueFloorLabel(xTitle, 0, zTitlePos, 1, 0, 0, -1, {
        priority: LABEL_PRI.AXIS_TITLE,
        order: 0,
        color: '#8e887f',
        size: 9.5,
        weight: 600,
        align: 'center',
        pad: 4,
      });
    }
    if (zTitle) {
      const xTitlePos = xEdgeSign * (xSpan / 2 + 0.65);
      const zHeaderPos = -zEdgeSign * (zSpan / 2 + 0.72);
      queueFloorLabel(zTitle, xTitlePos, zHeaderPos, 1, 0, 0, -1, {
        priority: LABEL_PRI.AXIS_TITLE,
        order: 1,
        color: '#8e887f',
        size: 9.5,
        weight: 600,
        align: 'outward',
        outX: xEdgeSign,
        outZ: 0,
        pad: 4,
      });
    }
  }

  function drawPoly(pts, fill, stroke, lineWidth = 1, dashed = false) {
    ctx.beginPath();
    ctx.moveTo(pts[0].sx, pts[0].sy);
    for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].sx, pts[i].sy);
    ctx.closePath();
    if (dashed) ctx.setLineDash([4, 4]);
    if (fill) { ctx.fillStyle = fill; ctx.fill(); }
    if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = lineWidth; ctx.stroke(); }
    if (dashed) ctx.setLineDash([]);
  }

  function drawFloorGrid(xMin, xMax, zMin, zMax, xStep, zStep) {
    ctx.strokeStyle = 'rgba(255,255,255,0.06)';
    ctx.lineWidth = 1;
    for (let x = xMin; x <= xMax + 0.01; x += xStep) {
      const a = project(x, 0, zMin), b = project(x, 0, zMax);
      ctx.beginPath(); ctx.moveTo(a.sx, a.sy); ctx.lineTo(b.sx, b.sy); ctx.stroke();
    }
    for (let z = zMin; z <= zMax + 0.01; z += zStep) {
      const a = project(xMin, 0, z), b = project(xMax, 0, z);
      ctx.beginPath(); ctx.moveTo(a.sx, a.sy); ctx.lineTo(b.sx, b.sy); ctx.stroke();
    }
  }

  function queueBox(queue, cx, cy, cz, w, h, d, baseColor, opts) {
    const hx = w / 2, hz = d / 2;
    const y0 = cy, y1 = cy + Math.max(0.03, h);
    const v = [
      project(cx - hx, y0, cz - hz), // 0
      project(cx + hx, y0, cz - hz), // 1
      project(cx + hx, y0, cz + hz), // 2
      project(cx - hx, y0, cz + hz), // 3
      project(cx - hx, y1, cz - hz), // 4
      project(cx + hx, y1, cz - hz), // 5
      project(cx + hx, y1, cz + hz), // 6
      project(cx - hx, y1, cz + hz), // 7
    ];
    const centerDepth = (v[0].depth + v[2].depth + v[4].depth + v[6].depth) / 4;
    const alpha = opts.dim ? 0.14 : opts.softDim ? 0.45 : (opts.alpha ?? 0.94);
    const stroke = opts.selected
      ? 'rgba(255,255,255,0.95)'
      : opts.hovered
        ? 'rgba(255,255,255,0.65)'
        : opts.dim
          ? 'rgba(255,255,255,0.06)'
          : opts.softDim
            ? 'rgba(15,14,13,0.28)'
            : (opts.stroke ?? 'rgba(15,14,13,0.45)');

    const faces = [
      { idx: [4, 5, 6, 7], shade: 1.12 }, // top
      { idx: [0, 1, 5, 4], shade: 0.72 }, // back
      { idx: [2, 3, 7, 6], shade: 0.88 }, // front
      { idx: [1, 2, 6, 5], shade: 0.80 }, // right
      { idx: [3, 0, 4, 7], shade: 0.65 }, // left
    ];

    const bounds = {
      minX: Math.min(...v.map(p => p.sx)),
      maxX: Math.max(...v.map(p => p.sx)),
      minY: Math.min(...v.map(p => p.sy)),
      maxY: Math.max(...v.map(p => p.sy)),
      depth: centerDepth,
    };

    queue.push({
      depth: centerDepth,
      yOrder: cy,
      draw() {
        const fList = faces.map(f => {
          const pts = f.idx.map(i => v[i]);
          const fDepth = pts.reduce((s, p) => s + p.depth, 0) / 4;
          return { pts, fDepth, shade: f.shade };
        }).sort((a, b) => b.fDepth - a.fDepth);

        for (const f of fList) {
          drawPoly(
            f.pts,
            rgb(shade(baseColor, f.shade), alpha),
            stroke,
            opts.selected ? 1.8 : 0.8
          );
        }
        if (opts.hit) {
          hitTargets.push({
            box: bounds,
            depth: centerDepth,
            data: opts.hit,
          });
        }
      },
    });
    return bounds;
  }

  function drawEmptyPad(queue, x, z, padW, padD, dim, softDim) {
    const hx = padW * 0.46, hz = padD * 0.46;
    const pts = [
      project(x - hx, 0.01, z - hz),
      project(x + hx, 0.01, z - hz),
      project(x + hx, 0.01, z + hz),
      project(x - hx, 0.01, z + hz),
    ];
    const centerDepth = (pts[0].depth + pts[2].depth) / 2;
    queue.push({
      depth: centerDepth + 0.02,
      yOrder: 0,
      draw() {
        drawPoly(
          pts,
          dim ? 'rgba(255,255,255,0.008)' : softDim ? 'rgba(255,255,255,0.01)' : 'rgba(255,255,255,0.018)',
          dim ? 'rgba(255,255,255,0.03)' : softDim ? 'rgba(255,255,255,0.05)' : 'rgba(255,255,255,0.09)',
          1,
          true
        );
      },
    });
  }

  function renderCallsMatrix() {
    const lanes = scene.lanes;
    const numCols = scene.timeBuckets;
    const xSpan = scene.xSpan || 20;
    const zSpan = scene.zSpan || 12;
    const { xEdgeSign, zEdgeSign } = getFloorEdges(xSpan, zSpan);

    const cellX = (colIdx) => -xSpan / 2 + ((colIdx + 0.5) / numCols) * xSpan;
    const cellZ = (laneIdx) => -zSpan / 2 + ((laneIdx + 0.5) / lanes.length) * zSpan;

    drawFloorGrid(-xSpan / 2, xSpan / 2, -zSpan / 2, zSpan / 2, xSpan / numCols, zSpan / lanes.length);

    const angledX = queueColumnHeadersFloor(scene.stepHeaders, numCols, xSpan, zSpan, zEdgeSign, true);
    queueLaneLabels(lanes, xSpan, zSpan, xEdgeSign, (_lane, _l, isIso) => (isIso ? '#ffffff' : '#f1efea'));
    queueFloorAxisTitles('ENDPOINTS', 'SCENARIOS', xSpan, zSpan, xEdgeSign, zEdgeSign, angledX);

    const padW = (xSpan / numCols) * 0.74;
    const padD = (zSpan / lanes.length) * 0.68;
    const barW = padW * 0.68;
    const barD = padD * 0.68;
    const queue = [];
    let maxTop = 0;

    for (const cell of scene.cells) {
      const x = cellX(cell.bucketIndex);
      const z = cellZ(cell.laneIndex);
      const dim = isolatedLane !== null && isolatedLane !== cell.laneIndex;
      const softDim = isLaneSoftDim(cell.laneIndex);

      if (cell.isEmptyPad) {
        drawEmptyPad(queue, x, z, padW, padD, dim, softDim);
        continue;
      }

      const isSel = selected && selected.laneIndex === cell.laneIndex && selected.bucketIndex === cell.bucketIndex;
      const isHov = hovered && hovered.laneIndex === cell.laneIndex && hovered.bucketIndex === cell.bucketIndex;

      const padBounds = queueBox(queue, x, 0, z, padW, 0.08, padD, [40, 37, 34], {
        dim,
        softDim,
        alpha: 0.92,
        stroke: isSel ? 'rgba(255,255,255,0.9)' : isHov ? 'rgba(255,255,255,0.5)' : 'rgba(255,255,255,0.12)',
      });

      const pb = queueBox(queue, x, 0.08, z, barW, cell.height, barD, cell.color, {
        dim,
        softDim,
        selected: isSel,
        hovered: isHov,
      });

      hitTargets.push({
        box: {
          minX: Math.min(padBounds.minX, pb.minX) - 3,
          maxX: Math.max(padBounds.maxX, pb.maxX) + 3,
          minY: Math.min(padBounds.minY, pb.minY) - 3,
          maxY: Math.max(padBounds.maxY, pb.maxY) + 3,
        },
        depth: Math.min(padBounds.depth, pb.depth),
        data: cell,
      });

      const topY = 0.08 + cell.height;
      if (topY > maxTop) maxTop = topY;

      if (cell.badgeText && !dim && !softDim) {
        const labelPt = project(x, topY + 0.36, z);
        if (isSel || isHov) {
          queuePillBadge(cell.badgeText, labelPt.sx, labelPt.sy, {
            priority: LABEL_PRI.SELECTED_HOVERED,
            order: isSel ? 0 : 1,
            color: cell.alertBadge ? '#ffb3b3' : '#ffffff',
            border: isSel ? 'rgba(255,255,255,0.65)' : 'rgba(255,255,255,0.35)',
          });
        } else if (cell.alertBadge) {
          queuePillBadge(cell.badgeText, labelPt.sx, labelPt.sy, {
            priority: LABEL_PRI.ALERT,
            order: -Math.round(cell.height * 100),
            color: '#ff9b9b',
            border: 'rgba(240,82,82,0.48)',
          });
        } else {
          ctx.font = '500 10px Inter, system-ui, sans-serif';
          const tw = ctx.measureText(cell.badgeText).width;
          const footprintW = padBounds.maxX - padBounds.minX;
          if (footprintW >= tw + 10) {
            queueHaloBadge(cell.badgeText, labelPt.sx, labelPt.sy, {
              priority: LABEL_PRI.CELL_BADGE,
              order: -Math.round(cell.height * 100),
              color: '#e5e2dc',
            });
          }
        }
      }
    }

    queue.sort((a, b) => (b.depth - a.depth) || ((a.yOrder ?? 0) - (b.yOrder ?? 0)));
    for (const item of queue) item.draw();
    hitTargets.sort((a, b) => a.depth - b.depth);

    renderYAxis(xSpan, zSpan, maxTop, inferYScale(scene), 'LATENCY');
    flushLabels();
  }

  function renderSwarm() {
    const lanes = scene.lanes;
    const maxSteps = scene.timeBuckets;
    const xSpan = scene.xSpan || 18;
    const zSpan = scene.zSpan || 12;
    const { xEdgeSign, zEdgeSign } = getFloorEdges(xSpan, zSpan);

    const cellX = (stepIdx) => -xSpan / 2 + ((stepIdx + 0.5) / maxSteps) * xSpan;
    const cellZ = (laneIdx) => -zSpan / 2 + ((laneIdx + 0.5) / lanes.length) * zSpan;

    drawFloorGrid(-xSpan / 2, xSpan / 2, -zSpan / 2, zSpan / 2, xSpan / maxSteps, zSpan / lanes.length);

    queueColumnHeadersFloor(scene.stepHeaders, maxSteps, xSpan, zSpan, zEdgeSign, false);
    queueLaneLabels(lanes, xSpan, zSpan, xEdgeSign, (_lane, _l, isIso) => (isIso ? '#ffffff' : '#f1efea'));
    queueFloorAxisTitles('STEPS', 'SCENARIOS', xSpan, zSpan, xEdgeSign, zEdgeSign, false);

    const padW = (xSpan / maxSteps) * 0.74;
    const padD = (zSpan / lanes.length) * 0.68;
    const barW = padW * 0.68;
    const barD = padD * 0.68;
    const isNarrow = width < 600;

    // Draw connecting flow rails along declared steps
    for (let l = 0; l < lanes.length; l++) {
      const lane = lanes[l];
      const declared = Math.max(1, lane.declaredSteps || maxSteps);
      const z = cellZ(l);
      const dim = isolatedLane !== null && isolatedLane !== l;
      const softDim = isLaneSoftDim(l);
      if (declared > 1) {
        const a = project(cellX(0), 0.04, z);
        const b = project(cellX(declared - 1), 0.04, z);
        ctx.beginPath();
        ctx.moveTo(a.sx, a.sy);
        ctx.lineTo(b.sx, b.sy);
        ctx.strokeStyle = dim ? 'rgba(91,155,240,0.06)' : softDim ? 'rgba(91,155,240,0.12)' : 'rgba(91,155,240,0.28)';
        ctx.lineWidth = 2;
        ctx.stroke();
      }
    }

    const queue = [];
    const topYGrid = Array.from({ length: lanes.length }, () => new Array(maxSteps).fill(0.08));
    let maxTop = 0;

    for (const cell of scene.cells) {
      const x = cellX(cell.bucketIndex);
      const z = cellZ(cell.laneIndex);
      const dim = isolatedLane !== null && isolatedLane !== cell.laneIndex;
      const softDim = isLaneSoftDim(cell.laneIndex);

      if (cell.isEmptyPad) {
        drawEmptyPad(queue, x, z, padW, padD, dim, softDim);
        continue;
      }

      const isSel = selected && selected.laneIndex === cell.laneIndex && selected.bucketIndex === cell.bucketIndex;
      const isHov = hovered && hovered.laneIndex === cell.laneIndex && hovered.bucketIndex === cell.bucketIndex;
      const primaryH = cell.height > 0 ? cell.height : 0;
      const secondaryH = cell.secondaryHeight > 0 ? cell.secondaryHeight : 0;
      const hasBars = primaryH > 0 || secondaryH > 0;

      const padBounds = queueBox(queue, x, 0, z, padW, 0.08, padD, [40, 37, 34], {
        dim,
        softDim,
        alpha: 0.92,
        selected: isSel && !hasBars,
        hovered: isHov && !hasBars,
        stroke: isSel ? 'rgba(255,255,255,0.9)' : isHov ? 'rgba(255,255,255,0.5)' : 'rgba(255,255,255,0.12)',
      });

      let minX = padBounds.minX, maxX = padBounds.maxX;
      let minY = padBounds.minY, maxY = padBounds.maxY;
      let hitDepth = padBounds.depth;

      if (primaryH > 0) {
        const pb = queueBox(queue, x, 0.08, z, barW, primaryH, barD, cell.color, {
          dim,
          softDim,
          selected: isSel,
          hovered: isHov,
        });
        minX = Math.min(minX, pb.minX);
        maxX = Math.max(maxX, pb.maxX);
        minY = Math.min(minY, pb.minY);
        maxY = Math.max(maxY, pb.maxY);
        hitDepth = Math.min(hitDepth, pb.depth);
      }

      if (secondaryH > 0 && cell.secondaryColor) {
        const sb = queueBox(queue, x, 0.08 + primaryH, z, barW, secondaryH, barD, cell.secondaryColor, {
          dim,
          softDim,
          selected: isSel,
          hovered: isHov,
        });
        minX = Math.min(minX, sb.minX);
        maxX = Math.max(maxX, sb.maxX);
        minY = Math.min(minY, sb.minY);
        maxY = Math.max(maxY, sb.maxY);
        hitDepth = Math.min(hitDepth, sb.depth);
      }

      hitTargets.push({
        box: { minX: minX - 3, maxX: maxX + 3, minY: minY - 3, maxY: maxY + 3 },
        depth: hitDepth,
        data: cell,
      });

      const topY = 0.08 + primaryH + secondaryH;
      topYGrid[cell.laneIndex][cell.bucketIndex] = topY;
      if (topY > maxTop) maxTop = topY;

      if (!dim && !softDim && (cell.stepName || cell.badgeText)) {
        const labelPt = project(x, topY + 0.42, z);
        if ((isSel || isHov) && (cell.badgeText || cell.stepName)) {
          const txt = truncateText(cell.badgeText || cell.stepName, isNarrow ? 22 : 34);
          queuePillBadge(txt, labelPt.sx, labelPt.sy, {
            priority: LABEL_PRI.SELECTED_HOVERED,
            order: isSel ? 0 : 1,
            color: cell.alertBadge ? '#ffb3b3' : '#ffffff',
            border: isSel ? 'rgba(255,255,255,0.65)' : 'rgba(255,255,255,0.35)',
          });
        } else if (cell.alertBadge && cell.badgeText) {
          const txt = truncateText(cell.badgeText, isNarrow ? 18 : 28);
          queuePillBadge(txt, labelPt.sx, labelPt.sy, {
            priority: LABEL_PRI.ALERT,
            order: -Math.round(topY * 100),
            color: '#ff9b9b',
            border: 'rgba(240,82,82,0.48)',
          });
        } else {
          const candidate = cell.badgeText || cell.stepName;
          const shortCandidate = truncateText(candidate, isNarrow ? 10 : 16);
          ctx.font = '500 10px Inter, system-ui, sans-serif';
          const tw = ctx.measureText(shortCandidate).width;
          const footprintW = padBounds.maxX - padBounds.minX;
          if (shortCandidate && footprintW >= tw + 10) {
            const pt = hasBars ? labelPt : project(x, 0.22, z);
            queueHaloBadge(shortCandidate, pt.sx, pt.sy, {
              priority: LABEL_PRI.CELL_BADGE,
              order: Math.round(pt.depth * 100),
              color: hasBars ? '#f1efea' : 'rgba(241,239,234,0.68)',
            });
          }
        }
      }
    }

    function arcPoint(scenarioIdx, fromStep, toStep, t) {
      const z = cellZ(scenarioIdx);
      const x0 = cellX(fromStep);
      const x1 = cellX(toStep);
      const sCurve = Math.sin(t * Math.PI);

      if (fromStep === toStep) {
        const top = topYGrid[scenarioIdx]?.[fromStep] ?? 0.08;
        const yBase = Math.max(0.45, top + 0.14);
        return {
          x: x0 + Math.sin(t * Math.PI * 2) * 0.56,
          y: yBase + sCurve * 1.45,
          z: z - sCurve * 0.52,
        };
      }

      if (toStep < fromStep) {
        const span = fromStep - toStep;
        const dip = 1.15 + span * 0.65;
        return {
          x: x0 + (x1 - x0) * t,
          y: -0.06 - sCurve * dip,
          z: z - sCurve * (0.30 + span * 0.10),
        };
      }

      if (toStep > fromStep + 1) {
        const span = toStep - fromStep;
        const peak = 1.15 + span * 0.55;
        return {
          x: x0 + (x1 - x0) * t,
          y: 0.22 + sCurve * peak,
          z: z + sCurve * 0.36,
        };
      }

      return {
        x: x0 + (x1 - x0) * t,
        y: 0.20 + sCurve * 0.36,
        z,
      };
    }

    const arcs = scene.arcs || [];
    const COL_LOOP = [251, 191, 36];   // #fbbf24
    const COL_CYAN = [56, 189, 248];   // #38bdf8
    const COL_NEXT = [91, 155, 240];   // #5b9bf0

    if (showJumpArcs && arcs.length > 0) {
      for (const arc of arcs) {
        if (arc.kind === 'next') continue;
        const dim = isolatedLane !== null && isolatedLane !== arc.scenarioIndex;
        const softDim = isLaneSoftDim(arc.scenarioIndex);
        const isLoop = arc.kind === 'loop';
        const isRepeat = arc.kind === 'repeat';
        const col = isLoop ? COL_LOOP : isRepeat ? COL_CYAN : COL_NEXT;
        const heat = Math.min(1, 0.25 + Math.log10(1 + arc.count) * 0.45);
        const alpha = dim ? 0.04 : softDim ? (0.08 + heat * 0.25) : (0.18 + heat * 0.62);
        const lw = 1.1 + heat * 1.4;

        const segs = 16;
        const pts = [];
        let midDepth = 0;
        let midY = 0;
        for (let i = 0; i <= segs; i++) {
          const p3 = arcPoint(arc.scenarioIndex, arc.fromStep, arc.toStep, i / segs);
          const pp = project(p3.x, p3.y, p3.z);
          pts.push(pp);
          if (i === segs / 2) {
            midDepth = pp.depth;
            midY = p3.y;
          }
        }

        queue.push({
          depth: isLoop ? midDepth + 0.15 : midDepth - 0.1,
          yOrder: midY,
          draw() {
            ctx.beginPath();
            ctx.moveTo(pts[0].sx, pts[0].sy);
            for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].sx, pts[i].sy);
            ctx.strokeStyle = rgb(col, alpha);
            ctx.lineWidth = lw;
            if (isLoop) ctx.setLineDash([5, 3]);
            ctx.stroke();
            if (isLoop) ctx.setLineDash([]);

            const tip = pts[Math.floor(segs * 0.85)];
            const prev = pts[Math.floor(segs * 0.78)];
            const ang = Math.atan2(tip.sy - prev.sy, tip.sx - prev.sx);
            const head = 5.2;
            ctx.beginPath();
            ctx.moveTo(tip.sx, tip.sy);
            ctx.lineTo(tip.sx - Math.cos(ang - 0.52) * head, tip.sy - Math.sin(ang - 0.52) * head);
            ctx.lineTo(tip.sx - Math.cos(ang + 0.52) * head, tip.sy - Math.sin(ang + 0.52) * head);
            ctx.closePath();
            ctx.fillStyle = rgb(col, Math.min(0.95, alpha + 0.12));
            ctx.fill();
          },
        });
      }
    }

    // Animated bee particles gliding along active arcs
    if (arcs.length > 0 && (scene.isRunning || showJumpArcs)) {
      for (const arc of arcs) {
        if (arc.kind === 'next' && !scene.isRunning) continue;
        if (arc.kind !== 'next' && !showJumpArcs) continue;
        const dim = isolatedLane !== null && isolatedLane !== arc.scenarioIndex;
        const softDim = isLaneSoftDim(arc.scenarioIndex);
        const isLoop = arc.kind === 'loop';
        const isNext = arc.kind === 'next';
        const col = isLoop ? COL_LOOP : isNext ? COL_NEXT : COL_CYAN;
        const speed = isNext ? 0.95 : 0.65;
        const particleCount = arc.count >= 20 ? 3 : arc.count >= 4 ? 2 : 1;
        const phaseBase = (arc.scenarioIndex * 0.31 + arc.fromStep * 0.19 + arc.toStep * 0.13) % 1;

        for (let k = 0; k < particleCount; k++) {
          const t = (swarmTime * speed + phaseBase + k / particleCount) % 1;
          const p3 = arcPoint(arc.scenarioIndex, arc.fromStep, arc.toStep, t);
          const p = project(p3.x, p3.y, p3.z);
          queue.push({
            depth: isLoop ? p.depth + 0.08 : p.depth - 0.2,
            yOrder: p3.y + (isLoop ? -0.1 : 0.2),
            draw() {
              ctx.beginPath();
              ctx.arc(p.sx, p.sy, dim ? 1.5 : (isNext ? 2.8 : 3.3), 0, Math.PI * 2);
              ctx.fillStyle = rgb(col, dim ? 0.14 : softDim ? 0.42 : 0.95);
              ctx.fill();
            },
          });
        }
      }
    }

    queue.sort((a, b) => (b.depth - a.depth) || ((a.yOrder ?? 0) - (b.yOrder ?? 0)));
    for (const item of queue) item.draw();
    hitTargets.sort((a, b) => a.depth - b.depth);

    renderYAxis(xSpan, zSpan, maxTop, inferYScale(scene), 'BEES');
    flushLabels();
  }

  function renderScene() {
    ctx.setTransform(dprX, 0, 0, dprY, 0, 0);
    ctx.clearRect(0, 0, width, height);
    hitTargets = [];
    placedBoxes = [];
    labelItems = [];
    if (!scene || !scene.lanes || scene.lanes.length === 0 || !scene.timeBuckets) return;

    if (scene.kind === 'swarm') {
      renderSwarm();
      return;
    }

    if (scene.kind === 'calls') {
      renderCallsMatrix();
      return;
    }

    const lanes = scene.lanes;
    const timeBuckets = scene.timeBuckets;
    const xSpan = scene.xSpan || 22;
    const zSpan = scene.zSpan || 10;
    const { xEdgeSign, zEdgeSign } = getFloorEdges(xSpan, zSpan);

    drawFloorGrid(-xSpan / 2, xSpan / 2, -zSpan / 2, zSpan / 2, xSpan / 6, zSpan / lanes.length);

    queueTimeTicksFloor(scene.timeTicks, xSpan, zSpan, zEdgeSign);
    queueLaneLabels(lanes, xSpan, zSpan, xEdgeSign, (lane, l, isIso) => {
      if (isIso) return '#ffffff';
      if (scene.kind === 'history' && l === 0) return '#f1efea';
      if (scene.kind === 'history' && lane.isStopped) return '#f08a8a';
      if (scene.kind === 'surface' && lane.highlight) return '#fbbf24';
      return '#b5b0a8';
    });

    const zTitle = scene.kind === 'surface'
      ? 'LATENCY BAND'
      : scene.kind === 'history'
        ? 'RUNS'
        : scene.kind === 'errors'
          ? 'ERROR SIGNATURE'
          : 'LANES';
    const yTitle = scene.kind === 'surface'
      ? 'BEES'
      : scene.kind === 'history'
        ? 'MAX DURATION'
        : scene.kind === 'errors'
          ? 'FAILURES'
          : 'AVG DURATION';
    queueFloorAxisTitles('RUN TIME', zTitle, xSpan, zSpan, xEdgeSign, zEdgeSign, false);

    const queue = [];
    const barW = (xSpan / timeBuckets) * (scene.kind === 'surface' ? 0.85 : 0.78);
    const barD = (zSpan / lanes.length) * (scene.kind === 'surface' ? 0.78 : 0.56);

    if (scene.kind === 'history') {
      for (let l = 0; l < lanes.length; l++) {
        const lane = lanes[l];
        if (lane.endFraction == null || lane.endFraction >= 0.992 || lane.endFraction <= 0) continue;
        const xEnd = -xSpan / 2 + lane.endFraction * xSpan;
        const z = -zSpan / 2 + ((l + 0.5) / lanes.length) * zSpan;
        const dim = isolatedLane !== null && isolatedLane !== l;
        const softDim = isLaneSoftDim(l);
        const p0 = project(xEnd, 0, z - barD * 0.68);
        const p1 = project(xEnd, 0, z + barD * 0.68);
        const pTop = project(xEnd, 1.05, z);
        const pBot = project(xEnd, 0, z);
        const capAlpha = dim ? 0.18 : softDim ? 0.35 : (lane.isStopped ? 0.78 : 0.55);
        const capColor = lane.isStopped ? `rgba(240,82,82,${capAlpha})` : `rgba(161,156,148,${capAlpha})`;
        queue.push({
          depth: pBot.depth - 0.05,
          draw() {
            ctx.save();
            ctx.strokeStyle = capColor;
            ctx.lineWidth = 1.6;
            ctx.setLineDash([4, 3]);
            ctx.beginPath(); ctx.moveTo(p0.sx, p0.sy); ctx.lineTo(p1.sx, p1.sy); ctx.stroke();
            ctx.setLineDash([]);
            ctx.beginPath(); ctx.moveTo(pBot.sx, pBot.sy); ctx.lineTo(pTop.sx, pTop.sy); ctx.stroke();
            ctx.restore();
          },
        });
        if (lane.endLabel && !dim && !softDim) {
          if (lane.isStopped) {
            queuePillBadge(lane.endLabel, pTop.sx, pTop.sy - 10, {
              priority: LABEL_PRI.ALERT,
              order: l,
              color: '#ff9b9b',
              border: 'rgba(240,82,82,0.45)',
            });
          } else {
            queueHaloBadge(lane.endLabel, pTop.sx + 4, pTop.sy - 8, {
              priority: LABEL_PRI.CELL_BADGE,
              order: l,
              align: 'left',
              color: '#b5b0a8',
            });
          }
        }
      }
    }

    let maxTop = 0;
    for (const cell of scene.cells) {
      const x = -xSpan / 2 + ((cell.bucketIndex + 0.5) / timeBuckets) * xSpan;
      const z = -zSpan / 2 + ((cell.laneIndex + 0.5) / lanes.length) * zSpan;
      const dim = isolatedLane !== null && isolatedLane !== cell.laneIndex;
      const softDim = isLaneSoftDim(cell.laneIndex);
      const isSel = selected && selected.laneIndex === cell.laneIndex && selected.bucketIndex === cell.bucketIndex;
      const isHov = hovered && hovered.laneIndex === cell.laneIndex && hovered.bucketIndex === cell.bucketIndex;

      if (cell.height > maxTop) maxTop = cell.height;

      queueBox(queue, x, 0, z, barW, cell.height, barD, cell.color, {
        dim,
        softDim,
        selected: isSel,
        hovered: isHov,
        hit: cell,
      });
    }

    queue.sort((a, b) => b.depth - a.depth);
    for (const item of queue) item.draw();
    hitTargets.sort((a, b) => a.depth - b.depth);

    renderYAxis(xSpan, zSpan, maxTop, inferYScale(scene), yTitle);
    flushLabels();
  }

  function isSwarmAnimating() {
    return !!(scene && scene.kind === 'swarm' && scene.arcs && scene.arcs.length > 0 && (scene.isRunning || showJumpArcs));
  }

  function frame(now) {
    rafId = 0;
    if (disposed) return;

    const ts = typeof now === 'number' ? now : performance.now();
    const dt = Math.min(0.05, Math.max(0, (ts - lastFrameTs) / 1000));
    lastFrameTs = ts;

    const dy = cam.targetYaw - cam.yaw;
    const dp = cam.targetPitch - cam.pitch;
    const dz = cam.targetZoom - cam.zoom;
    const easing = Math.abs(dy) > 0.0008 || Math.abs(dp) > 0.0008 || Math.abs(dz) > 0.0008;
    const swarmAnimating = isSwarmAnimating();

    if (easing) {
      cam.yaw += dy * 0.16;
      cam.pitch += dp * 0.16;
      cam.zoom += dz * 0.16;
      dirty = true;
    } else if (cam.yaw !== cam.targetYaw || cam.pitch !== cam.targetPitch || cam.zoom !== cam.targetZoom) {
      cam.yaw = cam.targetYaw;
      cam.pitch = cam.targetPitch;
      cam.zoom = cam.targetZoom;
      dirty = true;
    }

    if (swarmAnimating) {
      swarmTime += dt;
      dirty = true;
    }

    if (dirty) {
      dirty = false;
      renderScene();
    }

    if (easing || swarmAnimating) {
      rafId = requestAnimationFrame(frame);
    }
  }

  function scheduleFrame() {
    if (disposed || rafId) return;
    rafId = requestAnimationFrame(frame);
  }

  // ---------- Pointer / Wheel Orbit & Pick ----------
  let dragging = false;
  let moved = false;
  let lastX = 0;
  let lastY = 0;
  let notifiedCustom = false;

  function pick(mx, my) {
    for (const ht of hitTargets) {
      const b = ht.box;
      if (mx >= b.minX && mx <= b.maxX && my >= b.minY && my <= b.maxY) {
        return ht.data;
      }
    }
    return null;
  }

  function hideTooltip() {
    if (tooltip) tooltip.style.display = 'none';
  }

  function showTooltip(hit, mx, my) {
    if (!tooltip) return;
    const rowsHtml = (hit.tooltipRows || [])
      .map(r => `<div class="tt-row"><span>${esc(r.label)}</span><span class="tt-val">${esc(r.value)}</span></div>`)
      .join('');
    const errHtml = hit.tooltipError
      ? `<div class="tt-err">${esc(hit.tooltipError)}</div>`
      : '';
    tooltip.innerHTML = `<div class="tt-title">${esc(hit.tooltipTitle)}</div>${rowsHtml}${errHtml}`;
    tooltip.style.display = 'block';
    const left = Math.max(8, Math.min(width - 268, mx + 14));
    const top = Math.max(10, Math.min(height - 96, my - 12));
    tooltip.style.left = `${left}px`;
    tooltip.style.top = `${top}px`;
  }

  function onPointerDown(e) {
    if (e.button !== 0 && e.pointerType === 'mouse') return;
    dragging = true;
    moved = false;
    notifiedCustom = false;
    lastX = e.clientX;
    lastY = e.clientY;
    try { canvas.setPointerCapture(e.pointerId); } catch {}
  }

  function onPointerMove(e) {
    const rect = canvas.getBoundingClientRect();
    const mx = e.clientX - rect.left;
    const my = e.clientY - rect.top;

    if (dragging) {
      const dx = e.clientX - lastX;
      const dy = e.clientY - lastY;
      if (Math.abs(dx) > 2 || Math.abs(dy) > 2) moved = true;
      lastX = e.clientX;
      lastY = e.clientY;
      if (moved) {
        cam.targetYaw += dx * 0.008;
        cam.targetPitch = Math.max(0.05, Math.min(1.54, cam.targetPitch + dy * 0.008));
        hideTooltip();
        dirty = true;
        scheduleFrame();
        if (!notifiedCustom && dotnet) {
          notifiedCustom = true;
          dotnet.invokeMethodAsync('OnCameraCustom').catch(() => {});
        }
      }
      return;
    }

    const hit = pick(mx, my);
    const prevKey = hovered ? `${hovered.laneIndex}:${hovered.bucketIndex}` : '';
    const nextKey = hit ? `${hit.laneIndex}:${hit.bucketIndex}` : '';
    hovered = hit;
    canvas.style.cursor = hit ? 'pointer' : 'grab';

    if (prevKey !== nextKey) {
      dirty = true;
      scheduleFrame();
    }

    if (hit) showTooltip(hit, mx, my);
    else hideTooltip();
  }

  function onPointerUp(e) {
    if (!dragging) return;
    dragging = false;
    try { canvas.releasePointerCapture(e.pointerId); } catch {}
    if (!moved) {
      const rect = canvas.getBoundingClientRect();
      const hit = pick(e.clientX - rect.left, e.clientY - rect.top);
      selected = hit ? { laneIndex: hit.laneIndex, bucketIndex: hit.bucketIndex } : null;
      dirty = true;
      scheduleFrame();
      if (dotnet) {
        dotnet.invokeMethodAsync('OnCellSelected', hit ? hit.laneIndex : null, hit ? hit.bucketIndex : null).catch(() => {});
      }
    }
  }

  function onPointerLeave() {
    if (hovered) {
      hovered = null;
      dirty = true;
      scheduleFrame();
    }
    hideTooltip();
  }

  function onPointerCancel() {
    dragging = false;
    hideTooltip();
  }

  function onWheel(e) {
    e.preventDefault();
    cam.targetZoom = Math.max(0.55, Math.min(2.1, cam.targetZoom * (e.deltaY > 0 ? 0.92 : 1.08)));
    dirty = true;
    scheduleFrame();
  }

  canvas.addEventListener('pointerdown', onPointerDown);
  canvas.addEventListener('pointermove', onPointerMove);
  canvas.addEventListener('pointerup', onPointerUp);
  canvas.addEventListener('pointerleave', onPointerLeave);
  canvas.addEventListener('pointercancel', onPointerCancel);
  canvas.addEventListener('wheel', onWheel, { passive: false });

  return {
    setScene(nextScene) {
      scene = nextScene;
      hovered = null;
      hideTooltip();
      dirty = true;
      scheduleFrame();
    },
    setCamera(presetName) {
      const p = CAM_PRESETS[presetName] || CAM_PRESETS.iso;
      cam.targetYaw = p.yaw;
      cam.targetPitch = p.pitch;
      cam.targetZoom = p.zoom;
      dirty = true;
      scheduleFrame();
    },
    setIsolated(laneIdx) {
      isolatedLane = typeof laneIdx === 'number' ? laneIdx : null;
      dirty = true;
      scheduleFrame();
    },
    setSelected(laneIdx, bucketIdx) {
      selected = (typeof laneIdx === 'number' && typeof bucketIdx === 'number')
        ? { laneIndex: laneIdx, bucketIndex: bucketIdx }
        : null;
      dirty = true;
      scheduleFrame();
    },
    setShowArcs(show) {
      showJumpArcs = !!show;
      dirty = true;
      scheduleFrame();
    },
    dispose() {
      disposed = true;
      if (rafId) cancelAnimationFrame(rafId);
      if (ro) ro.disconnect();
      window.removeEventListener('resize', resize);
      canvas.removeEventListener('pointerdown', onPointerDown);
      canvas.removeEventListener('pointermove', onPointerMove);
      canvas.removeEventListener('pointerup', onPointerUp);
      canvas.removeEventListener('pointerleave', onPointerLeave);
      canvas.removeEventListener('pointercancel', onPointerCancel);
      canvas.removeEventListener('wheel', onWheel);
      hideTooltip();
    },
  };
}
