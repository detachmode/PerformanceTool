// Interactive horizontal drag-to-zoom brush for the Calls duration-over-time chart (brain 0060).
export function attach(plotEl, dotnet) {
  if (!plotEl) return;
  let dragging = false;
  let startX = 0;
  let pointerId = -1;
  const brush = plotEl.querySelector('.cl-brush');

  function setBrush(leftPx, widthPx) {
    if (!brush) return;
    brush.style.display = widthPx > 2 ? 'block' : 'none';
    brush.style.left = leftPx + 'px';
    brush.style.width = widthPx + 'px';
  }

  function onDown(e) {
    if (e.button !== 0) return;
    if (e.target.closest('button')) return;
    const rect = plotEl.getBoundingClientRect();
    if (rect.width <= 0) return;
    dragging = true;
    pointerId = e.pointerId;
    startX = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
    setBrush(startX, 0);
    try { plotEl.setPointerCapture(pointerId); } catch {}
  }

  function onMove(e) {
    if (!dragging || e.pointerId !== pointerId) return;
    const rect = plotEl.getBoundingClientRect();
    const x = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
    setBrush(Math.min(startX, x), Math.abs(x - startX));
  }

  function onUp(e) {
    if (!dragging || e.pointerId !== pointerId) return;
    dragging = false;
    setBrush(0, 0);
    try { plotEl.releasePointerCapture(pointerId); } catch {}
    const rect = plotEl.getBoundingClientRect();
    if (rect.width <= 0) return;
    const endX = Math.max(0, Math.min(rect.width, e.clientX - rect.left));
    if (Math.abs(endX - startX) < 12) return;
    const lo = Math.min(startX, endX) / rect.width;
    const hi = Math.max(startX, endX) / rect.width;
    dotnet.invokeMethodAsync('OnBrushRange', lo, hi).catch(() => {});
  }

  function onCancel() {
    dragging = false;
    setBrush(0, 0);
  }

  plotEl.addEventListener('pointerdown', onDown);
  plotEl.addEventListener('pointermove', onMove);
  plotEl.addEventListener('pointerup', onUp);
  plotEl.addEventListener('pointercancel', onCancel);

  return {
    dispose() {
      plotEl.removeEventListener('pointerdown', onDown);
      plotEl.removeEventListener('pointermove', onMove);
      plotEl.removeEventListener('pointerup', onUp);
      plotEl.removeEventListener('pointercancel', onCancel);
    }
  };
}

// Horizontal drag handle for the Calls overview target latency line.
export function attachTargetDrag(trackEl, handleEl, dotnet) {
  if (!trackEl || !handleEl) return null;
  let dragging = false;
  let pointerId = -1;
  let raf = 0;
  let pendingFrac = null;

  function fracFrom(clientX) {
    const rect = trackEl.getBoundingClientRect();
    if (rect.width <= 0) return 0;
    return Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
  }

  function flush(done) {
    if (raf) { cancelAnimationFrame(raf); raf = 0; }
    if (pendingFrac === null && !done) return;
    const f = pendingFrac ?? 0;
    pendingFrac = null;
    dotnet.invokeMethodAsync('OnTargetDrag', f, done).catch(() => {});
  }

  function onDown(e) {
    if (e.button !== 0) return;
    e.preventDefault();
    dragging = true;
    pointerId = e.pointerId;
    handleEl.classList.add('dragging');
    document.body.style.cursor = 'ew-resize';
    try { handleEl.setPointerCapture(pointerId); } catch {}
    pendingFrac = fracFrom(e.clientX);
    flush(false);
  }

  function onMove(e) {
    if (!dragging || e.pointerId !== pointerId) return;
    pendingFrac = fracFrom(e.clientX);
    if (!raf) raf = requestAnimationFrame(() => { raf = 0; flush(false); });
  }

  function endDrag(e, commit) {
    if (!dragging || e.pointerId !== pointerId) return;
    dragging = false;
    handleEl.classList.remove('dragging');
    document.body.style.cursor = '';
    try { handleEl.releasePointerCapture(pointerId); } catch {}
    if (commit) {
      pendingFrac = fracFrom(e.clientX);
      flush(true);
    } else if (raf) {
      cancelAnimationFrame(raf);
      raf = 0;
      pendingFrac = null;
      dotnet.invokeMethodAsync('OnTargetDragCancel').catch(() => {});
    }
  }

  function onUp(e) { endDrag(e, true); }
  function onCancel(e) { endDrag(e, false); }

  handleEl.addEventListener('pointerdown', onDown);
  handleEl.addEventListener('pointermove', onMove);
  handleEl.addEventListener('pointerup', onUp);
  handleEl.addEventListener('pointercancel', onCancel);

  return {
    dispose() {
      if (raf) cancelAnimationFrame(raf);
      handleEl.classList.remove('dragging');
      if (dragging) document.body.style.cursor = '';
      handleEl.removeEventListener('pointerdown', onDown);
      handleEl.removeEventListener('pointermove', onMove);
      handleEl.removeEventListener('pointerup', onUp);
      handleEl.removeEventListener('pointercancel', onCancel);
    }
  };
}
