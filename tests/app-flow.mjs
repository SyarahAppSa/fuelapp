// App ↔ system integration, in two halves around the Flutter test (mobile/test_live/driver_flow_test.dart):
//   node app-flow.mjs prepare  → office side creates a driver account, vehicle, meter (approved baseline) and an order
//   flutter test test/live ... → the real driver app UI signs in and runs the order through every step
//   node app-flow.mjs verify   → office side checks the operation, readings and photos arrived, then approves
import { writeFileSync, readFileSync } from 'node:fs';
import { call, token, PASS, B } from './fuauth.mjs';

const STATE = new URL('./.app-flow.json', import.meta.url).pathname;
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const mode = process.argv[2];
const admin = await token('admin');

if (mode === 'prepare') {
  const tag = Date.now().toString(36).slice(-5);
  const meter = (await call(admin, 'POST', '/api/master/meters', { serial: `APP-${tag}`, kind: 'اختبار التطبيق' })).j;
  const vehicle = (await call(admin, 'POST', '/api/master/vehicles', { plate: `ت ط ب ${tag.slice(0, 4)}`, capacity: 20000, meterId: meter.id })).j;
  const driver = (await call(admin, 'POST', '/api/master/drivers', { name: 'سائق اختبار التطبيق', type: 'SmallTruck' })).j;
  const username = `app.${tag}`, password = `App-Test-Route-${tag}9`;
  const u = await call(admin, 'POST', '/api/users', { username, displayName: 'سائق اختبار التطبيق', password, role: 'Driver', driverId: driver.id });
  await call(admin, 'POST', `/api/assignments/vehicles/${vehicle.id}`, { driverId: driver.id });
  const base = await call(admin, 'POST', `/api/meters/${meter.id}/baseline`, { reading: 50000 });
  const customer = (await call(admin, 'GET', '/api/master/customers')).j[0];
  const station = (await call(admin, 'GET', '/api/master/stations')).j[0];
  const trip = (await call(admin, 'POST', '/api/trips', { customerId: customer.id, stationId: station.id, driverId: driver.id, vehicleId: vehicle.id, sourceRef: `APP-${tag}`, product: 'ديزل', qtyRequested: 4000 })).j.trip;
  ok(u.s === 200 && base.s === 204 && trip?.no, `prepared: driver account ${username}, vehicle ${vehicle.plate}, meter ${meter.serial} (baseline 50,000 approved), order ${trip?.no}`);
  writeFileSync(STATE, JSON.stringify({ username, password, tripId: trip.id, tripNo: trip.no, meterId: meter.id }));
  console.log(`DEFINES --dart-define=APP_USER=${username} --dart-define=APP_PASS=${password} --dart-define=TRIP_NO=${trip.no}`);
}

if (mode === 'verify') {
  const st = JSON.parse(readFileSync(STATE, 'utf8'));
  const t = (await call(admin, 'GET', `/api/trips/${st.tripId}`)).j;
  const trip = t.view.trip, rec = t.view.reconciliation;
  ok(trip.status === 'AwaitingApproval', `order ${trip.no} completed from the app → ${t.view.statusLabel}`);
  ok(rec.loaded === 4000 && rec.delivered === 4000 && rec.receipt === 4000, `quantities sent by the app reached the system: loaded ${rec.loaded}, delivered ${rec.delivered}, receipt ${rec.receipt}`);
  const ledger = (await call(admin, 'GET', `/api/meters/${st.meterId}/ledger`)).j;
  ok(ledger.last === 58000 && ledger.readings.some(r => r.reading === 54000), `meter ledger updated from the app: ${ledger.readings.map(r => r.reading).reverse().join(' → ')}`);
  ok(!ledger.readings.some(r => r.reading === 49000), 'the reading below the last approved one (typed in the app) was never recorded');
  const files = (await call(admin, 'GET', `/api/attachments/by-owner/${st.tripId}`)).j;
  const kinds = new Set(files.map(f => f.kind));
  ok(['load-doc', 'meter-start', 'meter-end', 'seals', 'unload-meter-before', 'unload-meter-after', 'receipt'].every(k => kinds.has(k)), `photos taken in the app are attached to the operation (${files.length} files: ${[...kinds].join(', ')})`);
  ok(files.every(f => f.contentType === 'image/jpeg' && f.fromGallery === false), 'all photos verified as JPEG and marked as fresh camera captures');
  const audit = t.audit.map(a => a.action);
  const steps = ['بدء التنفيذ واعتماد التحميل', 'بدء الرحلة', 'الوصول للتفريغ', 'فحص الأختام وبدء التفريغ', 'تسجيل التسليم'];
  ok(steps.every(s => audit.includes(s)), `every app step recorded in the audit trail: ${steps.join(' ← ')}`);
  const sup = await token('supervisor');
  const ap = await call(sup, 'POST', `/api/trips/${st.tripId}/approve`, { note: 'اعتماد بعد اختبار التطبيق' });
  ok(ap.s === 200 && ap.j.trip.status === 'Approved', 'supervisor approves the operation from the office side');
  const notes = (await call((await (await fetch(B + '/api/auth/login', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ username: st.username, password: st.password }) })).json()).accessToken, 'GET', '/api/notifications')).j;
  ok(Array.isArray(notes) && notes.some(n => n.text.includes(st.tripNo)), 'the driver received the order notification in the app inbox');
}
void PASS;
