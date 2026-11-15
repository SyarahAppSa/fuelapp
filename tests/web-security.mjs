// Browser checks of the web dashboard (Playwright/Chromium) against the HTTPS stack:
// tokens never stored where scripts can read them, MFA sign-in works, hostile data renders as text, CSP active.
// Usage: API=https://localhost NODE_TLS_REJECT_UNAUTHORIZED=0 node web-security.mjs   (after e2e/security, which enrol the admin)
import { createRequire } from 'node:module';
import { call, login, nextCode, B, PASS } from './fuauth.mjs';
const { chromium } = createRequire(import.meta.url)('playwright');

let pass = 0, fail = 0;
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); c ? pass++ : fail++; };
const shots = process.env.SHOTS || '.';

// Hostile data created through the API: a trip whose customer name is an XSS payload.
const admin = (await login('admin')).token;
const xss = `<img src=x onerror="window.__xss=1">"><script>window.__xss=2</script>`;
const cust = (await call(admin, 'POST', '/api/master/customers', { name: xss.slice(0, 150), site: '<svg onload=window.__xss=3>' })).j;
const st = (await call(admin, 'GET', '/api/master/stations')).j[0];
const drv = (await call(admin, 'GET', '/api/master/drivers')).j[0];
await call(admin, 'POST', '/api/trips', { customerId: cust.id, stationId: st.id, driverId: drv.id, sourceRef: '<b>bold</b>', product: 'ديزل', qtyRequested: 10 });

const browser = await chromium.launch();
const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 } });
const page = await ctx.newPage();
const dialogs = []; page.on('dialog', d => { dialogs.push(d.message()); d.dismiss() });
const cspViolations = []; page.on('console', m => { if (/Content Security Policy/i.test(m.text())) cspViolations.push(m.text()) });

await page.goto(B + '/login');
await page.fill('#u', 'admin'); await page.fill('#p', PASS); await page.click('button.pri');
await page.waitForSelector('#code');
ok(await page.isVisible('text=التحقق بخطوتين'), 'web sign-in asks the admin for the second step');
ok(await page.inputValue('#code') === '', 'code field starts empty (no value carried over from the password step)');
await page.screenshot({ path: `${shots}/web_mfa.png` });
await page.fill('#code', '000000'); await page.click('button.pri');
await page.waitForSelector('.err');
ok(await page.isVisible('.err'), 'wrong code shows an error, no access');
await page.fill('#code', await nextCode('admin')); await page.click('button.pri');
await page.waitForSelector('.hero', { timeout: 15000 });
ok(true, 'correct code opens the dashboard');

const storage = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }) + document.cookie);
ok(!/eyJ|fu_rt|refresh/i.test(storage), 'no token in localStorage, sessionStorage or document.cookie (readable by scripts)');
const cookies = await ctx.cookies();
const rt = cookies.find(c => c.name === 'fu_rt');
ok(rt && rt.httpOnly && rt.secure && rt.sameSite === 'Strict', 'refresh cookie: HttpOnly, Secure, SameSite=Strict');

await page.reload(); await page.waitForSelector('.hero', { timeout: 15000 });
ok(true, 'page reload keeps the user signed in through the HttpOnly cookie (token re-issued, never stored)');

await page.goto(B + '/trips'); await page.waitForSelector('tbody tr');
await page.waitForTimeout(800);
const flag = await page.evaluate(() => window.__xss);
ok(flag === undefined && dialogs.length === 0, 'XSS payloads in customer/site/reference rendered as plain text (no script ran)');
ok(await page.locator('td', { hasText: '<img src=x' }).count() > 0, 'hostile text visible literally in the table');
await page.screenshot({ path: `${shots}/web_trips_xss.png` });

// inline script injection attempt is blocked by CSP even if some markup slipped through
const cspBlocked = await page.evaluate(() => { try { const s = document.createElement('script'); s.textContent = 'window.__inline=1'; document.body.appendChild(s); } catch { } return window.__inline === undefined; });
ok(cspBlocked, 'Content-Security-Policy blocks inline scripts');

await page.goto(B + '/security'); await page.waitForSelector('text=سجل الأحداث الأمنية');
await page.waitForTimeout(800);
await page.screenshot({ path: `${shots}/web_security.png`, fullPage: true });
ok(await page.locator('tbody tr').count() > 3, 'security page lists sessions, users and security events');

await page.click('button:has-text("خروج")'); await page.waitForURL(/login/);
const after = await (await page.request.post(B + '/api/auth/refresh', { headers: { 'X-FuelUp-Client': 'web' } })).status();
ok(after === 401, 'after logout the refresh cookie no longer works');

await browser.close();
console.log(`\nRESULT: ${pass} passed, ${fail} failed` + (cspViolations.length ? ` (CSP reports: ${cspViolations.length})` : ''));
if (fail) process.exitCode = 1;
