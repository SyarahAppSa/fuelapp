// Browser check (Chromium) of «توثيق التعبئة»: totals that filter, filters and search, before/after photos side by side, lightbox,
// photo download, «طلب إعادة التصوير». Needs the dashboard served by the API (web build copied to backend/src/FuelUp.Api/wwwroot).
//   PHOTO=/path/to/real.jpg OUT_DIR=/tmp/shots API=http://127.0.0.1:5005 node tests/loading-docs-web.mjs
import { createRequire } from 'node:module';
import { randomUUID } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { call, token, nextCode, B, PASS } from './fuauth.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright') } catch { pw = createRequire('/opt/node-tools/')('playwright') }
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const out = process.env.OUT_DIR || tmpdir();
const tag = Date.now().toString(36).slice(-5);
const real = process.env.PHOTO && existsSync(process.env.PHOTO) ? readFileSync(process.env.PHOTO) : null;
const jpeg = () => real ?? Buffer.concat([Buffer.from([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1]), Buffer.from(Array.from({ length: 300 }, () => Math.random() * 256 | 0)), Buffer.from([0xFF, 0xD9])]);
async function photo(tok, id, kind, extra = { lat: '26.64', lng: '50.16' }, capturedAt = new Date().toISOString()) {
  const f = new FormData(); f.append('file', new Blob([jpeg()], { type: 'image/jpeg' }), 'p.jpg'); f.append('kind', kind); f.append('clientId', randomUUID()); f.append('capturedAt', capturedAt);
  for (const [k, v] of Object.entries(extra)) f.append(k, v);
  return (await fetch(`${B}/api/fill-reports/${id}/photos`, { method: 'POST', headers: { authorization: 'Bearer ' + tok }, body: f })).status;
}

// data: one complete operation (Aramco, post-fill sent 40 min after capture) and one awaiting the post-fill photo (station, no GPS)
const admin = await token('admin'), sup = await token('supervisor'), d1 = await token('driver1');
const me1 = (await call(d1, 'GET', '/api/me')).j;
const stations = (await call(admin, 'GET', '/api/master/stations')).j, customers = (await call(admin, 'GET', '/api/master/customers')).j;
const order = (await call(sup, 'POST', '/api/orders', { customerId: customers[0].id, type: 'Transport', product: 'ديزل', qtyRequested: 24000, priority: 'Scheduled' })).j.order;
const trip = async (type, n) => (await call(sup, 'POST', `/api/orders/${order.id}/trips`, { vehicleId: me1.vehicles[0].id, stationId: stations.find(s => s.type === type).id, sourceRef: `W-${tag}-${n}`, qty: 12000 })).j;
const tA = await trip('Aramco', 'A'), tB = await trip('Station', 'B');
const rA = (await call(d1, 'POST', '/api/fill-reports/start', { tripId: tA.tripId })).j;
await photo(d1, rA.id, 'tank-pre-fill');
await call(d1, 'POST', `/api/fill-reports/${rA.id}/stages/pre-fill`, {});
await photo(d1, rA.id, 'tank-post-fill', { lat: '26.641', lng: '50.161' }, new Date(Date.now() - 40 * 60000).toISOString());
await photo(d1, rA.id, 'loading-doc');
await call(d1, 'POST', `/api/fill-reports/${rA.id}/stages/post-fill`, { loadedQty: 11980, loadingDocNo: `ARM-W-${tag}` });
const rB = (await call(d1, 'POST', '/api/fill-reports/start', { tripId: tB.tripId })).j;
await photo(d1, rB.id, 'tank-pre-fill');
const totals = (await call(sup, 'GET', '/api/fill-reports/loading')).j.totals;
ok(rA.id && rB.id, `data: ${order.no} — ${tA.tripNo} complete (Aramco), ${tB.tripNo} awaiting post-fill (station); totals ${JSON.stringify(totals)}`);

const browser = await pw.chromium.launch();
const ctx = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
const page = await ctx.newPage();
const errors = []; page.on('pageerror', e => errors.push(e.message));
await page.goto(B + '/login');
await page.fill('#u', 'supervisor'); await page.fill('#p', PASS); await page.click('button.pri');
await page.waitForSelector('#code'); await page.fill('#code', await nextCode('supervisor')); await page.click('button.pri');
await page.waitForSelector('nav a[href="/loading-docs"]', { timeout: 15000 });
ok(await page.locator('nav a[href="/loading-docs"]').innerText() === 'توثيق التعبئة', 'nav item «توثيق التعبئة»');
await page.click('nav a[href="/loading-docs"]');
await page.waitForSelector('table.ld-table tbody tr');
const num = async (k) => Number((await page.locator(`[data-total="${k}"]`).innerText()).replace(/,/g, ''));
ok(await num('all') === totals.all && await num('awaiting') === totals.incomplete && await num('review') === totals.needsReview, `totals on top: all ${await num('all')}, awaiting ${await num('awaiting')}, needs review ${await num('review')}`);
await page.waitForFunction(() => document.querySelectorAll('.ld-mini img').length >= 3);
await page.screenshot({ path: join(out, 'ld-list.png'), fullPage: true });

await page.click('button.stat.warn');
await page.waitForFunction(n => document.querySelectorAll('table.ld-table tbody tr').length === n, totals.incomplete);
const chips = await page.locator('table.ld-table tbody tr td:last-child .chip').allInnerTexts();
ok(chips.length === totals.incomplete && chips.every(c => c.startsWith('بانتظار صورة')) && await page.locator(`tr[data-id="${rB.id}"]`).count() === 1, 'clicking «غير مكتملة» lists only operations still missing photos');
await page.click('button.stat >> nth=0');
await page.fill('input[name=q]', order.no); await page.selectOption('select[name=source]', 'Aramco'); await page.click('form.filter button.pri');
await page.waitForFunction(() => document.querySelectorAll('table.ld-table tbody tr').length === 1);
ok(await page.locator(`tr[data-id="${rA.id}"]`).count() === 1, 'search by order no. + filter «أرامكو» → the Aramco operation only');
const rowText = await page.locator(`tr[data-id="${rA.id}"]`).innerText();
ok(rowText.includes(order.no) && rowText.includes(tA.tripNo) && rowText.includes('سالم العتيبي') && rowText.includes(`ARM-W-${tag}`) && rowText.includes('بانتظار صورة قبل التفريغ') && rowText.includes('مطلوبة: قبل التفريغ، بعد التفريغ') && rowText.includes('11,980'),
  'row: order no., trip no., driver, loading document no., loaded qty, «بانتظار صورة قبل التفريغ» with the photos still required');

await page.click(`tr[data-id="${rA.id}"]`);
await page.waitForSelector('.ld-photos figure.ld-photo');
await page.waitForFunction(() => document.querySelectorAll('.ld-photos figure.ld-photo .frp-img img').length === 3);
const boxes = await Promise.all(['tank-pre-fill', 'tank-post-fill'].map(k => page.locator(`figure[data-kind="${k}"] .frp-img`).boundingBox()));
ok(Math.abs(boxes[0].y - boxes[1].y) < 2 && Math.abs(boxes[0].x - boxes[1].x) > 200, 'before and after filling side by side (and the loading document next to them)');
const post = await page.locator('figure[data-kind="tank-post-fill"]').innerText();
ok(/وصلت بعد 40 دقيقة/.test(post) && post.includes('26.64100, 50.16100') && post.includes('وقت الالتقاط (السعودية)'), 'post-fill card: KSA capture time, coordinates and «وصلت بعد 40 دقيقة»');
await page.screenshot({ path: join(out, 'ld-record.png'), fullPage: true });

await page.click('figure[data-kind="tank-pre-fill"] .frp-img');
await page.waitForSelector('.lightbox img');
ok(await page.locator('.lightbox').isVisible(), 'photo opens enlarged (lightbox)');
await page.screenshot({ path: join(out, 'ld-lightbox.png') });
const dl1 = page.waitForEvent('download');
await page.click('.lightbox-cap .btn');
const f1 = await dl1;
ok(f1.suggestedFilename().endsWith('.jpg'), 'download from the lightbox: ' + f1.suggestedFilename());
await page.click('.lightbox img'); await page.waitForSelector('.lightbox', { state: 'detached' });
const dl2 = page.waitForEvent('download');
await page.click('figure[data-kind="tank-post-fill"] >> text=تنزيل');
const f2 = await dl2;
const saved = join(out, 'ld-download.jpg'); await f2.saveAs(saved);
ok(readFileSync(saved).subarray(0, 2).toString('hex') === 'ffd8', `download button saves the photo (${f2.suggestedFilename()})`);

await page.click('figure[data-kind="tank-post-fill"] >> text=طلب إعادة التصوير');
await page.fill('figure[data-kind="tank-post-fill"] textarea[name=note]', 'الصورة بعيدة؛ أعد تصوير فتحة الصهريج عن قرب');
await page.click('figure[data-kind="tank-post-fill"] >> text=إرسال الطلب');
await page.waitForSelector('figure[data-kind="tank-post-fill"] .ld-open');
ok((await page.locator('figure[data-kind="tank-post-fill"] .ld-open').innerText()).includes('أعد تصوير فتحة الصهريج'), 'retake request sent from the photo card; the card shows the open request');
await page.waitForSelector('[data-status="NeedsReview"]');
ok(await page.locator('[data-status="NeedsReview"]').innerText() === 'يحتاج مراجعة', 'record now «يحتاج مراجعة»');
ok((await call(d1, 'GET', '/api/notifications')).j.some(n => n.text.includes('أعد تصوير فتحة الصهريج')), 'driver notified');
await page.screenshot({ path: join(out, 'ld-retake.png'), fullPage: true });
ok(errors.length === 0, 'no page errors' + (errors.length ? ': ' + errors.join(' | ') : ''));
await browser.close();
