// Security test suite: attacks the running API (and the database directly) and checks each one is refused.
// Run on a fresh database after e2e.mjs:  API=http://127.0.0.1:5004 node security.mjs
// DB checks use psql: DB_MODE=local (default) or DB_MODE=docker with DB_CONTAINER=<postgres container>.
import { createHmac } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { call, login, token, nextCode, secretOf, totp, B, PASS, authPace } from './fuauth.mjs';

let pass = 0, fail = 0;
const section = (t) => console.log('\n## ' + t);
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); c ? pass++ : fail++; };
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const b64u = (o) => Buffer.from(typeof o === 'string' ? o : JSON.stringify(o)).toString('base64url');
const jwtParts = (t) => t.split('.').map((p, i) => i < 2 ? JSON.parse(Buffer.from(p, 'base64url')) : p);

// ── DB access (as the restricted runtime role, and as the owner role) ──────────────────────────────────────────
const DB = { mode: process.env.DB_MODE || 'local', container: process.env.DB_CONTAINER, name: process.env.DB_NAME || 'fuelup',
  app: [process.env.DB_APP_USER || 'fuelup_app', process.env.DB_APP_PASSWORD || 'fuelup_app_dev'],
  owner: [process.env.DB_OWNER_USER || 'fuelup', process.env.DB_OWNER_PASSWORD || 'fuelup'] };
function sql(role, q) {
  const [user, pw] = DB[role];
  const args = ['-h', DB.mode === 'docker' ? '127.0.0.1' : 'localhost', '-U', user, '-d', DB.name, '-tA', '-v', 'ON_ERROR_STOP=1', '-c', q];
  try {
    const out = DB.mode === 'docker'
      ? execFileSync('docker', ['exec', '-e', 'PGPASSWORD=' + pw, DB.container, 'psql', ...args], { stdio: ['ignore', 'pipe', 'pipe'] })
      : execFileSync('psql', args, { env: { ...process.env, PGPASSWORD: pw }, stdio: ['ignore', 'pipe', 'pipe'] });
    return { ok: true, out: out.toString().trim() };
  } catch (e) { return { ok: false, out: (e.stderr || '').toString().trim() }; }
}

// ════════════════════════════════════════════════════════════════════════════════════════════════════════════════
section('1. Unauthenticated and forged tokens');
for (const p of ['/api/trips', '/api/users', '/api/me', '/api/master/vehicles', '/api/security/events', '/api/reports/fill-operations'])
  ok((await call(null, 'GET', p)).s === 401, `no token → 401 on ${p}`);

const adm = await login('admin');
ok(!!adm.token && secretOf('admin')?.recovery?.length === 8, 'admin had to enrol two-step verification at first sign-in (8 recovery codes issued)');
const admin = adm.token;
const [hdr, pay, sig] = jwtParts(admin);
ok(hdr.alg === 'HS256' && pay.sid && pay.cid && pay.exp - pay.nbf <= 15 * 60, 'access token: HS256, bound to a session (sid) and company (cid), lifetime ≤ 15 min');
ok((await call(b64u({ alg: 'none', typ: 'JWT' }) + '.' + b64u(pay) + '.', 'GET', '/api/me')).s === 401, 'alg=none token rejected');
ok((await call(b64u(hdr) + '.' + b64u({ ...pay, role: 'Admin', sub: '00000000-0000-0000-0000-000000000001' }) + '.' + sig, 'GET', '/api/me')).s === 401, 'tampered payload with original signature rejected');
const forged = b64u(hdr) + '.' + b64u({ ...pay, exp: pay.exp + 99999 });
ok((await call(forged + '.' + createHmac('sha256', 'guessed-secret-key-guessed-secret-key').update(forged).digest('base64url'), 'GET', '/api/me')).s === 401, 'token signed with a guessed key rejected');

section('2. Sign-in: generic errors, two-step verification, replay, recovery codes');
const unknown = await call(null, 'POST', '/api/auth/login', { username: 'no-such-user', password: 'x-wrong-password-1' });
const wrong = await call(null, 'POST', '/api/auth/login', { username: 'supervisor', password: 'x-wrong-password-1' });
ok(unknown.s === 401 && wrong.s === 401 && unknown.j.error === wrong.j.error, 'unknown user and wrong password give the same answer (no username enumeration)');
const sup1 = await call(null, 'POST', '/api/auth/login', { username: 'supervisor', password: PASS });
ok(sup1.s === 200 && !sup1.j.accessToken && (sup1.j.mfaSetupRequired || sup1.j.mfaRequired), 'correct password alone gives no access token to a management account');
ok((await call(sup1.j.mfaToken, 'GET', '/api/me')).s === 401, 'the intermediate MFA ticket cannot be used as an access token');
const supEnroll = await login('supervisor'); const sup = supEnroll.token;
const a2 = await call(null, 'POST', '/api/auth/login', { username: 'admin', password: PASS });
ok(a2.j.mfaRequired === true && !a2.j.accessToken, 'enrolled admin: password step returns only an MFA ticket');
ok((await call(null, 'POST', '/api/auth/mfa', { mfaToken: a2.j.mfaToken, code: '000000' })).s === 401, 'wrong 6-digit code rejected');
const used = totp(secretOf('admin').secret, secretOf('admin').last);
ok((await call(null, 'POST', '/api/auth/mfa', { mfaToken: a2.j.mfaToken, code: used })).s === 401, 'replay of an already used code rejected');
const rc = secretOf('admin').recovery[0];
const viaRc = await call(null, 'POST', '/api/auth/mfa', { mfaToken: a2.j.mfaToken, code: rc });
ok(viaRc.s === 200 && viaRc.j.accessToken, 'one-time recovery code accepted once');
const a3 = await call(null, 'POST', '/api/auth/login', { username: 'admin', password: PASS });
ok((await call(null, 'POST', '/api/auth/mfa', { mfaToken: a3.j.mfaToken, code: rc })).s === 401, 'same recovery code rejected the second time');
const dbUser = sql('owner', "select password_hash, coalesce(mfa_secret_protected,'') from users where username='admin'").out.split('|');
ok(/^\$2[aby]\$12\$/.test(dbUser[0]), 'passwords stored as bcrypt (cost 12): ' + dbUser[0].slice(0, 7) + '…');
ok(dbUser[1].length > 50 && !dbUser[1].includes(secretOf('admin').secret), 'TOTP secret stored encrypted (Data Protection), not in clear');

section('3. Lockout after repeated failures');
for (let i = 0; i < 5; i++) await call(null, 'POST', '/api/auth/login', { username: 'driver2', password: 'x-wrong-password-' + i });
ok((await call(null, 'POST', '/api/auth/login', { username: 'driver2', password: PASS })).s === 401, 'after 5 failures even the correct password is refused (temporary lockout)');
const users = (await call(admin, 'GET', '/api/users')).j;
const d2u = users.find(u => u.username === 'driver2');
ok(d2u.lockoutUntil != null, 'lockout visible to the administrator');
ok((await call(admin, 'POST', `/api/users/${d2u.id}/enable`)).s === 204, 'administrator lifts the lockout');
const d2 = await token('driver2'); ok(!!d2, 'driver2 can sign in after the lockout is lifted');
ok(!JSON.stringify(users).match(/passwordHash|password_hash|mfaSecret|recoveryCode/i), 'user list never returns password hashes, MFA secrets or recovery codes');

section('4. Sessions: revoke, logout, disable, refresh-token theft');
const d1a = await login('driver1'), d1b = await login('driver1');
const sessions = (await call(d1a.token, 'GET', '/api/me/sessions')).j;
ok(sessions.length >= 2 && sessions.filter(s => s.current).length === 1, 'user sees his active sessions (' + sessions.length + ')');
const other = sessions.find(s => !s.current && s.id === jwtParts(d1b.token)[1].sid);
ok((await call(d1a.token, 'POST', `/api/me/sessions/${other.id}/revoke`)).s === 204, 'user ends a session on another device');
ok((await call(d1b.token, 'GET', '/api/me')).s === 401, 'revoked session: its unexpired access token stops working immediately');
ok((await call(null, 'POST', '/api/auth/refresh', { refreshToken: d1b.refresh })).s === 401, 'revoked session: its refresh token is refused');
const r1 = await call(null, 'POST', '/api/auth/refresh', { refreshToken: d1a.refresh });
ok(r1.s === 200 && r1.j.refreshToken !== d1a.refresh, 'refresh token rotates (new one each time)');
await sleep(11000);
ok((await call(null, 'POST', '/api/auth/refresh', { refreshToken: d1a.refresh })).s === 401, 'old refresh token replayed → refused');
ok((await call(r1.j.accessToken, 'GET', '/api/me')).s === 401, 'replay detected as theft → the whole session is revoked (attacker and victim must sign in again)');
const d1c = await login('driver1');
ok((await call(d1c.token, 'POST', '/api/auth/logout')).s === 204 && (await call(d1c.token, 'GET', '/api/me')).s === 401, 'logout kills the access token at once');
const driver1 = await token('driver1');

section('5. Web client: refresh token only in an HttpOnly cookie, CSRF guard');
await authPace();
const web = await fetch(B + '/api/auth/login', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: 'driver1', password: PASS, client: 'web' }) });
const webBody = await web.json(); const cookie = web.headers.get('set-cookie') || '';
ok(webBody.accessToken && !webBody.refreshToken, 'web login: no refresh token in the JSON body (unreachable by scripts)');
ok(/fu_rt=/.test(cookie) && /httponly/i.test(cookie) && /samesite=strict/i.test(cookie) && /path=\/api\/auth/i.test(cookie), 'refresh cookie is HttpOnly, SameSite=Strict, scoped to /api/auth' + (/secure/i.test(cookie) ? ', Secure' : ' (Secure off: plain-HTTP dev)'));
const ck = cookie.split(';')[0];
await authPace();
ok((await fetch(B + '/api/auth/refresh', { method: 'POST', headers: { cookie: ck } })).status === 401, 'cookie refresh without the X-FuelUp-Client header refused (cross-site request forgery guard)');
await authPace();
ok((await fetch(B + '/api/auth/refresh', { method: 'POST', headers: { cookie: ck, 'X-FuelUp-Client': 'web' } })).status === 200, 'cookie refresh with the header accepted');

section('6. Permissions on the server and object-level access (IDOR)');
for (const [m, p, b] of [['GET', '/api/users'], ['GET', '/api/security/events'], ['POST', '/api/master/stations', { name: 'x', type: 'Station' }], ['GET', '/api/assignments/vehicles'], ['POST', '/api/users', { username: 'evil', displayName: 'evil', password: 'Very-Long-Pass-123', role: 'Admin' }]])
  ok((await call(driver1, m, p, b)).s === 403, `driver → 403 on ${m} ${p}`);
const trips1 = (await call(driver1, 'GET', '/api/trips?size=100')).j.items;
const t1 = trips1[0].trip;
ok((await call(d2, 'GET', `/api/trips/${t1.id}`)).s === 403, "driver2 cannot open driver1's trip");
ok((await call(d2, 'GET', `/api/attachments/by-owner/${t1.id}`)).s === 403, "driver2 cannot list driver1's trip attachments");
ok((await call(d2, 'POST', `/api/trips/${t1.id}/corrections`, { field: 'qty', newValue: '1', reason: 'x' })).s === 403, "driver2 cannot request a correction on driver1's trip (fixed IDOR)");
ok((await call(d2, 'POST', `/api/trips/${t1.id}/depart`)).s === 403, "driver2 cannot run steps of driver1's trip");

// a user manager who is not an Admin must not be able to escalate
const meAdmin = (await call(admin, 'GET', '/api/me')).j;
const um = await call(admin, 'POST', '/api/users', { username: 'useradmin', displayName: 'مدير مستخدمين', password: 'First-Pass-For-Ua-2026', role: 'Custom', levels: { Users: 'Edit', Ops: 'View' } });
ok(um.s === 200, 'admin creates a limited user manager');
ok((await call(admin, 'POST', `/api/users/${um.j.id}/password`, { newPassword: 'User-Admin-Pass-2026x' })).s === 204, 'admin resets that user\'s password');
ok((await call(admin, 'PUT', `/api/users/${um.j.id}/permissions`, { username: 'useradmin', displayName: 'مدير مستخدمين', role: 'Custom', levels: { Users: 'Edit', Ops: 'View' } })).s === 204, 'admin edits that user\'s permissions');
const ua = (await login('useradmin', 'User-Admin-Pass-2026x')).token;
ok((await call(ua, 'POST', '/api/users', { username: 'opsboss', displayName: 'مدير تشغيل', password: 'Ops-Boss-Pass-2026x', role: 'Custom', levels: { Ops: 'Edit' } })).s === 403, 'user manager cannot grant a level he does not hold (Ops:Edit)');
ok((await call(ua, 'POST', '/api/users', { username: 'newadmin', displayName: 'مدير جديد', password: 'New-Admin-Pass-2026x', role: 'Admin' })).s === 403, 'user manager cannot create an Admin');
ok((await call(ua, 'PUT', `/api/users/${meAdmin.id}/permissions`, { username: 'admin', displayName: 'مدير المنظومة', role: 'Custom', levels: {} })).s === 403, 'user manager cannot edit an Admin account');
ok((await call(ua, 'POST', `/api/users/${meAdmin.id}/reset-mfa`)).s === 403, 'only an Admin can reset two-step verification');

section('7. Mass assignment and input validation');
const foreignCompany = '11111111-1111-1111-1111-111111111111', forcedId = '22222222-2222-2222-2222-222222222222';
const st = await call(admin, 'POST', '/api/master/stations', { id: forcedId, companyId: foreignCompany, name: 'محطة اختبار', type: 'Station' });
ok(st.s === 200 && st.j.id !== forcedId && st.j.companyId === pay.cid, 'client-sent id/companyId ignored (server sets them)');
for (const [why, b] of [
  ['negative quantity', { customerId: t1.customerId, stationId: t1.stationId, sourceRef: 'V', product: 'ديزل', qtyRequested: -5 }],
  ['absurd quantity', { customerId: t1.customerId, stationId: t1.stationId, sourceRef: 'V', product: 'ديزل', qtyRequested: 1e12 }],
  ['over-long text', { customerId: t1.customerId, stationId: t1.stationId, sourceRef: 'V'.repeat(5000), product: 'ديزل', qtyRequested: 10 }],
]) ok((await call(sup, 'POST', '/api/trips', b)).s === 400, 'rejected: ' + why);
ok((await call(sup, 'GET', '/api/trips?status=Approved%27%20OR%201=1--')).s === 400, 'SQL-injection-like enum value rejected (400)');
ok((await call(sup, 'GET', "/api/reports/fill-operations?from=2024-01-01';DROP TABLE trips;--")).s === 400, 'SQL-injection-like date rejected (400)');
ok((await call(null, 'POST', '/api/auth/login', { username: "admin' OR '1'='1", password: "' OR '1'='1" })).s === 401, "classic ' OR '1'='1 sign-in refused");
const inj = "'; DROP TABLE trips; -- <script>alert(1)</script>";
const cInj = await call(admin, 'POST', '/api/master/customers', { name: inj, site: '<img src=x onerror=alert(1)>' });
ok(cInj.s === 200 && cInj.j.name === inj, 'hostile text stored literally as data (parameterised queries)');
ok(sql('owner', 'select count(*) from trips').ok, 'trips table intact after injection attempts');
const big = await fetch(B + '/api/trips', { method: 'POST', headers: { 'content-type': 'application/json', authorization: 'Bearer ' + sup }, body: JSON.stringify({ sourceRef: 'x'.repeat(2_000_000) }) });
ok(big.status === 413 || big.status === 400, 'request body over 1 MB refused (' + big.status + ')');

section('8. File uploads');
const up = async (tok, bytes, name, type, owner = t1.id, kind = 'receipt') => {
  const f = new FormData(); f.append('file', new Blob([bytes], { type }), name); f.append('ownerType', 'trip'); f.append('ownerId', owner); f.append('kind', kind);
  try {
    const r = await fetch(B + '/api/attachments', { method: 'POST', headers: { authorization: 'Bearer ' + tok }, body: f });
    return { s: r.status, j: await r.json().catch(() => null) };
  } catch { return { s: 413, j: null }; }   // server closed the connection while the oversized body was still being sent
};
const jpeg = Buffer.concat([Buffer.from([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1]), Buffer.alloc(200, 7), Buffer.from([0xFF, 0xD9])]);
ok((await up(driver1, Buffer.from('<html><script>alert(1)</script></html>'), 'photo.jpg', 'image/jpeg')).s === 400, 'HTML disguised as .jpg with image/jpeg type rejected (content checked by signature)');
ok((await up(driver1, Buffer.from('<svg xmlns="http://www.w3.org/2000/svg" onload="alert(1)"/>'), 'x.svg', 'image/svg+xml')).s === 400, 'SVG (can carry scripts) rejected');
ok((await up(driver1, Buffer.from('MZ\x90\x00 fake exe'), 'invoice.pdf', 'application/pdf')).s === 400, 'executable disguised as PDF rejected');
const huge = await up(driver1, Buffer.concat([jpeg, Buffer.alloc(11 * 1024 * 1024)]), 'big.jpg', 'image/jpeg');
ok(huge.s === 413 || huge.s === 400, 'file over 10 MB refused (' + huge.s + ')');
ok((await up(d2, jpeg, 'x.jpg', 'image/jpeg')).s === 403, "driver2 cannot attach files to driver1's trip");
const good = await up(driver1, jpeg, '../../../etc/passwd.jpg', 'image/jpeg');
ok(good.s === 200, 'genuine JPEG accepted');
const dl = await fetch(B + '/api/attachments/' + good.j.id, { headers: { authorization: 'Bearer ' + driver1 } });
ok(dl.status === 200 && dl.headers.get('content-type') === 'image/jpeg' && dl.headers.get('x-content-type-options') === 'nosniff' && /sandbox/.test(dl.headers.get('content-security-policy') || ''), 'download: server-detected type, nosniff, CSP sandbox');
ok((await fetch(B + '/api/attachments/' + good.j.id, { headers: { authorization: 'Bearer ' + d2 } })).status === 403, "driver2 cannot download driver1's file by guessing its id");
const stored = sql('owner', `select file_name || '|' || storage_path from attachments where id='${good.j.id}'`).out;
ok(!stored.includes('..') && !stored.includes('/etc/'), 'path-traversal file name neutralised: ' + stored.split('|')[0]);

section('9. Company isolation (application layer)');
const gulf = await token('gulf.admin');
const gTrips = (await call(gulf, 'GET', '/api/trips?size=100')).j.items;
ok(gTrips.length >= 1 && gTrips.every(x => x.trip.snapshot.customerName === 'عميل شركة الخليج'), 'company B sees only its own trips (' + gTrips.length + ')');
ok((await call(gulf, 'GET', `/api/trips/${t1.id}`)).s === 404, "company B cannot open company A's trip by id (404, not even 403)");
ok((await call(gulf, 'GET', '/api/users')).j.every(u => u.username.startsWith('gulf.')), "company B's user list contains only its users");
ok(!(await call(gulf, 'GET', '/api/master/customers')).j.some(c => c.name === 'مقاولات الجبيل'), "company A's customers invisible to company B");
const aVehicle = (await call(admin, 'GET', '/api/master/vehicles')).j.find(v => v.plate === 'ر ط أ 4821');
ok((await call(gulf, 'POST', `/api/assignments/vehicles/${aVehicle.id}`, { driverId: null })).s === 404, "company B cannot assign company A's vehicle");
ok((await call(gulf, 'POST', '/api/trips', { customerId: t1.customerId, stationId: t1.stationId, sourceRef: 'X', product: 'ديزل', qtyRequested: 10 })).s === 404, "company B cannot create a trip on company A's customer/station");
ok((await up(gulf, jpeg, 'x.jpg', 'image/jpeg')).s === 404, "company B cannot attach to company A's trip");
ok((await call(gulf, 'GET', `/api/attachments/${good.j.id}`)).s === 404, "company B cannot download company A's file");
ok(!(await call(gulf, 'GET', '/api/security/events')).j.some(e => ['admin', 'driver1', 'driver2', 'supervisor'].includes(e.user)), "company B's security log has no company A events");
ok((await call(gulf, 'GET', '/api/master/vehicles')).j.some(v => v.plate === aVehicle.plate), 'same plate exists in both companies (uniqueness is per company)');

section('10. Company isolation and integrity in the database itself');
const cidA = pay.cid, cidB = jwtParts(gulf)[1].cid;
ok(sql('app', 'select count(*) from trips').out === '0', 'runtime DB role without a company set: sees 0 trips (row-level security)');
const aCount = sql('app', `select set_config('app.company_id','${cidA}',false); select count(*) from trips`).out.split('\n').pop();
const aOnly = sql('app', `select set_config('app.company_id','${cidA}',false); select count(*) from trips where company_id <> '${cidA}'`).out.split('\n').pop();
ok(Number(aCount) > 0 && aOnly === '0', `with company A set: ${aCount} trips, none of company B — even with a hand-written query`);
ok(!sql('app', `select set_config('app.company_id','${cidA}',false); update trips set company_id='${cidB}'`).ok, 'moving rows to another company refused by the database (WITH CHECK)');
ok(!sql('app', `select set_config('app.company_id','${cidB}',false); insert into stations(id, company_id, name, type) values (gen_random_uuid(), '${cidA}', 'x', 'Station')`).ok, "inserting into another company's data refused by the database");
ok(/permission denied/.test(sql('app', 'delete from trips').out), 'runtime role cannot DELETE trips (no deletion of operations)');
ok(/permission denied/.test(sql('app', `select set_config('app.company_id','${cidA}',false); update audit set action='x'`).out), 'runtime role cannot UPDATE the audit log');
ok(/permission denied/.test(sql('app', 'create table hack(x int)').out), 'runtime role cannot create tables (no DDL)');
ok(/append-only/.test(sql('owner', `select set_config('app.company_id','${cidA}',false); delete from audit`).out), 'even the owner role cannot delete audit rows (append-only trigger)');
ok(/append-only/.test(sql('owner', `select set_config('app.company_id','${cidA}',false); update meter_readings set reading = 1`).out), 'meter ledger is append-only for every non-superuser role');

section('11. Excel export cannot carry formulas');
const veh = await call(admin, 'POST', '/api/master/vehicles', { plate: 'ت س ت 1', capacity: 5000 });
const me2 = (await call(d2, 'GET', '/api/me')).j;
await call(admin, 'POST', `/api/assignments/vehicles/${veh.j.id}`, { driverId: me2.driverId });
const cust2 = (await call(admin, 'GET', '/api/master/customers')).j.find(c => c.name === 'مصنع الدمام');
const formula = '=HYPERLINK("http://evil.example","اضغط")';
const ft = (await call(sup, 'POST', '/api/trips', { customerId: cust2.id, stationId: t1.stationId, driverId: me2.driverId, vehicleId: veh.j.id, sourceRef: formula, product: 'ديزل', qtyRequested: 100 })).j.trip;
const ld = await call(d2, 'POST', `/api/trips/${ft.id}/load`, { vehicleId: veh.j.id, qty: 100, docNo: '@SUM(1+1)', odometer: 10 });
const xl = Buffer.from(await (await fetch(B + '/api/reports/fill-operations?format=xlsx', { headers: { authorization: 'Bearer ' + sup } })).arrayBuffer());
const xml = execFileSync('python3', ['-c', 'import sys,zipfile,io;z=zipfile.ZipFile(io.BytesIO(sys.stdin.buffer.read()));print("".join(z.read(n).decode() for n in z.namelist() if n.startswith("xl/")))'], { input: xl }).toString();
ok(ld.s === 200 && xml.includes('HYPERLINK') && !/<f[ >]/.test(xml), 'formula-looking text exported as plain text (no executable cell formula)');

section('12. HTTP hardening');
const h = (await fetch(B + '/api/me', { headers: { authorization: 'Bearer ' + admin } })).headers;
ok(h.get('x-content-type-options') === 'nosniff' && h.get('x-frame-options') === 'DENY' && /default-src 'none'/.test(h.get('content-security-policy') || '') && h.get('cache-control') === 'no-store', 'API answers carry nosniff, DENY framing, CSP, no-store');
ok(!/\d/.test(h.get('server') || ''), 'Server header reveals no product version: ' + (h.get('server') || 'absent'));
if (B.startsWith('https:')) ok(/max-age=31536000/.test(h.get('strict-transport-security') || ''), 'HTTPS: Strict-Transport-Security on API answers (browsers refuse plain HTTP afterwards)');
const pre = await fetch(B + '/api/trips', { method: 'OPTIONS', headers: { origin: 'https://evil.example', 'access-control-request-method': 'POST' } });
ok(!pre.headers.get('access-control-allow-origin'), 'CORS: foreign origin not allowed');
const bad = await fetch(B + '/api/trips', { method: 'POST', headers: { 'content-type': 'application/json', authorization: 'Bearer ' + sup }, body: '{"broken": ' });
const badText = await bad.text();
ok(bad.status === 400 && !/at FuelUp|StackTrace|System\.|Exception/.test(badText), 'malformed JSON → 400 without stack traces or internals');

section('13. Security log and alerts');
const ev = (await call(admin, 'GET', '/api/security/events?take=500')).j;
const kinds = new Set(ev.map(e => e.action));
for (const k of ['login.failed', 'account.locked', 'mfa.failed', 'refresh.reuse', 'access.denied', 'upload.rejected', 'mfa.enrolled', 'permissions.changed', 'user.created', 'password.reset', 'session.revoked'])
  ok(kinds.has(k), 'security event recorded: ' + k);
ok(!JSON.stringify(ev).includes(PASS) && !JSON.stringify(ev).includes('x-wrong-password') && !JSON.stringify(ev).includes(secretOf('admin').secret), 'security log contains no passwords, codes or secrets');
const notes = (await call(admin, 'GET', '/api/notifications')).j;
ok(notes.some(n => n.text.startsWith('تنبيه أمني')), 'administrators received in-app security alerts (' + notes.filter(n => n.text.startsWith('تنبيه أمني')).length + ')');

section('14. Password rules');
for (const [why, pw] of [['too short', 'Ab1!'], ['common', 'Password123'], ['contains username', 'driver1-is-me-2026']])
  ok((await call(driver1, 'POST', '/api/me/password', { currentPassword: PASS, newPassword: pw })).s === 400, 'weak new password refused: ' + why);
ok((await call(driver1, 'POST', '/api/me/password', { currentPassword: 'not-my-password', newPassword: 'Truck-Route-Dammam-77' })).s === 400, 'change refused without the current password');

section('15. Brute-force rate limit (per IP)');
await sleep(61000);
let first429 = 0, layer = '';
for (let i = 1; i <= 40 && !first429; i++) {
  const r = await fetch(B + '/api/auth/login', { method: 'POST', headers: { 'content-type': 'application/json', 'X-Forwarded-For': `10.0.0.${i}` }, body: JSON.stringify({ username: 'nobody' + i, password: 'x-wrong-password-z' }) });
  if (r.status === 429) { first429 = i; layer = /json/.test(r.headers.get('content-type') || '') ? 'API' : 'nginx'; }
}
ok(first429 > 0 && first429 <= Number(process.env.AUTH_LIMIT || 20) + 1, `sign-in attempts throttled by ${layer} after ${first429 - 1} requests (spoofed X-Forwarded-For does not bypass it)`);
await sleep(2000);
// Attempts on unknown usernames belong to no company: they are platform-level events (not shown to company admins).
const stuffing = Number(sql('app', "select count(*) from audit where action = 'credential.stuffing'").out);
if (layer === 'API') ok(stuffing > 0, 'many failed sign-ins for unknown users from one IP raised a platform-level credential-stuffing alert');
else console.log(`INFO nginx throttled first (${first429 - 1} attempts reached the API), so the API-side stuffing alert threshold (20) was not reached in this run`);

console.log(`\nRESULT: ${pass} passed, ${fail} failed`);
if (fail) process.exitCode = 1;
