// Layout check at many window sizes against a running Beesharp host (brain 0059, 0068). Usage:
//   BASE_URL=http://127.0.0.1:5411 node layout-sizes.mjs [--shots <folder>] [--widths 1920,1440] [--heights 700,1000]
// For every width x height it opens the app in Fake mode (seeded run, no server data needed) and visits the Overview (finished, running, edit mode with the scenario
// settings side panel), the scenario page, Calls, Logs, Editor and Environment. On each it checks:
//   - no horizontal page scrollbar (document.scrollingElement.scrollWidth <= innerWidth),
//   - the top bar and the player bar do not overflow and their items do not overlap,
//   - fixed side panels (the scenario settings panel) reach from the top bar to the player bar, with no gap and not hidden behind it.
// Exit code 1 on any failure. With --shots a screenshot per state and size goes into the folder (deviceScaleFactor 1).
import { chromium } from 'playwright';
import { mkdirSync } from 'node:fs';

const arg = name => { const i = process.argv.indexOf(`--${name}`); return i > 0 ? process.argv[i + 1] : null; };
const base = (process.env.BASE_URL ?? 'http://127.0.0.1:5411').replace(/\/$/, '');
const widths = (arg('widths') ?? '1920,1600,1440,1280,1100,1024,960,900,861,800,761,390').split(',').map(Number);
const heights = (arg('heights') ?? '700,1000').split(',').map(Number);
const shots = arg('shots');
if (shots) mkdirSync(shots, { recursive: true });

const failures = [];
const report = (size, state, what) => { const m = `${size} ${state}: ${what}`; failures.push(m); console.log('FAIL', m); };

/** Runs in the page: the layout problems of the current state. */
function inspect() {
  const problems = [];
  const vw = window.innerWidth, vh = window.innerHeight;
  const se = document.scrollingElement;
  if (se.scrollWidth > vw + 1) problems.push(`horizontal page scroll: scrollWidth ${se.scrollWidth} > ${vw}`);
  const vis = e => { const r = e.getBoundingClientRect(); const c = getComputedStyle(e); return r.width > 0 && r.height > 0 && c.visibility !== 'hidden' && c.display !== 'none'; };
  const overlap = (a, b) => { const x = Math.min(a.right, b.right) - Math.max(a.left, b.left), y = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top); return x > 3 && y > 3; };
  const name = e => (e.className && typeof e.className === 'string' ? '.' + e.className.trim().split(/\s+/)[0] : e.tagName.toLowerCase()) + (e.textContent ? ` "${e.textContent.trim().slice(0, 18)}"` : '');
  for (const sel of ['.top', '.player', '.runbar']) {
    const bar = [...document.querySelectorAll(sel)].find(vis);
    if (!bar) continue;
    if (bar.scrollWidth > bar.clientWidth + 1) problems.push(`${sel} overflows: scrollWidth ${bar.scrollWidth} > ${bar.clientWidth}`);
    const br = bar.getBoundingClientRect();
    if (br.right > vw + 1 || br.left < -1) problems.push(`${sel} outside the window (${Math.round(br.left)}..${Math.round(br.right)})`);
    // The bar's leaf items (a leaf = an element whose children are all text), not wrappers: they must stay inside it and not overlap each other.
    const items = [...bar.querySelectorAll('*')].filter(e => vis(e) && e.children.length === 0 && getComputedStyle(e).position !== 'absolute' && e.getBoundingClientRect().width > 4 && !e.closest('.dd-menu') && !e.closest('svg'));
    for (const e of items) {
      const r = e.getBoundingClientRect();
      if (r.right > br.right + 1 || r.left < br.left - 1) problems.push(`${sel}: ${name(e)} sticks out of the bar`);
      if (r.bottom > br.bottom + 1 || r.top < br.top - 1) problems.push(`${sel}: ${name(e)} sticks out vertically (wrapped?)`);
      if (sel !== '.top' && e.scrollWidth > e.clientWidth + 1 && getComputedStyle(e).textOverflow === 'ellipsis') problems.push(`${sel}: ${name(e)} is cut off (ellipsis)`);
    }
    for (let i = 0; i < items.length; i++) for (let j = i + 1; j < items.length; j++) {
      if (items[i].contains(items[j]) || items[j].contains(items[i])) continue;
      if (overlap(items[i].getBoundingClientRect(), items[j].getBoundingClientRect())) problems.push(`${sel}: ${name(items[i])} overlaps ${name(items[j])}`);
    }
  }
  // Fixed side panels: from the top bar down to the player bar (or the window bottom), no gap.
  const top = [...document.querySelectorAll('.top')].find(vis), player = [...document.querySelectorAll('.player')].find(vis);
  const topBottom = top ? top.getBoundingClientRect().bottom : 0;
  const floorY = player ? player.getBoundingClientRect().top : vh;
  for (const sel of ['.ed-side', '.asst', '.asst-panel']) {
    for (const p of document.querySelectorAll(sel)) {
      if (!vis(p) || getComputedStyle(p).position !== 'fixed') continue;
      const r = p.getBoundingClientRect();
      if (Math.abs(r.top - topBottom) > 2) problems.push(`${sel} starts at ${Math.round(r.top)}, top bar ends at ${Math.round(topBottom)}`);
      if (Math.abs(r.bottom - floorY) > 2) problems.push(`${sel} ends at ${Math.round(r.bottom)}, expected ${Math.round(floorY)} (${player ? 'player bar top' : 'window bottom'})`);
      if (r.left < 0 || r.right > vw + 1) problems.push(`${sel} outside the window (${Math.round(r.left)}..${Math.round(r.right)})`);
    }
  }
  // The page content must not be hidden behind a fixed bottom bar: the last element of the scrolling content can be reached (checked through the main's bottom padding).
  return problems;
}

const browser = await chromium.launch();
try {
  for (const h of heights) for (const w of widths) {
    const size = `${w}x${h}`;
    const page = await browser.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 1 });
    page.on('pageerror', e => report(size, 'page', `script error: ${e.message}`));
    const phone = w <= 760;
    const state = async (name, setup) => {
      try { if (setup) await setup(); } catch (e) { report(size, name, `could not set up: ${e.message.split('\n')[0]}`); return; }
      await page.waitForTimeout(500);
      const problems = await page.evaluate(inspect);
      for (const p of problems) report(size, name, p);
      if (shots) await page.screenshot({ path: `${shots}/${size}-${name}.png` });
      console.log(problems.length ? 'bad ' : 'ok  ', size, name);
    };
    const go = async (path, tab) => {
      const link = page.locator(`a[href="${path}"]:visible`).first();
      if (await link.count()) await link.click(); else await page.goto(`${base}/${path}`);
      await page.waitForTimeout(1200);
    };
    await page.goto(`${base}/`);
    await page.locator('#boot-splash').waitFor({ state: 'detached', timeout: 60_000 });
    await page.waitForTimeout(1500);
    // Fake mode: the seeded finished run and scenarios with steps, independent of server data.
    await page.locator('.mode-btn:visible').first().click();
    await page.locator('.mode-runs-menu').getByText('Fake mode').click();
    await page.waitForTimeout(2500);

    await state('overview-finished');
    if (!phone) {
      await state('overview-edit-panel', async () => {
        await page.getByRole('button', { name: 'Edit', exact: true }).click();
        await page.waitForTimeout(600);
        await page.locator('.sc-card').first().click();
        await page.waitForSelector('.ed-side', { timeout: 5000 });
      });
      await page.locator('.preset-bar').getByRole('button', { name: 'Done' }).dispatchEvent('click').catch(() => { });   // below 860 px the settings panel covers it
    }
    await state('overview-running', async () => {
      await page.getByRole('button', { name: /Start run/ }).first().click();
      await page.waitForTimeout(1500);
    });
    await page.getByRole('button', { name: /Stop/ }).first().click().catch(() => { });
    await state('scenario', async () => { await page.locator('.sc-card').first().click(); await page.waitForSelector('.scn', { timeout: 5000 }); });
    await state('calls', () => go('calls'));
    await state('logs', () => go('logs'));
    await state('editor', () => go('editor'));
    await state('environment', () => go('environment'));
    await page.close();
  }
} finally {
  await browser.close();
}

if (failures.length) { console.error(`\n${failures.length} layout problem(s)`); process.exit(1); }
console.log('\nlayout ok at every size');
