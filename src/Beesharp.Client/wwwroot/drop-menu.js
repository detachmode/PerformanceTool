// DropMenu (brain 0068): when the menu opens, scroll its list so the current entry is visible (centred), and focus the filter field when there is one.
export function opened(menu) {
    if (!menu) return;
    const scroll = menu.querySelector('.dd-scroll');
    const cur = scroll && scroll.querySelector('.on, [aria-checked="true"], [aria-current="true"]');
    if (cur) {
        const s = scroll.getBoundingClientRect(), c = cur.getBoundingClientRect();
        scroll.scrollTop += (c.top - s.top) - (scroll.clientHeight - c.height) / 2;
    }
    const f = menu.querySelector('.dd-filter-input');
    if (f && !window.matchMedia('(pointer: coarse)').matches) f.focus({ preventScroll: true });
}
