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

  function drawPoly(pts, fill, stroke, lineWidth = 1) {
    ctx.beginPath();
    ctx.moveTo(pts[0].sx, pts[0].sy);
    for (let i = 1; i < pts.length; i++) ctx.lineTo(pts[i].sx, pts[i].sy);
    ctx.closePath();
    if (fill) { ctx.fillStyle = fill; ctx.fill(); }
    if (stroke) { ctx.strokeStyle = stroke; ctx.lineWidth = lineWidth; ctx.stroke(); }
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
    const y0 = cy, y1 = cy + Math.max(0.05, h);
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
    const alpha = opts.dim ? 0.14 : 0.94;
    const stroke = opts.selected
      ? 'rgba(255,255,255,0.95)'
      : opts.hovered
        ? 'rgba(255,255,255,0.65)'
        : opts.dim
          ? 'rgba(255,255,255,0.06)'
          : 'rgba(15,14,13,0.45)';

    const faces = [
      { idx: [4, 5, 6, 7], shade: 1.12 }, // top
      { idx: [0, 1, 5, 4], shade: 0.72 }, // back
      { idx: [2, 3, 7, 6], shade: 0.88 }, // front
      { idx: [1, 2, 6, 5], shade: 0.80 }, // right
      { idx: [3, 0, 4, 7], shade: 0.65 }, // left
    ];

    queue.push({
      depth: centerDepth,
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
          const minX = Math.min(...v.map(p => p.sx));
          const maxX = Math.max(...v.map(p => p.sx));
          const minY = Math.min(...v.map(p => p.sy));
          const maxY = Math.max(...v.map(p => p.sy));
          hitTargets.push({
            box: { minX, maxX, minY, maxY },
            depth: centerDepth,
            data: opts.hit,
          });
        }
      },
    });
  }

  function renderScene() {
    ctx.clearRect(0, 0, width, height);
    hitTargets = [];
    if (!scene || !scene.lanes || scene.lanes.length === 0 || !scene.timeBuckets) return;

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

  function frame() {
    rafId = 0;
    if (disposed) return;

    const dy = cam.targetYaw - cam.yaw;
    const dp = cam.targetPitch - cam.pitch;
    const dz = cam.targetZoom - cam.zoom;
    const easing = Math.abs(dy) > 0.0008 || Math.abs(dp) > 0.0008 || Math.abs(dz) > 0.0008;

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

    if (dirty) {
      dirty = false;
      renderScene();
    }

    if (easing) {
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
