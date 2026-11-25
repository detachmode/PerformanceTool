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

export function mount(canvas, tooltip, dotnet) {
  if (!canvas) return null;
  const ctx = canvas.getContext('2d');
  if (!ctx) return null;

  let width = 0;
  let height = 0;
  let dpr = 1;
  let scene = null;
  let isolatedLane = null;
  let selected = null; // { laneIndex, bucketIndex }
  let hovered = null;  // { laneIndex, bucketIndex }
  let showJumpArcs = true;
  let swarmTime = 0;
  let lastFrameTs = performance.now();
  let hitTargets = [];
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
    ctx.setTransform(pw / width, 0, 0, ph / height, 0, 0);
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

  function drawLabel3D(text, x, y, z, color = '#a19c94', align = 'center', size = 11.5) {
    const p = project(x, y, z);
    ctx.font = `500 ${size}px Inter, system-ui, sans-serif`;
    ctx.fillStyle = color;
    ctx.textAlign = align;
    ctx.textBaseline = 'middle';
    ctx.fillText(text, p.sx, p.sy);
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
    const alpha = opts.dim ? 0.14 : (opts.alpha ?? 0.94);
    const stroke = opts.selected
      ? 'rgba(255,255,255,0.95)'
      : opts.hovered
        ? 'rgba(255,255,255,0.65)'
        : opts.dim
          ? 'rgba(255,255,255,0.06)'
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

  function renderSwarm() {
    const lanes = scene.lanes;
    const maxSteps = scene.timeBuckets;
    const xSpan = scene.xSpan || 18;
    const zSpan = scene.zSpan || 12;

    const cellX = (stepIdx) => -xSpan / 2 + ((stepIdx + 0.5) / maxSteps) * xSpan;
    const cellZ = (laneIdx) => -zSpan / 2 + ((laneIdx + 0.5) / lanes.length) * zSpan;

    drawFloorGrid(-xSpan / 2, xSpan / 2, -zSpan / 2, zSpan / 2, xSpan / maxSteps, zSpan / lanes.length);

    // X-axis step headers
    for (let k = 0; k < maxSteps; k++) {
      const header = (scene.stepHeaders && scene.stepHeaders[k]) ? scene.stepHeaders[k] : `Step ${k + 1}`;
      drawLabel3D(header, cellX(k), 0, -zSpan / 2 - 1.05, '#a19c94', 'center', 11.5);
    }

    // Z-axis scenario lane labels
    for (let l = 0; l < lanes.length; l++) {
      const lane = lanes[l];
      const z = cellZ(l);
      const isIso = isolatedLane === l;
      const dim = isolatedLane !== null && !isIso;
      const col = isIso ? '#ffffff' : (dim ? 'rgba(161,156,148,0.28)' : '#f1efea');
      const shortName = lane.name.length > 28 ? lane.name.slice(0, 27) + '…' : lane.name;
      drawLabel3D(shortName, -xSpan / 2 - 1.0, 0.15, z, col, 'right', 11.5);
    }

    const padW = (xSpan / maxSteps) * 0.74;
    const padD = (zSpan / lanes.length) * 0.68;
    const barW = padW * 0.68;
    const barD = padD * 0.68;

    // Draw connecting flow rails along declared steps
    for (let l = 0; l < lanes.length; l++) {
      const lane = lanes[l];
      const declared = Math.max(1, lane.declaredSteps || maxSteps);
      const z = cellZ(l);
      const dim = isolatedLane !== null && isolatedLane !== l;
      if (declared > 1) {
        const a = project(cellX(0), 0.04, z);
        const b = project(cellX(declared - 1), 0.04, z);
        ctx.beginPath();
        ctx.moveTo(a.sx, a.sy);
        ctx.lineTo(b.sx, b.sy);
        ctx.strokeStyle = dim ? 'rgba(91,155,240,0.06)' : 'rgba(91,155,240,0.28)';
        ctx.lineWidth = 2;
        ctx.stroke();
      }
    }

    const queue = [];
    const topYGrid = Array.from({ length: lanes.length }, () => new Array(maxSteps).fill(0.08));

    for (const cell of scene.cells) {
      const x = cellX(cell.bucketIndex);
      const z = cellZ(cell.laneIndex);
      const dim = isolatedLane !== null && isolatedLane !== cell.laneIndex;

      if (cell.isEmptyPad) {
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
              dim ? 'rgba(255,255,255,0.008)' : 'rgba(255,255,255,0.018)',
              dim ? 'rgba(255,255,255,0.03)' : 'rgba(255,255,255,0.09)',
              1,
              true
            );
          },
        });
        continue;
      }

      const isSel = selected && selected.laneIndex === cell.laneIndex && selected.bucketIndex === cell.bucketIndex;
      const isHov = hovered && hovered.laneIndex === cell.laneIndex && hovered.bucketIndex === cell.bucketIndex;
      const primaryH = cell.height > 0 ? cell.height : 0;
      const secondaryH = cell.secondaryHeight > 0 ? cell.secondaryHeight : 0;
      const hasBars = primaryH > 0 || secondaryH > 0;

      const padBounds = queueBox(queue, x, 0, z, padW, 0.08, padD, [40, 37, 34], {
        dim,
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

      if (cell.stepName || cell.badgeText) {
        const showTopBadge = (cell.alertBadge || isSel || isHov) && cell.badgeText;
        const labelPt = project(x, topY + 0.42, z);
        const footPt = project(x, 0.14, z - padD * 0.56);
        queue.push({
          depth: (showTopBadge ? labelPt.depth : footPt.depth) - 0.6,
          yOrder: topY + 1,
          draw() {
            if (dim) return;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            if (showTopBadge) {
              ctx.font = '600 10.5px Inter, system-ui, sans-serif';
              ctx.fillStyle = cell.alertBadge ? '#ff9b9b' : '#ffffff';
              ctx.fillText(cell.badgeText, labelPt.sx, labelPt.sy);
            } else if (cell.stepName) {
              ctx.font = '500 10px Inter, system-ui, sans-serif';
              ctx.fillStyle = 'rgba(241,239,234,0.58)';
              ctx.fillText(cell.stepName, footPt.sx, footPt.sy);
            }
          },
        });
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
        const isLoop = arc.kind === 'loop';
        const isRepeat = arc.kind === 'repeat';
        const col = isLoop ? COL_LOOP : isRepeat ? COL_CYAN : COL_NEXT;
        const heat = Math.min(1, 0.25 + Math.log10(1 + arc.count) * 0.45);
        const alpha = dim ? 0.04 : (0.18 + heat * 0.62);
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
              ctx.fillStyle = rgb(col, dim ? 0.14 : 0.95);
              ctx.fill();
            },
          });
        }
      }
    }

    queue.sort((a, b) => (b.depth - a.depth) || ((a.yOrder ?? 0) - (b.yOrder ?? 0)));
    for (const item of queue) item.draw();
    hitTargets.sort((a, b) => a.depth - b.depth);
  }

  function renderScene() {
    ctx.clearRect(0, 0, width, height);
    hitTargets = [];
    if (!scene || !scene.lanes || scene.lanes.length === 0 || !scene.timeBuckets) return;

    if (scene.kind === 'swarm') {
      renderSwarm();
      return;
    }

    const lanes = scene.lanes;
    const timeBuckets = scene.timeBuckets;
    const xSpan = scene.xSpan || 22;
    const zSpan = scene.zSpan || 10;

    drawFloorGrid(-xSpan / 2, xSpan / 2, -zSpan / 2, zSpan / 2, xSpan / 6, zSpan / lanes.length);

    if (scene.timeTicks) {
      for (const tick of scene.timeTicks) {
        const x = -xSpan / 2 + tick.fraction * xSpan;
        drawLabel3D(tick.label, x, 0, -zSpan / 2 - 1.1, '#6f6a63', 'center', 11);
      }
    }

    for (let l = 0; l < lanes.length; l++) {
      const lane = lanes[l];
      const z = -zSpan / 2 + ((l + 0.5) / lanes.length) * zSpan;
      const isIso = isolatedLane === l;
      const col = isIso
        ? '#ffffff'
        : (scene.kind === 'history' && l === 0)
          ? '#f1efea'
          : (scene.kind === 'history' && lane.isStopped)
            ? '#f08a8a'
            : (scene.kind === 'surface' && lane.highlight ? '#fbbf24' : '#a19c94');
      const shortName = lane.name.length > 28 ? lane.name.slice(0, 27) + '…' : lane.name;
      drawLabel3D(shortName, -xSpan / 2 - 1.0, 0.2, z, col, 'right', 11);
    }

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
        const p0 = project(xEnd, 0, z - barD * 0.68);
        const p1 = project(xEnd, 0, z + barD * 0.68);
        const pTop = project(xEnd, 1.05, z);
        const pBot = project(xEnd, 0, z);
        const capColor = lane.isStopped ? `rgba(240,82,82,${dim ? 0.2 : 0.78})` : `rgba(161,156,148,${dim ? 0.16 : 0.55})`;
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
            if (lane.endLabel && !dim) {
              ctx.font = '600 10.5px Inter, system-ui, sans-serif';
              ctx.fillStyle = lane.isStopped ? '#f08a8a' : '#a19c94';
              ctx.textAlign = 'left';
              ctx.textBaseline = 'bottom';
              ctx.fillText(lane.endLabel, pTop.sx + 4, pTop.sy - 2);
            }
            ctx.restore();
          },
        });
      }
    }

    for (const cell of scene.cells) {
      const x = -xSpan / 2 + ((cell.bucketIndex + 0.5) / timeBuckets) * xSpan;
      const z = -zSpan / 2 + ((cell.laneIndex + 0.5) / lanes.length) * zSpan;
      const dim = isolatedLane !== null && isolatedLane !== cell.laneIndex;
      const isSel = selected && selected.laneIndex === cell.laneIndex && selected.bucketIndex === cell.bucketIndex;
      const isHov = hovered && hovered.laneIndex === cell.laneIndex && hovered.bucketIndex === cell.bucketIndex;

      queueBox(queue, x, 0, z, barW, cell.height, barD, cell.color, {
        dim,
        selected: isSel,
        hovered: isHov,
        hit: cell,
      });
    }

    queue.sort((a, b) => b.depth - a.depth);
    for (const item of queue) item.draw();
    hitTargets.sort((a, b) => a.depth - b.depth);
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
