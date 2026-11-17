// «تقرير التعبئة» from the real driver app to the operations panel, in two halves around the Flutter test
// (mobile/test/live/fill_report_test.dart):
//   node fill-app-flow.mjs prepare  → office side: driver account, truck (with trailer) and secret meter, an assigned trip
//   flutter test test/live/fill_report_test.dart --dart-define=...  → the app documents the 4 stages, part of it offline
//   node fill-app-flow.mjs verify   → office side: the report is in the list, 6 photos once each, readings, review reasons; approve
import { writeFileSync, readFileSync } from 'node:fs';
import { call, token } from './fuauth.mjs';

const STATE = new URL('./.fill-app-flow.json', import.meta.url).pathname;
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const mode = process.argv[2];
const admin = await token('admin');

if (mode === 'prepare') {
  const tag = Date.now().toString(36).slice(-5);
  const meter = (await call(admin, 'POST', '/api/master/meters', { serial: `SM-${tag}`, kind: 'عداد سري' })).j;
  const vehicle = (await call(admin, 'POST', '/api/master/vehicles', { plate: `ت ق ر ${tag.slice(0, 4)}`, trailerPlate: `م ق ط ${tag.slice(0, 4)}`, capacity: 32000, meterId: meter.id })).j;
  const driver = (await call(admin, 'POST', '/api/master/drivers', { name: 'سائق تقرير التعبئة', type: 'SmallTruck', phone: '0551112233', nationalId: '1098765432' })).j;
  const username = `fr.${tag}`, password = `Fill-Report-Route-${tag}9`;
  const u = await call(admin, 'POST', '/api/users', { username, displayName: 'سائق تقرير التعبئة', password, role: 'Driver', driverId: driver.id });
  await call(admin, 'POST', `/api/assignments/vehicles/${vehicle.id}`, { driverId: driver.id });
  const customer = (await call(admin, 'GET', '/api/master/customers')).j[0];
  const station = (await call(admin, 'GET', '/api/master/stations')).j[0];
  const trip = (await call(admin, 'POST', '/api/trips', { customerId: customer.id, stationId: station.id, driverId: driver.id, vehicleId: vehicle.id, sourceRef: `FR-${tag}`, product: 'ديزل', qtyRequested: 20000 })).j.trip;
  ok(u.s === 200 && trip?.no, `prepared: driver ${username}, truck ${vehicle.plate} + trailer, secret meter ${meter.serial}, trip ${trip?.no}`);
  writeFileSync(STATE, JSON.stringify({ username, password, tripId: trip.id, tripNo: trip.no, plate: vehicle.plate, meter: meter.serial }));
  console.log(`DEFINES --dart-define=APP_USER=${username} --dart-define=APP_PASS=${password} --dart-define=TRIP_NO=${trip.no}`);
}

if (mode === 'verify') {
  const st = JSON.parse(readFileSync(STATE, 'utf8'));
  const sup = await token('supervisor');
  const list = (await call(sup, 'GET', `/api/fill-reports?q=${encodeURIComponent(st.tripNo)}`)).j;
  ok(list.total === 1, `operations panel finds the report by trip number (${list.items?.[0]?.no})`);
  const byPlate = (await call(sup, 'GET', `/api/fill-reports?q=${encodeURIComponent(st.plate)}`)).j;
  ok(byPlate.total === 1, 'and by truck plate');
  const r = (await call(sup, 'GET', `/api/fill-reports/${list.items[0].id}`)).j;
  ok(r.status === 'NeedsReview', `status «بحاجة إلى مراجعة» — ${r.reviewReasons}`);
  ok(/جزئي/.test(r.reviewReasons ?? '') && r.stages.postUnload.outcome === 'Partial', 'partial unloading and its reason reached operations');
  ok(r.stages.arrival.meterBefore === 100000 && r.stages.postUnload.meterAfter === 112000 && r.quantities.delivered === 12000, `readings from the app: before ${r.stages.arrival.meterBefore}, after ${r.stages.postUnload.meterAfter}, delivered ${r.quantities.delivered}`);
  ok(r.quantities.loaded === 19500 && r.quantities.difference === -7500, `loaded ${r.quantities.loaded} (typed in the app), difference ${r.quantities.difference}`);
  ok(r.stages.arrival.secretMeterSerial === st.meter && r.trailerPlate, 'secret meter serial and trailer plate linked automatically');
  ok(r.photos.length === 6 && r.photos.every(p => p.id), 'all six photo slots filled');
  ok(r.photos.every(p => p.retakes === 0 || p.kind === 'tank-pre-fill'), 'photos taken offline were uploaded once (no duplicates after sync)');
  ok(r.photos.find(p => p.kind === 'tank-pre-fill').retakes === 1, 'the retaken photo is kept as history (retakes = 1)');
  ok(r.photos.filter(p => p.lat != null).length >= 4 && r.photos.some(p => p.lat == null && /GPS/.test(p.locationNote ?? '')), 'capture location saved, or the driver’s reason when unavailable');
  ok(r.driver.nationalId === '1098765432' && r.driver.phone === '0551112233', 'supervisor (Fill reports: edit) sees the full driver ID and phone');
  ok(r.history.some(h => h.action === 'إرسال تقرير التعبئة'), 'submission recorded in the history');
  const ap = await call(sup, 'POST', `/api/fill-reports/${r.id}/approve`, { note: 'التفريغ الجزئي مؤكد مع العميل' });
  ok(ap.s === 200 && ap.j.status === 'Approved', 'operations approve the report with a note');
}
