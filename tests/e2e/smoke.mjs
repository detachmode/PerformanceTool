// Smoke test against a running Beesharp host (CI: the freshly built Docker image). Usage: BASE_URL=http://localhost:8080 node smoke.mjs
// Fails when the client does not boot: unresolved asset placeholders in index.html, missing _framework files, script errors,
// or the server API not answering. Exit code 1 on any failure.
import { chromium } from 'playwright';

const base = (process.env.BASE_URL ?? 'http://localhost:8080').replace(/\/$/, '');
const failures = [];
const check = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) failures.push(what); };

// 1. The server answers and index.html has its placeholders filled.
const indexRes = await fetch(`${base}/`);
const index = await indexRes.text();
check(indexRes.ok, `GET / -> ${indexRes.status}`);
check(!index.includes('{fingerprint}'), 'index.html has no #[.{fingerprint}] placeholder left');
check(!index.includes('<script type="importmap"></script>') && !index.includes('"imports":{}'), 'index.html has a filled importmap');

const api = await fetch(`${base}/api/scenarios`);
check(api.ok, `GET /api/scenarios -> ${api.status}`);

// 2. The WebAssembly client boots in a real browser, on / and on a deep link.
const browser = await chromium.launch();
try {
  for (const path of ['/', '/calls', '/editor']) {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', e => errors.push(`page error: ${e.message}`));
    page.on('console', m => { if (m.type() === 'error') errors.push(`console: ${m.text()}`); });
    page.on('response', r => { if (r.url().startsWith(base) && r.status() >= 400) errors.push(`${r.status()} ${r.url()}`); });
    page.on('requestfailed', r => { if (r.url().startsWith(base)) errors.push(`request failed: ${r.url()} (${r.failure()?.errorText})`); });

    await page.goto(base + path);
    let booted = true;
    try {
      // The boot splash disappears once Blazor has rendered the layout; the top bar's mode button is part of it.
      await page.locator('#boot-splash').waitFor({ state: 'detached', timeout: 60_000 });
      await page.locator('.mode-btn:visible').first().waitFor({ timeout: 10_000 });
    } catch {
      booted = false;
      const msg = await page.locator('#boot-error-msg').textContent().catch(() => null);
      if (msg) errors.push(`boot error shown: ${msg}`);
    }
    check(booted, `client boots on ${path}`);
    check(errors.length === 0, `no errors on ${path}${errors.length ? ':\n       ' + errors.join('\n       ') : ''}`);
    await page.close();
  }
} finally {
  await browser.close();
}

if (failures.length) { console.error(`\n${failures.length} check(s) failed`); process.exit(1); }
console.log('\nall checks passed');
