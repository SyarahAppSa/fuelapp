// Load test through nginx + TLS, like real clients.
// Scenario (default = 2× the expected load of ~50 drivers + ~10 office users):
//   DRIVERS virtual drivers: refresh the app every 5 s (me, orders, notifications) and each runs one full order
//   (load, depart, arrive, seals photo, unload, deliver with photo) during the test;
//   OFFICE virtual office users: dashboard, orders list and fill report every 3 s.
// Then a short BURST phase at 3× drivers. Reports latency percentiles, throughput and errors per request type.
// All virtual users come from one machine, so per-IP sign-in/API limits must be raised for this test only.
// Usage: AUTH_BUDGET=100000 API=https://localhost NODE_TLS_REJECT_UNAUTHORIZED=0 DRIVERS=100 OFFICE=20 SECONDS=180 node loadtest.mjs
import { writeFileSync } from 'node:fs';
import { call, token, B } from './fuauth.mjs';

const DRIVERS = +(process.env.DRIVERS || 100), OFFICE = +(process.env.OFFICE || 20), SECONDS = +(process.env.SECONDS || 180), BURST = +(process.env.BURST_SECONDS || 60);
const stats = {}; let phase = 'steady';
const rec = (name, ms, ok) => { const k = `${phase}|${name}`; (stats[k] ??= { t: [], err: 0 }); stats[k].t.push(ms); if (!ok) stats[k].err++; };
async function req(tok, method, path, body, name, form) {
  const t0 = performance.now();
  try {
    const r = await fetch(B + path, { method, headers: { authorization: 'Bearer ' + tok, ...(form ? {} : { 'content-type': 'application/json' }) }, body: form ?? (body ? JSON.stringify(body) : undefined) });
    await r.arrayBuffer();
    rec(name, performance.now() - t0, r.ok); return r;
  } catch { rec(name, performance.now() - t0, false); return null; }
}
const sleep = (ms) => new Promise(r => setTimeout(r, ms));
const jitter = (ms) => ms * (0.7 + Math.random() * 0.6);

// ── setup: drivers with accounts, vehicles, a station and a customer ─────────────────────────────────────────────
const admin = await token('admin');
const tag = Date.now().toString(36).slice(-4);
const st = (await call(admin, 'POST', '/api/master/stations', { name: `محطة الحمل ${tag}`, type: 'Station' })).j;
const cu = (await call(admin, 'POST', '/api/master/customers', { name: `عميل الحمل ${tag}`, site: 'موقع الحمل' })).j;
const total = DRIVERS * 3;   // enough accounts for the burst phase
console.log(`setup: ${total} drivers…`);
const drivers = [];
for (let i = 0; i < total; i += 10) {
  drivers.push(...await Promise.all(Array.from({ length: Math.min(10, total - i) }, async (_, k) => {
    const n = i + k;
    const d = (await call(admin, 'POST', '/api/master/drivers', { name: `سائق حمل ${n}`, type: 'SmallTruck' })).j;
    const v = (await call(admin, 'POST', '/api/master/vehicles', { plate: `ح م ل ${tag}${n}`, capacity: 20000 })).j;
    await call(admin, 'POST', `/api/assignments/vehicles/${v.id}`, { driverId: d.id });
    const pw = `Load-Test-Route-${tag}-${n}x`;
    await call(admin, 'POST', '/api/users', { username: `load.${tag}.${n}`, displayName: `سائق حمل ${n}`, password: pw, role: 'Driver', driverId: d.id });
    const tok = (await call(null, 'POST', '/api/auth/login', { username: `load.${tag}.${n}`, password: pw })).j.accessToken;
    return { d, v, tok };
  })));
}
const officeTok = await token('supervisor');
console.log(`setup done: ${drivers.filter(x => x.tok).length} signed-in drivers`);
const jpeg = () => new Blob([Buffer.concat([Buffer.from([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1]), Buffer.from(Array.from({ length: 150_000 }, () => Math.random() * 256 | 0)), Buffer.from([0xFF, 0xD9])])], { type: 'image/jpeg' });

async function driverLoop(x, until, withOrder) {
  let order = null;
  if (withOrder) {
    const r = await req(officeTok, 'POST', '/api/trips', { customerId: cu.id, stationId: st.id, driverId: x.d.id, vehicleId: x.v.id, sourceRef: 'LOAD', product: 'ديزل', qtyRequested: 3000 }, 'office: create order');
    order = r?.ok ? (await (await fetch(B + '/api/trips?size=1', { headers: { authorization: 'Bearer ' + x.tok } })).json()).items[0]?.trip.id : null;
  }
  const steps = order ? [
    ['load', { vehicleId: x.v.id, qty: 3000, docNo: 'L1', odometer: 1000 }], ['depart'], ['arrive', { location: 'موقع الحمل' }],
    ['photo'], ['unload', { sealsOk: true }], ['deliver', { received: 3000, leftInTank: 0, returned: 0, receiptNo: 'R1', hasMoreStops: false }]] : [];
  while (performance.now() < until) {
    await req(x.tok, 'GET', '/api/me', null, 'driver: profile');
    await req(x.tok, 'GET', '/api/trips?size=100', null, 'driver: my orders');
    await req(x.tok, 'GET', '/api/notifications', null, 'driver: notifications');
    const s = steps.shift();
    if (s?.[0] === 'photo') {
      const f = new FormData(); f.append('file', jpeg(), 'seal.jpg'); f.append('ownerType', 'trip'); f.append('ownerId', order); f.append('kind', 'seals');
      await req(x.tok, 'POST', '/api/attachments', null, 'driver: photo upload 150 KB', f);
    } else if (s) await req(x.tok, 'POST', `/api/trips/${order}/${s[0]}`, s[1] ?? {}, `driver: ${s[0]}`);
    await sleep(jitter(5000));
  }
}
async function officeLoop(until) {
  while (performance.now() < until) {
    await req(officeTok, 'GET', '/api/dashboard/summary', null, 'office: dashboard');
    await req(officeTok, 'GET', '/api/trips?page=1&size=20', null, 'office: orders page');
    await req(officeTok, 'GET', '/api/reports/fill-operations', null, 'office: fill report');
    await sleep(jitter(3000));
  }
}

const t0 = performance.now();
console.log(`steady: ${DRIVERS} drivers + ${OFFICE} office for ${SECONDS}s`);
let until = performance.now() + SECONDS * 1000;
await Promise.all([...drivers.slice(0, DRIVERS).map(x => driverLoop(x, until, true)), ...Array.from({ length: OFFICE }, () => officeLoop(until))]);
const steadySecs = (performance.now() - t0) / 1000;
phase = 'burst';
console.log(`burst: ${DRIVERS * 3} drivers + ${OFFICE} office for ${BURST}s`);
const t1 = performance.now(); until = performance.now() + BURST * 1000;
await Promise.all([...drivers.map(x => driverLoop(x, until, false)), ...Array.from({ length: OFFICE }, () => officeLoop(until))]);
const burstSecs = (performance.now() - t1) / 1000;

const pct = (a, p) => { const s = [...a].sort((x, y) => x - y); return s[Math.min(s.length - 1, Math.floor(p / 100 * s.length))] ?? 0; };
const rows = Object.entries(stats).map(([k, v]) => { const [ph, name] = k.split('|'); return { phase: ph, name, n: v.t.length, err: v.err, p50: pct(v.t, 50), p95: pct(v.t, 95), p99: pct(v.t, 99), max: Math.max(...v.t) }; });
const sum = (ph) => rows.filter(r => r.phase === ph).reduce((a, r) => ({ n: a.n + r.n, err: a.err + r.err }), { n: 0, err: 0 });
const out = { drivers: DRIVERS, office: OFFICE, seconds: SECONDS, burstSeconds: BURST, steady: { ...sum('steady'), rps: sum('steady').n / steadySecs }, burst: { ...sum('burst'), rps: sum('burst').n / burstSecs, drivers: DRIVERS * 3 }, rows };
writeFileSync(process.env.OUT || 'loadtest-result.json', JSON.stringify(out, null, 1));
console.log(`\n${'phase'.padEnd(7)} ${'request'.padEnd(30)} ${'count'.padStart(6)} ${'errors'.padStart(6)} ${'p50'.padStart(6)} ${'p95'.padStart(6)} ${'p99'.padStart(6)} (ms)`);
for (const r of rows) console.log(`${r.phase.padEnd(7)} ${r.name.padEnd(30)} ${String(r.n).padStart(6)} ${String(r.err).padStart(6)} ${r.p50.toFixed(0).padStart(6)} ${r.p95.toFixed(0).padStart(6)} ${r.p99.toFixed(0).padStart(6)}`);
console.log(`\nsteady: ${out.steady.n} requests, ${out.steady.rps.toFixed(1)} req/s, errors ${out.steady.err}`);
console.log(`burst:  ${out.burst.n} requests, ${out.burst.rps.toFixed(1)} req/s, errors ${out.burst.err}`);
