// «تقرير التعبئة»: driver documents a trip in 4 stages / 6 photos; operations search, review, approve, return, correct.
import { randomUUID } from 'node:crypto';
import { call, token, B } from './fuauth.mjs';
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const admin = await token('admin'), d1 = await token('driver1'), d2 = await token('driver2'), gulf = await token('gulf.admin');
const jpeg = () => Buffer.concat([Buffer.from([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1]), Buffer.from(Array.from({ length: 300 }, () => Math.random() * 256 | 0)), Buffer.from([0xFF, 0xD9])]);
// the API allows 30 uploads per minute per user (a real trip has 6); the test paces itself to stay under it
const sent = [];
async function pace() { while (sent.length && Date.now() - sent[0] > 61000) sent.shift(); if (sent.length >= 28) { await new Promise(r => setTimeout(r, 61000 - (Date.now() - sent[0]))); return pace(); } sent.push(Date.now()); }
async function photo(tok, id, kind, clientId = randomUUID(), bytes = jpeg(), extra = {}) {
  await pace();
  const f = new FormData(); f.append('file', new Blob([bytes], { type: 'image/jpeg' }), 'p.jpg'); f.append('kind', kind); f.append('clientId', clientId);
  f.append('capturedAt', new Date().toISOString()); for (const [k, v] of Object.entries(extra)) f.append(k, v);
  const r = await fetch(`${B}/api/fill-reports/${id}/photos`, { method: 'POST', headers: { authorization: 'Bearer ' + tok }, body: f });
  return { s: r.status, j: await r.json().catch(() => null) };
}
const stage = (tok, id, st, body = {}) => call(tok, 'POST', `/api/fill-reports/${id}/stages/${st}`, body);

// a fresh order for driver1 (with his vehicle) per scenario
const me1 = (await call(d1, 'GET', '/api/me')).j;
const cust = (await call(admin, 'GET', '/api/master/customers')).j[0], st = (await call(admin, 'GET', '/api/master/stations')).j[0];
const newTrip = async (qty = 5000) => (await call(admin, 'POST', '/api/trips', { customerId: cust.id, stationId: st.id, driverId: me1.driverId, vehicleId: me1.vehicles[0].id, sourceRef: 'FR-T', product: 'ديزل', qtyRequested: qty })).j.trip;
async function fullRun(tripId, { loaded = 5000, before = 10000, after = 15000, outcome = 'Full', reason, tank = 'فارغ', skip = [] } = {}) {
  const rep = (await call(d1, 'POST', '/api/fill-reports/start', { tripId })).j;
  for (const k of ['tank-pre-fill', 'tank-post-fill', 'tank-arrival', 'meter-before', 'tank-post-unload', 'meter-after'].filter(k => !skip.includes(k))) await photo(d1, rep.id, k, randomUUID(), jpeg(), { lat: '26.4', lng: '50.1' });
  await stage(d1, rep.id, 'pre-fill'); await stage(d1, rep.id, 'post-fill', { loadedQty: loaded });
  await stage(d1, rep.id, 'arrival', { meterBefore: before });
  const pu = await stage(d1, rep.id, 'post-unload', { meterAfter: after, tankState: tank, outcome, outcomeReason: reason });
  return { rep, pu, sub: await call(d1, 'POST', `/api/fill-reports/${rep.id}/submit`) };
}

// ── 1. Happy path, step by step ─────────────────────────────────────────────────────────────────────────────────
const t1 = await newTrip(5000);
const mine = (await call(d1, 'GET', '/api/fill-reports/mine')).j;
ok(mine.some(x => x.tripId === t1.id && x.report === null), 'driver sees his assigned trip in «تقرير التعبئة» before starting');
const r1 = (await call(d1, 'POST', '/api/fill-reports/start', { tripId: t1.id })).j;
ok(r1.no?.startsWith('FR-') && r1.plate && r1.customerName && r1.deliverySite && r1.loadingSite && r1.product === 'ديزل' && r1.quantities.requested === 5000, `«ابدأ التوثيق» creates ${r1.no} with plate ${r1.plate}, customer, sites, product and requested quantity`);
ok(r1.driver.nationalId === '1012345678' && r1.driver.phone === '0551234567', 'driver ID and phone filled automatically from the driver file');
ok((await call(d1, 'POST', '/api/fill-reports/start', { tripId: t1.id })).j.id === r1.id, 'starting twice (retry) returns the same report, no second one');
let s = await stage(d1, r1.id, 'pre-fill');
ok(s.s === 400 && s.j.error === 'صوّر الصهريج قبل التعبئة.', 'missing mandatory photo blocks the stage with a direct message: ' + s.j.error);
ok((await stage(d1, r1.id, 'arrival', { meterBefore: 1 })).j.error === 'أكمل المرحلة السابقة أولًا.', 'stages cannot be skipped');
const cid = randomUUID();
const p1 = await photo(d1, r1.id, 'tank-pre-fill', cid, jpeg(), { locationNote: 'الموقع غير مفعّل في الجهاز' });
const p1again = await photo(d1, r1.id, 'tank-pre-fill', cid);
ok(p1.s === 200 && p1again.j.duplicate === true && p1again.j.id === p1.j.id, 'same photo re-sent after a network loss (same clientId) is recognised, not stored twice');
ok((await photo(d1, r1.id, 'tank-pre-fill', randomUUID(), Buffer.from('<html>not a photo</html>'))).s === 400, 'a non-image file is refused');
ok((await stage(d1, r1.id, 'pre-fill')).s === 200, 'stage «قبل التعبئة» saved');
await photo(d1, r1.id, 'tank-post-fill');
ok((await stage(d1, r1.id, 'post-fill', {})).j.error === 'أدخل الكمية المحمّلة باللتر.', 'loaded quantity required when not in the loading record');
ok((await stage(d1, r1.id, 'post-fill', { loadedQty: 5000 })).s === 200, 'stage «بعد التعبئة» saved with 5,000 L');
await photo(d1, r1.id, 'tank-arrival');
ok((await stage(d1, r1.id, 'arrival', { meterBefore: 82000 })).j.error === 'صوّر قراءة العداد قبل التفريغ.', 'secret meter photo before unloading required');
await photo(d1, r1.id, 'meter-before', randomUUID(), jpeg(), { lat: '26.3', lng: '50.2' });
ok((await stage(d1, r1.id, 'arrival', { meterBefore: 82000, meterSerial: 'SEC-777' })).s === 200, 'arrival saved with meter reading 82,000 and serial');
await photo(d1, r1.id, 'tank-post-unload');
s = await stage(d1, r1.id, 'post-unload', { meterAfter: 86995, tankState: 'فارغ' });
ok(s.j.error === 'صوّر قراءة العداد بعد التفريغ.', 'before sending, the driver is told exactly what is missing: ' + s.j.error);
ok((await call(d1, 'POST', `/api/fill-reports/${r1.id}/submit`)).j.error === 'وثّق مرحلة ما بعد التفريغ.', 'report cannot be sent before the last stage');
await photo(d1, r1.id, 'meter-after');
ok((await stage(d1, r1.id, 'post-unload', { meterAfter: 86995, tankState: 'فارغ' })).s === 200, 'after-unloading stage saved (tank «فارغ», reading 86,995)');
const sub1 = (await call(d1, 'POST', `/api/fill-reports/${r1.id}/submit`)).j;
ok(sub1.status === 'Submitted' && sub1.quantities.delivered === 4995 && sub1.quantities.difference === -5, `sent: delivered = 86,995 − 82,000 = ${sub1.quantities.delivered} L, difference ${sub1.quantities.difference} L within tolerance → «${sub1.status}»`);
ok((await call(d1, 'POST', `/api/fill-reports/${r1.id}/submit`)).j.status === 'Submitted', 'sending again (retry) changes nothing');
ok(sub1.photos.map(p => p.kind).join() === 'tank-pre-fill,tank-post-fill,tank-arrival,meter-before,tank-post-unload,meter-after' && sub1.photos.every(p => p.id), 'six photos, each in its own named slot, in trip order');
ok(sub1.photos[0].locationNote === 'الموقع غير مفعّل في الجهاز' && sub1.photos[3].lat === 26.3, 'location saved when available, reason saved when not');
ok((await photo(d1, r1.id, 'tank-pre-fill')).s === 400, 'after sending, photos are locked');

// ── 2. Operations: search, detail, permissions on personal data ────────────────────────────────────────────────
for (const q of [sub1.no, sub1.plate.split(' ')[0], 'سالم', cust.name.split(' ')[0]])
  ok((await call(admin, 'GET', `/api/fill-reports?q=${encodeURIComponent(q)}`)).j.items.some(x => x.id === r1.id), `operations find the report by «${q}»`);
const det = (await call(admin, 'GET', `/api/fill-reports/${r1.id}`)).j;
ok(det.driver.nationalId === '1012345678' && det.history.length >= 2, 'reviewer sees full driver details and the report history');
const viewer = await call(admin, 'POST', '/api/users', { username: 'fr.viewer', displayName: 'مراجع اطلاع', password: 'Viewer-Route-Check-26', role: 'Custom', levels: { FillReports: 'View' } });
const vt = (await call(null, 'POST', '/api/auth/login', { username: 'fr.viewer', password: 'Viewer-Route-Check-26' })).j.accessToken;
const vdet = (await call(vt, 'GET', `/api/fill-reports/${r1.id}`)).j;
ok(viewer.s === 200 && vdet.driver.nationalId === '••••••5678' && vdet.driver.phone === '••••••4567' && vdet.driver.masked, 'a view-only user sees ID and phone masked (••••5678)');
ok((await call(vt, 'POST', `/api/fill-reports/${r1.id}/approve`, { note: 'x' })).s === 403, 'a view-only user cannot approve');
const photoOk = await fetch(`${B}/api/attachments/${det.photos[3].id}`, { headers: { authorization: 'Bearer ' + admin } });
ok(photoOk.status === 200 && photoOk.headers.get('content-type') === 'image/jpeg', 'reviewer opens the meter photo');
ok((await fetch(`${B}/api/attachments/${det.photos[3].id}`, { headers: { authorization: 'Bearer ' + d2 } })).status === 403, "another driver cannot open the photos");
ok((await call(d2, 'GET', `/api/fill-reports/${r1.id}`)).s === 403, "another driver cannot open the report");
ok((await call(gulf, 'GET', `/api/fill-reports/${r1.id}`)).s === 404, 'another company cannot see it');
ok((await call(d1, 'GET', '/api/fill-reports')).s === 403, 'a driver cannot list everyone’s reports');
ok((await call(admin, 'POST', `/api/fill-reports/${r1.id}/approve`, { note: 'مطابق' })).j.status === 'Approved', 'reviewer approves');

// ── 3. Field cases ───────────────────────────────────────────────────────────────────────────────────────────────
const partial = await fullRun((await newTrip()).id, { before: 20000, after: 22000, outcome: 'Partial', reason: 'خزان العميل امتلأ', tank: 'متبقي 3000 لتر' });
ok(partial.sub.j.status === 'NeedsReview' && /تفريغ جزئي/.test(partial.sub.j.reviewReasons), 'partial unloading → «بحاجة إلى مراجعة» with the reason: ' + partial.sub.j.reviewReasons);
const failedTrip = await newTrip();
const failed = await fullRun(failedTrip.id, { before: 30000, after: undefined, outcome: 'Failed', reason: 'الموقع مغلق ولم يحضر المستلم', tank: 'ممتلئ', skip: ['tank-post-unload', 'meter-after'] });
ok(failed.sub.j.status === 'NeedsReview' && /تعذّر التفريغ/.test(failed.sub.j.reviewReasons) && failed.sub.j.stages.postUnload.tankState === 'ممتلئ', 'failed unloading accepted with the available evidence, tank recorded as «ممتلئ», sent for review');
const badRead = await fullRun((await newTrip()).id, { before: 40000, after: 39000 });
ok(badRead.sub.j.status === 'NeedsReview' && /أقل من/.test(badRead.sub.j.reviewReasons), 'meter reading after < before → review, original readings and photos kept');
const diff = await fullRun((await newTrip()).id, { loaded: 5000, before: 50000, after: 54700 });
ok(diff.sub.j.status === 'NeedsReview' && /الفرق/.test(diff.sub.j.reviewReasons), 'difference of −300 L beyond tolerance → review: ' + diff.sub.j.reviewReasons);

// ── 4. Return to the driver, correction log ────────────────────────────────────────────────────────────────────
ok((await call(admin, 'POST', `/api/fill-reports/${diff.rep.id}/return`, {})).s === 400, 'return needs a specific note');
const ret = (await call(admin, 'POST', `/api/fill-reports/${diff.rep.id}/return`, { note: 'صورة العداد بعد التفريغ غير واضحة، أعد تصويرها' })).j;
ok(ret.status === 'Returned', 'reviewer returns the report to the driver with a note');
ok((await call(d1, 'GET', '/api/notifications')).j.some(n => n.text.includes('غير واضحة')), 'driver is notified with the exact note');
await photo(d1, diff.rep.id, 'meter-after');
await stage(d1, diff.rep.id, 'post-unload', { meterAfter: 54990, tankState: 'فارغ' });
const re = (await call(d1, 'POST', `/api/fill-reports/${diff.rep.id}/submit`)).j;
ok(re.status === 'Submitted' && re.photos.find(p => p.kind === 'meter-after').retakes === 1, 'driver retakes the meter photo, corrects the reading and sends again (old photo kept as history)');
const ed = await call(admin, 'PUT', `/api/fill-reports/${re.id}/fields`, { field: 'meterAfter', value: '54995', reason: 'مطابقة مع صورة العداد المكبّرة' });
const h = ed.j.history.find(x => x.action === 'تعديل قراءة العداد بعد التفريغ');
ok(ed.s === 200 && h && h.oldValue === '54990' && h.newValue === '54995' && h.detail.includes('مطابقة') && h.by && h.at, 'post-send edit logged: field, original value, new value, who, when and why');
ok((await call(admin, 'PUT', `/api/fill-reports/${re.id}/fields`, { field: 'meterAfter', value: '1', reason: '' })).s === 400, 'an edit without a reason is refused');
ok((await call(d1, 'PUT', `/api/fill-reports/${re.id}/fields`, { field: 'meterAfter', value: '1', reason: 'تجربة' })).s === 403, 'a driver cannot edit after sending');
