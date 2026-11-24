// Bottom sheet drag (brain 0059, phone): the sheet follows the finger from its grab handle or its header, and on release C# (SheetSnap) decides where it settles.
// Only the height is moved while dragging; the stops themselves are CSS classes (half, full). A tap on the handle toggles half and full.
export function attach(sheet, ref, resizable) {
    const handles = () => [sheet.querySelector('.sheet-grab'), sheet.querySelector('.bp-head')].filter(Boolean);
    let drag = null;
    const vh = () => window.innerHeight;
    const stops = () => ({ half: resizable ? vh() * 0.58 : sheet.scrollHeight, full: vh() - 12 });

    const down = e => {
        if (e.pointerType === 'mouse' && e.button !== 0) return;
        drag = { id: e.pointerId, y: e.clientY, h: sheet.getBoundingClientRect().height, last: e.clientY, lastT: e.timeStamp, v: 0, moved: false, el: e.currentTarget };
    };
    const move = e => {
        if (!drag || e.pointerId !== drag.id) return;
        const dy = e.clientY - drag.y;
        if (!drag.moved) {
            if (Math.abs(dy) < 8) return;
            drag.moved = true;
            sheet.style.transition = 'none';
        }
        const { full } = stops();
        const h = Math.max(0, Math.min(full, drag.h - dy));
        sheet.style.height = h + 'px';
        const dt = e.timeStamp - drag.lastT;
        if (dt > 0) drag.v = 0.6 * drag.v + 0.4 * ((drag.last - e.clientY) / dt);
        drag.last = e.clientY; drag.lastT = e.timeStamp;
        e.preventDefault();
    };
    const up = async e => {
        if (!drag || e.pointerId !== drag.id) return;
        const d = drag; drag = null;
        if (!d.moved) {
            if (resizable && d.el.classList.contains('sheet-grab')) await ref.invokeMethodAsync('Toggle');
            return;
        }
        const { half, full } = stops();
        const h = sheet.getBoundingClientRect().height;
        let stop = 'Half';
        try { stop = await ref.invokeMethodAsync('DragEnd', h, d.v, half, full); } catch { }
        if (stop === 'Closed') return;
        sheet.style.transition = 'height .18s ease-out';
        sheet.style.height = (stop === 'Full' ? full : half) + 'px';
        setTimeout(() => { sheet.style.transition = ''; sheet.style.height = ''; }, 220);
    };
    // pointerdown on the handles; the rest on the document, so the drag follows the finger (or mouse) wherever it goes and a plain tap on a button in the header stays a click
    const hs = handles();
    for (const h of hs) h.addEventListener('pointerdown', down);
    document.addEventListener('pointermove', move, { passive: false });
    document.addEventListener('pointerup', up);
    document.addEventListener('pointercancel', up);
    return { dispose() {
        for (const h of hs) h.removeEventListener('pointerdown', down);
        document.removeEventListener('pointermove', move); document.removeEventListener('pointerup', up); document.removeEventListener('pointercancel', up);
    } };
}
