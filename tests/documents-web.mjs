// Browser check (Chromium): a document expiry alert in the bell opens that document's record. Needs the dashboard served by the API
// (web build copied to backend/src/FuelUp.Api/wwwroot). API=http://127.0.0.1:5005 node tests/documents-web.mjs
import { createRequire } from 'node:module';
import { call, token, nextCode, B, PASS } from './fuauth.mjs';
const require = createRequire(import.meta.url);
let pw; try { pw = require('playwright') } catch { pw = createRequire('/opt/node-tools/')('playwright') }
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const ksaToday = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh' }).format(new Date());
const ksaDay = (d) => { const x = new Date(ksaToday + 'T12:00:00Z'); x.setUTCDate(x.getUTCDate() + d); return x.toISOString().slice(0, 10); };

const admin = await token('admin'), sup = await token('supervisor');
const V1 = (await call(admin, 'GET', '/api/master/vehicles')).j.find(v => v.plate === 'ر ط أ 4821');
const meta = (await call(sup, 'GET', '/api/documents/meta')).j;
const card = meta.types.find(t => t.name === 'كرت التشغيل');
const supId = meta.users.find(u => u.username === 'supervisor').id;
await call(sup, 'PUT', '/api/documents/settings', { offsets: meta.offsets, fleetManagerUserId: supId });
let r = await call(sup, 'POST', '/api/documents', { typeId: card.id, ownerId: V1.id, number: 'TGA-WEB-1', expiry: ksaDay(1) });
const docId = r.j.id ?? (await call(sup, 'GET', `/api/documents?ownerId=${V1.id}&typeId=${card.id}`)).j.rows[0].id;
await call(admin, 'POST', '/api/documents/run-alerts');
const note = (await call(sup, 'GET', '/api/notifications')).j.find(n => n.ref === `/documents/${docId}`);
ok(!!note, 'operating card expiring tomorrow → alert for the fleet manager: ' + note?.text);

const browser = await pw.chromium.launch();
const page = await (await browser.newContext({ viewport: { width: 1440, height: 900 } })).newPage();
const errors = []; page.on('pageerror', e => errors.push(e.message));
await page.goto(B + '/login');
await page.fill('#u', 'supervisor'); await page.fill('#p', PASS); await page.click('button.pri');
await page.waitForSelector('#code'); await page.fill('#code', await nextCode('supervisor')); await page.click('button.pri');
await page.waitForSelector('button.bell', { timeout: 15000 });
ok(await page.locator('button.bell .badge').count() === 1, 'bell shows unread notifications');
await page.click('button.bell');
const item = page.locator('.bell-item', { hasText: 'TGA' }).or(page.locator('.bell-item', { hasText: 'كرت التشغيل' })).first();
await item.waitFor({ timeout: 10000 });
await item.click();
await page.waitForURL(`**/documents/${docId}`, { timeout: 10000 });
await page.waitForSelector('h1:has-text("كرت التشغيل")', { timeout: 10000 });
ok(page.url().endsWith(`/documents/${docId}`), 'clicking the alert opens /documents/' + docId);
ok(await page.locator(`text=${V1.plate}`).count() >= 1 && await page.locator('text=TGA-WEB-1').count() >= 1, 'the record shows the truck and the document number');
ok(await page.locator('.chip', { hasText: 'قريبة من الانتهاء' }).count() >= 1, 'status «قريبة من الانتهاء» on the record');
ok(await page.locator('button:has-text("تجديد الوثيقة")').count() === 1, 'renewal button available to the fleet manager');
ok(errors.length === 0, 'no page errors ' + errors.join(' | '));
await browser.close();
