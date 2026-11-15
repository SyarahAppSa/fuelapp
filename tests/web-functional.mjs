// Office workflow through the real web dashboard (Chromium): master data → user → assignment → order → handover,
// then checks the driver sees the order (web → server → mobile API) and that a driver cannot reach office pages.
// Usage: API=https://localhost NODE_TLS_REJECT_UNAUTHORIZED=0 node web-functional.mjs   (admin enrolled by e2e/security runs)
import { createRequire } from 'node:module';
import { call, nextCode, B, PASS } from './fuauth.mjs';
const { chromium } = createRequire(import.meta.url)('playwright');

let pass = 0, fail = 0;
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); c ? pass++ : fail++; };
const shots = process.env.SHOTS || '.';
const tag = Date.now().toString(36).slice(-4);

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();
const errors = []; page.on('pageerror', e => errors.push(e.message));
// waits for THIS action's outcome: a success banner containing `expect`, or any error banner
const okBanner = async (what, expect = 'تمت الإضافة') => {
  const sel = `.okmsg:has-text("${expect}"), .err`;
  try { await page.waitForSelector(sel, { timeout: 15000 }); } catch { await page.screenshot({ path: `${shots}/fail_${Date.now()}.png` }); }
  const t = (await page.locator(sel).last().textContent().catch(() => '')) ?? '';
  ok(t.includes(expect), `${what}: ${t.trim() || 'no answer'}`);
};

// sign in (two-step)
await page.goto(B + '/login');
await page.fill('#u', 'admin'); await page.fill('#p', PASS); await page.click('button.pri');
await page.waitForSelector('#code'); await page.fill('#code', await nextCode('admin')); await page.click('button.pri');
await page.waitForSelector('.hero', { timeout: 15000 });
for (const l of ['الرحلات', 'الإسناد', 'العدادات', 'البيانات الأساسية', 'المستخدمون والصلاحيات', 'الأمان والجلسات'])
  ok(await page.locator('nav a', { hasText: l }).count() === 1, `admin menu has «${l}»`);

// master data
await page.click('nav a:has-text("البيانات الأساسية")');
await page.click('button:has-text("مواقع التعبئة")');
await page.fill('input[name=name]', `محطة اختبار ${tag}`); await page.selectOption('select[name=type]', 'MaysanYard'); await page.click('button:has-text("إضافة موقع")');
await okBanner('station added from the web');
ok(await page.locator('td', { hasText: `محطة اختبار ${tag}` }).count() === 1, 'new station listed');
await page.click('button:has-text("العدادات")');
await page.fill('input[name=serial]', `WEB-${tag}`); await page.click('button:has-text("إضافة عداد")'); await okBanner('meter added');
await page.click('button:has-text("المركبات")');
await page.fill('input[name=plate]', `و ب ي ${tag}`); await page.fill('input[name=capacity]', '18000');
await page.selectOption('select[name=meterId]', { label: `WEB-${tag}` }); await page.click('button:has-text("إضافة مركبة")'); await okBanner('vehicle added with its meter');
await page.click('button:has-text("السائقون")');
await page.fill('input[name=name]', `سائق الويب ${tag}`); await page.fill('input[name=phone]', '0500000000'); await page.click('button:has-text("إضافة سائق")'); await okBanner('driver added');
await page.click('button:has-text("العملاء والوجهات")');
await page.fill('input[name=name]', `عميل الويب ${tag}`); await page.fill('input[name=site]', 'الدمام — موقع الاختبار'); await page.click('button:has-text("إضافة عميل")'); await okBanner('customer added');
// server-side validation surfaces in the page
await page.click('button:has-text("المركبات")');
await page.fill('input[name=plate]', `و ب ي ${tag}`); await page.fill('input[name=capacity]', '1000'); await page.click('button:has-text("إضافة مركبة")');
await page.waitForSelector('.err'); ok(/مسجلة مسبقًا/.test(await page.locator('.err').textContent()), 'duplicate plate refused by the server and explained in Arabic');
await page.screenshot({ path: `${shots}/web_master.png` });

// user for the new driver
await page.click('nav a:has-text("المستخدمون والصلاحيات")');
const uname = `web.drv.${tag}`, upass = `Web-Driver-Route-${tag}7`;
await page.fill('input[name=username]', uname); await page.fill('input[name=displayName]', `سائق الويب ${tag}`); await page.fill('input[name=password]', upass);
await page.selectOption('select[name=role]', 'Driver'); await page.selectOption('select[name=driverId]', { label: `سائق الويب ${tag}` });
await page.click('button:has-text("إنشاء مستخدم")'); await okBanner('driver account created from the web', 'أُنشئ المستخدم');
await page.fill('input[name=username]', `weak.${tag}`); await page.fill('input[name=displayName]', 'اختبار'); await page.fill('input[name=password]', 'Password123');
await page.click('button:has-text("إنشاء مستخدم")'); await page.waitForSelector('.err');
ok(/شائعة/.test(await page.locator('.err').textContent()), 'common password refused with a clear message');

// assignments
await page.click('nav a:has-text("الإسناد")');
await page.selectOption(`select[aria-label="سائق المركبة و ب ي ${tag}"]`, { label: `سائق الويب ${tag}` }); await okBanner('vehicle assigned to the driver', 'تم تحديث إسناد');
await page.selectOption(`select[aria-label="السائق الافتراضي لـ عميل الويب ${tag}"]`, { label: `سائق الويب ${tag}` }); await okBanner('default driver set for the customer', 'السائق الافتراضي');
await page.waitForTimeout(800);
ok(await page.locator(`tr:has-text("و ب ي ${tag}") td:nth-child(2)`).textContent() === `سائق الويب ${tag}`, 'vehicle row now shows the assigned driver (saved on the server, not just a message)');
ok(await page.locator(`tr:has-text("عميل الويب ${tag}") td:nth-child(3)`).textContent() === `سائق الويب ${tag}`, 'customer row now shows its default driver');
await page.screenshot({ path: `${shots}/web_assign.png` });

// new order with no driver chosen → the customer's default driver gets it
await page.click('nav a:has-text("الرحلات")');
await page.click('button:has-text("طلب نقل جديد")');
await page.selectOption('select[name=customerId]', { label: `عميل الويب ${tag} — الدمام — موقع الاختبار` });
await page.selectOption('select[name=stationId]', { label: `محطة اختبار ${tag}` });
await page.fill('input[name=sourceRef]', `WEB-ORD-${tag}`); await page.fill('input[name=qtyRequested]', '6000');
await page.click('button:has-text("إنشاء الطلب")');
await page.waitForSelector(`td:has-text("سائق الويب ${tag}"), .err`, { timeout: 15000 }).catch(async () => page.screenshot({ path: `${shots}/fail_order.png` }));
if (await page.locator('.err').count()) { ok(false, 'order creation: ' + await page.locator('.err').textContent()); }
ok(true, 'order created from the web, listed with the default driver');
await page.screenshot({ path: `${shots}/web_new_order.png` });

// the driver (mobile API) sees the order and its notification
const dl = await call(null, 'POST', '/api/auth/login', { username: uname, password: upass });
const dTok = dl.j.accessToken;
const mine = (await call(dTok, 'GET', '/api/trips')).j.items;
ok(mine.length === 1 && mine[0].trip.sourceRef === `WEB-ORD-${tag}` && mine[0].trip.vehicleId == null, 'driver app API lists the web-created order for that driver only');
ok((await call(dTok, 'GET', '/api/notifications')).j.some(n => n.text.includes(mine[0].trip.no)), 'driver got the new-order notification');
const me = (await call(dTok, 'GET', '/api/me')).j;
ok(me.vehicles.some(v => v.plate === `و ب ي ${tag}`), 'driver app shows the vehicle assigned on the web');

// handover from the drawer to another driver
await page.click(`tr:has-text("سائق الويب ${tag}")`);
await page.waitForSelector('.drawer');
await page.selectOption('select[name=toDriverId]', { index: 1 }); await page.fill('.drawer input[name=note]', 'إجازة السائق');
await page.click('.drawer button:has-text("نقل")'); await okBanner('order handed over to another driver from the drawer', 'نُقل الطلب');
ok((await call(dTok, 'GET', '/api/trips')).j.items.length === 0, 'after the handover the first driver no longer sees the order');
await page.keyboard.press('Escape');

// a driver signing in to the web sees no office pages, and direct links are refused by the server
const p2 = await (await browser.newContext({ ignoreHTTPSErrors: true })).newPage();
await p2.goto(B + '/login'); await p2.fill('#u', uname); await p2.fill('#p', upass); await p2.click('button.pri');
await p2.waitForSelector('nav');
ok(await p2.locator('nav a').count() === 1, 'driver web menu shows only «الأمان والجلسات»');
await p2.goto(B + '/users'); await p2.waitForTimeout(1500);
ok(await p2.locator('td').count() === 0, 'driver opening /users directly gets no data (server returns 403)');

ok(errors.length === 0, 'no JavaScript errors in the pages' + (errors.length ? ': ' + errors.join(' / ') : ''));
await browser.close();
console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
if (fail) process.exitCode = 1;
