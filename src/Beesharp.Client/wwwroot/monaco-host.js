// Shared Monaco Editor loader, Beesharp dark theme, custom log language, and editor/log-viewer hosts.
const MONACO_VERSION = '0.52.2';
const MONACO_CDN_ROOT = `https://cdn.jsdelivr.net/npm/monaco-editor@${MONACO_VERSION}/min`;
const MONACO_VS_BASE = `${MONACO_CDN_ROOT}/vs`;
const LOAD_TIMEOUT_MS = 12000;

let monacoPromise = null;
let workerBlobUrl = null;
let themeAndLanguageRegistered = false;

function getWorkerBlobUrl() {
    if (!workerBlobUrl) {
        const src = [
            `self.MonacoEnvironment = { baseUrl: '${MONACO_CDN_ROOT}/' };`,
            `importScripts('${MONACO_VS_BASE}/base/worker/workerMain.js');`
        ].join('\n');
        workerBlobUrl = URL.createObjectURL(new Blob([src], { type: 'text/javascript' }));
    }
    return workerBlobUrl;
}

function loadAmdLoader() {
    return new Promise((resolve, reject) => {
        if (typeof window.require === 'function' && typeof window.require.config === 'function') {
            resolve();
            return;
        }
        const existing = document.querySelector('script[data-beesharp-monaco-loader]');
        if (existing) {
            existing.addEventListener('load', () => resolve(), { once: true });
            existing.addEventListener('error', () => reject(new Error('Monaco loader script failed')), { once: true });
            return;
        }
        const script = document.createElement('script');
        script.src = `${MONACO_VS_BASE}/loader.js`;
        script.async = true;
        script.dataset.beesharpMonacoLoader = '1';
        script.onload = () => resolve();
        script.onerror = () => reject(new Error('Failed to load Monaco AMD loader'));
        document.head.appendChild(script);
    });
}

function registerThemeAndLanguage(monaco) {
    if (themeAndLanguageRegistered) return;
    themeAndLanguageRegistered = true;

    monaco.languages.register({ id: 'beesharp-log' });
    monaco.languages.setMonarchTokensProvider('beesharp-log', {
        tokenizer: {
            root: [
                // Step divider banners like "── Checkout ──"
                [/──[^─\r\n]*──/, 'log.banner'],

                // Leading timestamp brackets: [0:05.9], [14:02:07.312], [0:05.9 · 14:02:07.312]
                [/^\[[^\]]+\]/, 'log.timestamp'],

                // Error / Fatal level + full line tinted coral red (preserving B123 highlights)
                [/(\b(?:ERR|FTL)\b)(\s+.*?)(\[B\d+\]|\bB\d+\b)(.*)/, ['log.error', 'log.error.text', 'log.bee', 'log.error.text']],
                [/(\b(?:ERR|FTL)\b)(.*)/, ['log.error', 'log.error.text']],

                // Warning level + full line tinted honey amber
                [/(\bWRN\b)(\s+.*?)(\[B\d+\]|\bB\d+\b)(.*)/, ['log.warn', 'log.warn.text', 'log.bee', 'log.warn.text']],
                [/(\bWRN\b)(.*)/, ['log.warn', 'log.warn.text']],

                // Info level + full line tinted emerald green
                [/(\bINF\b)(\s+.*?)(\[B\d+\]|\bB\d+\b)(.*)/, ['log.info', 'log.info.text', 'log.bee', 'log.info.text']],
                [/(\bINF\b)(.*)/, ['log.info', 'log.info.text']],

                // Debug / Trace level + full line tinted muted slate gray
                [/(\b(?:DBG|TRC|VRB)\b)(\s+.*?)(\[B\d+\]|\bB\d+\b)(.*)/, ['log.debug', 'log.debug.text', 'log.bee', 'log.debug.text']],
                [/(\b(?:DBG|TRC|VRB)\b)(.*)/, ['log.debug', 'log.debug.text']],

                // Out level + full line tinted soft sky
                [/(\bOUT\b)(\s+.*?)(\[B\d+\]|\bB\d+\b)(.*)/, ['log.out', 'log.out.text', 'log.bee', 'log.out.text']],
                [/(\bOUT\b)(.*)/, ['log.out', 'log.out.text']],

                // Bee identifiers: B123 or [B123]
                [/\[B\d+\]|\bB\d+\b/, 'log.bee'],
            ]
        }
    });

    monaco.editor.defineTheme('beesharp-dark', {
        base: 'vs-dark',
        inherit: true,
        rules: [
            // C# syntax tuned for Beesharp obsidian + honey palette
            { token: 'keyword', foreground: 'fbbf24' },
            { token: 'type.identifier', foreground: '7dd3fc' },
            { token: 'string', foreground: '34d399' },
            { token: 'number', foreground: 'fb923c' },
            { token: 'comment', foreground: '78716c', fontStyle: 'italic' },

            // Beesharp log language tokens (full line coloured by level)
            { token: 'log.timestamp', foreground: '6f6a63' },
            { token: 'log.error', foreground: 'f05252', fontStyle: 'bold' },
            { token: 'log.error.text', foreground: 'f87171' },
            { token: 'log.warn', foreground: 'fbbf24', fontStyle: 'bold' },
            { token: 'log.warn.text', foreground: 'fbbf24' },
            { token: 'log.info', foreground: '34d399', fontStyle: 'bold' },
            { token: 'log.info.text', foreground: '86efac' },
            { token: 'log.debug', foreground: '8c8578', fontStyle: 'bold' },
            { token: 'log.debug.text', foreground: '94a3b8' },
            { token: 'log.out', foreground: '7dd3fc', fontStyle: 'bold' },
            { token: 'log.out.text', foreground: '7dd3fc' },
            { token: 'log.bee', foreground: 'd6d2ca', fontStyle: 'bold' },
            { token: 'log.banner', foreground: '8c8578' },
        ],
        colors: {
            'editor.background': '#141311',
            'editorGutter.background': '#141311',
            'editor.foreground': '#f1efea',
            'editorLineNumber.foreground': '#6f6a63',
            'editorLineNumber.activeForeground': '#b8b2a8',
            'editor.selectionBackground': '#38342e',
            'editor.inactiveSelectionBackground': '#2a2723',
            'editor.lineHighlightBackground': '#1c1a17',
            'editor.lineHighlightBorder': '#24211d',
            'editorCursor.foreground': '#f1efea',
            'editorIndentGuide.background1': '#24211d',
            'editorIndentGuide.activeBackground1': '#3b3731',
            'scrollbarSlider.background': '#3a363080',
            'scrollbarSlider.hoverBackground': '#504b43a0',
            'scrollbarSlider.activeBackground': '#6a6359c0',
            'editorWidget.background': '#1a1917',
            'editorWidget.border': '#2e2b26',
        }
    });
}

function loadMonaco() {
    if (window.monaco && window.monaco.editor) {
        registerThemeAndLanguage(window.monaco);
        return Promise.resolve(window.monaco);
    }
    if (monacoPromise) return monacoPromise;

    monacoPromise = new Promise((resolve) => {
        let settled = false;
        const finish = (val) => {
            if (settled) return;
            settled = true;
            clearTimeout(timer);
            if (!val) monacoPromise = null;
            resolve(val);
        };

        const timer = setTimeout(() => finish(null), LOAD_TIMEOUT_MS);

        window.MonacoEnvironment = {
            getWorkerUrl: () => getWorkerBlobUrl()
        };

        loadAmdLoader()
            .then(() => {
                if (typeof window.require !== 'function' || typeof window.require.config !== 'function') {
                    finish(null);
                    return;
                }
                window.require.config({ paths: { vs: MONACO_VS_BASE } });
                window.require(
                    ['vs/editor/editor.main'],
                    () => {
                        if (window.monaco && window.monaco.editor) {
                            registerThemeAndLanguage(window.monaco);
                            finish(window.monaco);
                        } else {
                            finish(null);
                        }
                    },
                    () => finish(null)
                );
            })
            .catch(() => finish(null));
    });

    return monacoPromise;
}

// On a touch screen only a deliberate tap may raise the on-screen keyboard. Scrolling the editor, or touching it to stop a flick, must not:
// Monaco focuses its hidden input on such touches and the keyboard follows. So the input gets inputmode="none" (focus and hardware keyboards
// keep working) and switches to "text" for a clean tap (short, hardly any movement, not on a scrolling editor). It goes back to "none" when
// the editor loses the focus. Mouse and pen are untouched.
function installTouchKeyboardGuard(editor) {
    const root = editor.getContainerDomNode();
    const input = () => root.querySelector('textarea.inputarea');
    const setKeyboard = (on) => { const ta = input(); if (ta) ta.inputMode = on ? 'text' : 'none'; };
    const TAP_MS = 400, TAP_SLOP_PX = 10, SCROLL_SETTLE_MS = 300;

    let lastScrollAt = -Infinity;
    let down = null;
    const scrollSub = editor.onDidScrollChange((e) => {
        if (e.scrollTopChanged || e.scrollLeftChanged) lastScrollAt = performance.now();
    });

    const onDown = (e) => {
        if (e.pointerType !== 'touch') { down = null; setKeyboard(true); return; }
        setKeyboard(false);
        down = { id: e.pointerId, x: e.clientX, y: e.clientY, t: performance.now(), stopsFlick: performance.now() - lastScrollAt < SCROLL_SETTLE_MS };
    };
    const onUp = (e) => {
        if (!down || e.pointerId !== down.id) return;
        const d = down;
        down = null;
        const tap = !d.stopsFlick && performance.now() - d.t < TAP_MS && Math.hypot(e.clientX - d.x, e.clientY - d.y) < TAP_SLOP_PX;
        if (!tap) return;
        const ta = input();
        if (!ta) return;
        // Monaco focuses the input itself right after this (on its tap); an input that already has the focus (keyboard hidden by hand) needs a new focus.
        ta.inputMode = 'text';
        if (document.activeElement === ta) { ta.blur(); ta.inputMode = 'text'; ta.focus({ preventScroll: true }); }
    };
    const onCancel = () => { down = null; };
    const onFocusOut = (e) => { if (e.target === input()) setKeyboard(false); };

    root.addEventListener('pointerdown', onDown, true);
    root.addEventListener('pointerup', onUp, true);
    root.addEventListener('pointercancel', onCancel, true);
    root.addEventListener('focusout', onFocusOut, true);
    return () => {
        scrollSub.dispose();
        root.removeEventListener('pointerdown', onDown, true);
        root.removeEventListener('pointerup', onUp, true);
        root.removeEventListener('pointercancel', onCancel, true);
        root.removeEventListener('focusout', onFocusOut, true);
    };
}

export async function createCodeEditor(container, dotNetRef, initialValue, options) {
    const monaco = await loadMonaco();
    if (!monaco || !container || !container.isConnected) return null;

    const opts = options || {};
    const editor = monaco.editor.create(container, {
        value: initialValue ?? '',
        language: 'csharp',
        theme: 'beesharp-dark',
        automaticLayout: true,
        fontFamily: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
        fontSize: opts.fontSize ?? 13,
        lineHeight: 20,
        minimap: { enabled: false },
        scrollBeyondLastLine: false,
        tabSize: 4,
        insertSpaces: true,
        wordWrap: opts.wordWrap === false ? 'off' : 'on',
        ariaLabel: opts.ariaLabel || 'C# scenario script',
        renderLineHighlight: 'line',
        overviewRulerLanes: 0,
        hideCursorInOverviewRuler: true,
        padding: { top: 10, bottom: 10 },
    });

    const removeTouchGuard = installTouchKeyboardGuard(editor);

    let suppressChange = false;
    let debounceTimer = null;
    let disposed = false;

    const sub = editor.onDidChangeModelContent(() => {
        if (suppressChange || disposed || !dotNetRef) return;
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            if (disposed || !dotNetRef) return;
            dotNetRef.invokeMethodAsync('OnContentChanged', editor.getValue()).catch(() => {});
        }, 120);
    });

    return {
        setValue(v) {
            if (disposed) return;
            const next = v ?? '';
            if (editor.getValue() === next) return;
            clearTimeout(debounceTimer);
            suppressChange = true;
            try {
                const model = editor.getModel();
                if (model) model.setValue(next);
                else editor.setValue(next);
            } finally {
                suppressChange = false;
            }
        },
        setWordWrap(w) {
            if (disposed) return;
            editor.updateOptions({ wordWrap: w ? 'on' : 'off' });
        },
        layout() {
            if (disposed) return;
            editor.layout();
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            clearTimeout(debounceTimer);
            sub.dispose();
            removeTouchGuard();
            editor.dispose();
        }
    };
}

export async function createLogViewer(container, initialValue, options) {
    const monaco = await loadMonaco();
    if (!monaco || !container || !container.isConnected) return null;

    const opts = options || {};
    const initial = initialValue ?? '';
    const editor = monaco.editor.create(container, {
        value: initial,
        language: 'beesharp-log',
        theme: 'beesharp-dark',
        readOnly: true,
        domReadOnly: true,
        lineNumbers: opts.lineNumbers ?? 'off',
        glyphMargin: false,
        folding: false,
        lineDecorationsWidth: 8,
        lineNumbersMinChars: 0,
        minimap: { enabled: false },
        renderLineHighlight: 'none',
        scrollBeyondLastLine: false,
        automaticLayout: true,
        fontFamily: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace',
        fontSize: opts.fontSize ?? 12.5,
        lineHeight: 19,
        wordWrap: opts.wordWrap === false ? 'off' : 'on',
        ariaLabel: opts.ariaLabel || 'Log viewer',
        overviewRulerLanes: 0,
        hideCursorInOverviewRuler: true,
        scrollbar: { verticalScrollbarSize: 10, horizontalScrollbarSize: 10 },
        padding: { top: 6, bottom: 8 },
    });

    let disposed = false;

    const isNearBottom = () => {
        const scrollTop = editor.getScrollTop();
        const scrollHeight = editor.getScrollHeight();
        const layoutHeight = editor.getLayoutInfo().height;
        return scrollHeight - (scrollTop + layoutHeight) <= 32;
    };

    if (opts.followBottom && initial.length > 0) {
        const model = editor.getModel();
        if (model) editor.revealLine(model.getLineCount());
    }

    return {
        setValue(v, followBottom) {
            if (disposed) return;
            const next = v ?? '';
            if (editor.getValue() === next) return;
            const wasAtBottom = isNearBottom();
            const prevScrollTop = editor.getScrollTop();
            const prevScrollLeft = editor.getScrollLeft();
            const model = editor.getModel();
            if (model) model.setValue(next);
            else editor.setValue(next);

            if (followBottom && wasAtBottom) {
                const lineCount = model ? model.getLineCount() : 1;
                editor.revealLine(lineCount);
            } else {
                editor.setScrollPosition({ scrollTop: prevScrollTop, scrollLeft: prevScrollLeft });
            }
        },
        setWordWrap(w) {
            if (disposed) return;
            editor.updateOptions({ wordWrap: w ? 'on' : 'off' });
        },
        scrollToBottom() {
            if (disposed) return;
            const model = editor.getModel();
            if (model) editor.revealLine(model.getLineCount());
        },
        layout() {
            if (disposed) return;
            editor.layout();
        },
        dispose() {
            if (disposed) return;
            disposed = true;
            editor.dispose();
        }
    };
}
