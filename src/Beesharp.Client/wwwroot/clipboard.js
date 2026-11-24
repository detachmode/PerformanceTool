// Copies text to the clipboard. navigator.clipboard only exists in secure contexts (https, localhost); the app may be opened over plain http,
// so without it a hidden textarea and document.execCommand('copy') do the job. Returns whether it worked.
export async function copy(text) {
    if (window.isSecureContext && navigator.clipboard?.writeText) {
        try { await navigator.clipboard.writeText(text); return true; } catch { /* fall through to the textarea */ }
    }
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;pointer-events:none';
    const active = document.activeElement;
    document.body.appendChild(area);
    area.select();
    area.setSelectionRange(0, text.length);
    let ok = false;
    try { ok = document.execCommand('copy'); } catch { ok = false; }
    area.remove();
    if (active instanceof HTMLElement) active.focus({ preventScroll: true });
    return ok;
}
