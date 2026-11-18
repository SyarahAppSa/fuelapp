// «تقرير التعبئة» from the real driver app to the operations panel, in two halves around the Flutter test
// (mobile/test/live/fill_report_test.dart):
//   node fill-app-flow.mjs prepare  → office side: driver account, truck (with trailer) and secret meter, an assigned trip
//   flutter test test/live/fill_report_test.dart --dart-define=...  → the app documents the 4 stages, part of it offline
//   node fill-app-flow.mjs verify   → office side: the report is in the list, 6 photos once each, readings, review reasons; approve
//
// «توثيق التعبئة» (mobile/test/live/loading_docs_test.dart) runs in one go, the office checks happening while the app test waits:
//   PHOTO=/path/to/real.jpg OUT_DIR=/tmp/somewhere API=http://127.0.0.1:5005 node fill-app-flow.mjs loading
import { writeFileSync, readFileSync, existsSync, mkdtempSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { call, token, B } from './fuauth.mjs';

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

if (mode === 'loading') {
  const photoFile = process.env.PHOTO, outDir = process.env.OUT_DIR || tmpdir();
  if (!photoFile || !existsSync(photoFile)) { console.log('FAIL set PHOTO to a real JPEG (e.g. a phone-sized picture)'); process.exit(1); }
  const tag = Date.now().toString(36).slice(-5);
  const sup = await token('supervisor');
  const driver = (await call(admin, 'POST', '/api/master/drivers', { name: 'سائق توثيق التعبئة', type: 'Aramco', phone: '0553334455', nationalId: '1087654321' })).j;
  const vehicle = (await call(admin, 'POST', '/api/master/vehicles', { plate: `ت و ث ${tag.slice(0, 4)}`, capacity: 32000 })).j;
  const username = `ld.${tag}`, password = `Loading-Docs-Route-${tag}9`;
  await call(admin, 'POST', '/api/users', { username, displayName: 'سائق توثيق التعبئة', password, role: 'Driver', driverId: driver.id });
  await call(admin, 'POST', `/api/assignments/vehicles/${vehicle.id}`, { driverId: driver.id });
  const customer = (await call(admin, 'GET', '/api/master/customers')).j[0];
  const aramco = (await call(admin, 'GET', '/api/master/stations')).j.find(x => x.type === 'Aramco');
  const order = (await call(sup, 'POST', '/api/orders', { customerId: customer.id, type: 'Transport', product: 'ديزل', qtyRequested: 20000, priority: 'Scheduled' })).j.order;
  const trip = (await call(sup, 'POST', `/api/orders/${order.id}/trips`, { vehicleId: vehicle.id, stationId: aramco.id, sourceRef: `AR-${tag}`, qty: 20000 })).j;
  ok(trip.tripNo && order.no, `prepared: driver ${username}, truck ${vehicle.plate}, order ${order.no}, trip ${trip.tripNo} from ${aramco.name}`);
  const dir = mkdtempSync(join(tmpdir(), 'ld-sync-'));
  const docNo = `ARM-LIVE-${tag}`;
  const app = spawn('/opt/flutter/bin/flutter', ['test', 'test/live/loading_docs_test.dart', `--dart-define=API_URL=${B}`, `--dart-define=APP_USER=${username}`, `--dart-define=APP_PASS=${password}`,
    `--dart-define=TRIP_NO=${trip.tripNo}`, `--dart-define=PHOTO=${photoFile}`, `--dart-define=SYNC_DIR=${dir}`, `--dart-define=DOC_NO=${docNo}`],
  { cwd: new URL('../mobile/', import.meta.url).pathname, stdio: ['ignore', 'pipe', 'pipe'] });
  let appOut = ''; app.stdout.on('data', d => { appOut += d; process.stdout.write(String(d).split('\n').filter(l => /✔|EXCEPTION|Expected|Actual|fail|All tests|Some tests/i.test(l)).map(l => '  app: ' + l.trim() + '\n').join('')) });
  app.stderr.on('data', d => { appOut += d });
  const done = new Promise(res => app.on('exit', res));
  const wait = async (n) => { for (let i = 0; i < 1500; i++) { if (existsSync(join(dir, `step${n}`))) return readFileSync(join(dir, `step${n}`), 'utf8'); if (app.exitCode !== null) return null; await new Promise(r => setTimeout(r, 200)); } return null; };
  const answer = (n, good, why) => writeFileSync(join(dir, `go${n}`), good ? 'ok' : `office check ${n} failed: ${why}`);
  const record = async () => (await call(sup, 'GET', `/api/fill-reports/loading?q=${encodeURIComponent(trip.tripNo)}`)).j;

  // 1. only the pre-fill photo has arrived
  if (await wait(1) !== null) {
    const l = await record(), r = l.items?.[0];
    const good = l.total === 1 && r.status === 'AwaitingPostFill' && r.orderNo === order.no && r.tripNo === trip.tripNo && r.preFill?.lat === 24.7136 && !r.postFill;
    ok(good, `pre-fill photo sent from the app → record «${r?.statusLabel}» under order ${r?.orderNo} / trip ${r?.tripNo}`);
    answer(1, good, JSON.stringify(r));
  }
  // 2. post-fill photo taken offline, synced later
  let first;
  const taken = await wait(2);
  if (taken !== null) {
    const l = await record(), r = l.items?.[0];
    first = r;
    const cap = Date.parse(r?.postFill?.capturedAt), up = Date.parse(r?.postFill?.uploadedAt);
    ok(l.total === 1 && r.status === 'Complete', `after sync: still ONE record (${l.total}), «${r?.statusLabel}»`);
    ok(r?.preFill?.id && r?.postFill?.id && r.orderNo === order.no, 'both photos under the same order / record');
    ok(Math.abs(cap - Date.parse(taken)) < 1500 && up - cap >= 7000, `post-fill capturedAt = the moment of capture in the app (${new Date(cap).toISOString()}), ${Math.round((up - cap) / 1000)} s before it reached the server`);
    ok(r?.postFill?.lat === 24.7136 && r.postFill.lng === 46.6753, `coordinates kept: ${r?.postFill?.lat}, ${r?.postFill?.lng}`);
    ok(r?.loadedQty === 19800 && r.loadingDocNo === docNo && r.loadingDocNoFrom === 'driver' && r.loadingDoc?.id, `loaded qty ${r?.loadedQty}, loading document no. ${r?.loadingDocNo} and its photo from the app`);
    const img = await fetch(`${B}/api/attachments/${r.postFill.id}`, { headers: { authorization: 'Bearer ' + sup } });
    const file = join(outDir, 'loading-postfill-from-app.jpg');
    writeFileSync(file, Buffer.from(await img.arrayBuffer()));
    const ksa = new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Riyadh', dateStyle: 'short', timeStyle: 'medium' }).format(new Date(cap));
    ok(img.status === 200, `post-fill photo downloaded to ${file} — its stamp should read ${ksa} (KSA) and 24.71360, 46.67530`);
    answer(2, l.total === 1 && r.status === 'Complete' && up - cap >= 7000, JSON.stringify(r));
  }
  // 3. report sent → the supervisor asks for the post-fill photo again
  if (await wait(3) !== null) {
    const rep = (await call(sup, 'GET', `/api/fill-reports/${first.id}`)).j;
    const rq = await call(sup, 'POST', `/api/fill-reports/${first.id}/retakes`, { kind: 'tank-post-fill', note: 'الصورة غير واضحة؛ أعد تصوير الصهريج بعد التعبئة من الأعلى' });
    ok(['Submitted', 'NeedsReview'].includes(rep.status) && rq.s === 200, `report sent from the app («${rep.status}»); supervisor requests «إعادة التصوير» of the post-fill photo`);
    answer(3, rq.s === 200, JSON.stringify(rq.j));
  }
  // 4. the driver retook it from the app
  if (await wait(4) !== null) {
    const d = (await call(sup, 'GET', `/api/fill-reports/${first.id}/loading`)).j;
    const post = d.slots.find(x => x.kind === 'tank-post-fill');
    const req = d.retakeRequests[0];
    ok(req?.resolvedAt && post.versions.length === 2 && post.versions[1].id === req.resolvedAttachmentId && post.versions[1].retakeNote === req.note, 'the app retook the photo on the locked report: request resolved, 2 versions kept');
    ok(d.record.status === 'Complete' && d.record.id === first.id && d.record.postFill.id === post.versions[1].id, 'the new photo is in the same record, «مكتمل» again');
    answer(4, true);
  }
  const code = await done;
  ok(code === 0, 'flutter live test passed');
  if (code !== 0) console.log(appOut.split('\n').slice(-60).join('\n'));
}
