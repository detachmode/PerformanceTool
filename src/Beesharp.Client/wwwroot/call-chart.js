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
