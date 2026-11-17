// Attendance: own check-in/out, double punches, server clock, manager view, corrections with reason, permissions, company isolation, Excel.
import { call, token, B } from './fuauth.mjs';
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const admin = await token('admin'), d1 = await token('driver1'), d2 = await token('driver2'), gulf = await token('gulf.admin');

const r1 = await call(d1, 'POST', '/api/attendance/check-in', { lat: 26.4, lng: 50.1 });
ok(r1.s === 200 && !r1.j.checkOutAt && Math.abs(Date.parse(r1.j.checkInAt) - Date.now()) < 60_000, 'driver checks in; time comes from the server clock');
ok((await call(d1, 'POST', '/api/attendance/check-in', {})).s === 400, 'second check-in while one is open is refused');
const both = await Promise.all([call(d2, 'POST', '/api/attendance/check-in', {}), call(d2, 'POST', '/api/attendance/check-in', {})]);
ok(both.map(x => x.s).sort().join(',') === '200,400', 'two simultaneous taps open only one period (database guard)');
ok((await call(d1, 'POST', '/api/attendance/check-in', { checkInAt: '2020-01-01T08:00:00Z' })).s === 400, 'client cannot choose its own time (field ignored; still refused as already open)');
const me = (await call(d1, 'GET', '/api/attendance/me')).j;
ok(me.open && me.records.length === 1, 'driver sees his open period');
const out = await call(d1, 'POST', '/api/attendance/check-out', { note: 'نهاية الدوام' });
ok(out.s === 200 && out.j.checkOutAt && out.j.status === 'مكتمل', 'driver checks out');
ok((await call(d1, 'POST', '/api/attendance/check-out', {})).s === 400, 'check-out without an open period is refused');
ok((await call(d1, 'GET', '/api/attendance')).s === 403, 'a driver cannot see other employees');
ok((await call(d1, 'PUT', `/api/attendance/${r1.j.id}`, { checkInAt: new Date(Date.now() - 9 * 3600e3).toISOString(), checkOutAt: new Date().toISOString(), reason: 'تعديل ذاتي' })).s === 403, 'a driver cannot edit his own record');

const team = (await call(admin, 'GET', '/api/attendance')).j;
ok(team.records.length >= 2 && team.summary.some(s => s.name === 'سالم العتيبي'), `manager sees the team (${team.records.length} records, ${team.summary.length} people)`);
const bad = await call(admin, 'PUT', `/api/attendance/${r1.j.id}`, { checkInAt: new Date().toISOString(), checkOutAt: new Date(Date.now() - 3600e3).toISOString(), reason: 'x' });
ok(bad.s === 400, 'correction with check-out before check-in (or no real reason) refused');
const inAt = new Date(Date.now() - 8 * 3600e3).toISOString(), outAt = new Date(Date.now() - 3600e3).toISOString();
const fix = await call(admin, 'PUT', `/api/attendance/${r1.j.id}`, { checkInAt: inAt, checkOutAt: outAt, reason: 'نسي تسجيل الحضور صباحًا' });
ok(fix.s === 200 && fix.j.corrected && fix.j.minutes === 420, 'manager corrects a record (7 h) and it is marked as corrected');
const audit = await call(admin, 'GET', '/api/security/events');   // security log is separate; correction lives in the business audit
ok(audit.s === 200, 'audit endpoints reachable for the manager');
ok((await call(gulf, 'GET', '/api/attendance')).j.records.every(r => r.name !== 'سالم العتيبي'), "another company's manager does not see these records");
ok((await call(gulf, 'PUT', `/api/attendance/${r1.j.id}`, { checkInAt: inAt, checkOutAt: outAt, reason: 'محاولة' })).s === 404, "another company cannot edit them (404)");
const xl = await fetch(B + '/api/attendance?format=xlsx', { headers: { authorization: 'Bearer ' + admin } });
ok(xl.status === 200 && /spreadsheetml/.test(xl.headers.get('content-type') || ''), 'Excel export of attendance');
