// «توثيق التعبئة» (loading documentation): the record appears with the first loading photo, linked to the order and the trip;
// the post-fill photo joins the same record; documentation status and reasons; «طلب إعادة التصوير» (driver notified, only that
// slot unlocked after sending, versions kept); search, filters, totals, Excel; permissions and company isolation.
// Run on a fresh database with the demo seed: API=http://127.0.0.1:5005 node tests/loading-docs.mjs
import { randomUUID } from 'node:crypto';
import { call as rawCall, token, login, B } from './fuauth.mjs';
async function call(...args) { for (let i = 0; ; i++) { const r = await rawCall(...args); if (r.s !== 429 || i > 30) return r; await new Promise(res => setTimeout(res, 3000)); } }
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const P = (t, p, b) => call(t, 'POST', p, b ?? {});
const G = (t, p) => call(t, 'GET', p);
const tag = Date.now().toString(36).slice(-5);
const ksaDate = (d = 0) => new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh' }).format(new Date(Date.now() + d * 864e5));

const admin = await token('admin'), sup = await token('supervisor'), d1 = await token('driver1'), d2 = await token('driver2'), gulf = await token('gulf.admin');
const jpeg = () => Buffer.concat([Buffer.from([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1]), Buffer.from(Array.from({ length: 300 }, () => Math.random() * 256 | 0)), Buffer.from([0xFF, 0xD9])]);
// the API allows 30 uploads per minute per user; pace like tests/fill-report.mjs
const sent = [];
async function pace() { while (sent.length && Date.now() - sent[0] > 61000) sent.shift(); if (sent.length >= 28) { await new Promise(r => setTimeout(r, 61000 - (Date.now() - sent[0]))); return pace(); } sent.push(Date.now()); }
const GPS = { lat: '26.64', lng: '50.16' };
async function photo(tok, id, kind, extra = GPS, { clientId = randomUUID(), capturedAt = new Date().toISOString() } = {}) {
  await pace();
  const f = new FormData(); f.append('file', new Blob([jpeg()], { type: 'image/jpeg' }), 'p.jpg'); f.append('kind', kind); f.append('clientId', clientId);
  if (capturedAt) f.append('capturedAt', capturedAt);
  for (const [k, v] of Object.entries(extra)) f.append(k, v);
  const r = await fetch(`${B}/api/fill-reports/${id}/photos`, { method: 'POST', headers: { authorization: 'Bearer ' + tok }, body: f });
  return { s: r.status, j: await r.json().catch(() => null) };
}
const stage = (id, st, body = {}) => P(d1, `/api/fill-reports/${id}/stages/${st}`, body);
const list = async (tok, qs = '') => (await G(tok, `/api/fill-reports/loading?${qs}`)).j;
const rowOf = async (id, qs = '') => (await list(sup, qs)).items?.find(x => x.id === id);

// ── setup: an order with three trucks' worth of trips for driver1 (Aramco, a station, Maysan yard) ────────────────
const me1 = (await G(d1, '/api/me')).j;
const stations = (await G(admin, '/api/master/stations')).j, customers = (await G(admin, '/api/master/customers')).j;
const aramco = stations.find(s => s.type === 'Aramco'), station = stations.find(s => s.type === 'Station'), yard = stations.find(s => s.type === 'MaysanYard');
const order = (await P(sup, '/api/orders', { customerId: customers[0].id, type: 'Transport', product: 'ديزل', qtyRequested: 36000, priority: 'Scheduled', customerRef: `PO-${tag}` })).j.order;
const dispatch = async (st, n) => (await P(sup, `/api/orders/${order.id}/trips`, { vehicleId: me1.vehicles[0].id, stationId: st.id, sourceRef: `LO-${tag}-${n}`, qty: 12000 })).j;
const tA = await dispatch(aramco, 'A'), tB = await dispatch(station, 'B'), tC = await dispatch(yard, 'C');
ok(order?.no && tA.tripId && tB.tripId && tC.tripId, `order ${order?.no} with trips ${tA.tripNo} (أرامكو), ${tB.tripNo} (محطة), ${tC.tripNo} (حوش ميسان) for driver1`);
const before = await list(sup);

// ── 1. no photo → no record; first photo → the record appears ───────────────────────────────────────────────────
const rA = (await P(d1, '/api/fill-reports/start', { tripId: tA.tripId })).j;
let l = await list(sup, `q=${encodeURIComponent(tA.tripNo)}`);
ok(rA.id && l.total === 0 && (await list(sup)).totals.all === before.totals.all, 'a started report with no photo does not appear in «توثيق التعبئة» («لم تبدأ»)');
let p = await photo(d1, rA.id, 'tank-pre-fill');
let row = await rowOf(rA.id);
ok(p.s === 200 && row?.status === 'AwaitingPostFill' && row.statusLabel === 'بانتظار صورة بعد التعبئة', `first photo (pre-fill) → record appears: «${row?.statusLabel}»`);
ok(row?.orderNo === order.no && row.tripNo === tA.tripNo && row.tripId === tA.tripId, `linked to order no. ${row?.orderNo} and trip no. ${row?.tripNo}`);
ok(row?.driverName === 'سالم العتيبي' && row.plate === me1.vehicles[0].plate, `trip data: driver ${row?.driverName}, plate ${row?.plate}`);
ok(row?.source === 'Aramco' && row.sourceLabel === 'أرامكو' && row.facility === aramco.name, `loading source «${row?.sourceLabel}» · ${row?.facility}`);
ok(row?.product === 'ديزل' && row.qtyRequested === 12000 && row.preFill?.id && !row.postFill && row.preFill.lat === 26.64, 'fuel data and the pre-fill photo with its coordinates; no post-fill photo yet');
ok(row?.loadingDocNo === `LO-${tag}-A` && row.loadingDocNoFrom === 'sourceRef' && row.loadingDocLabel === 'رقم سند أرامكو', `no number typed yet → falls back to the loading order ref ${row?.loadingDocNo}`);

// ── 2. post-fill photo (sent 30 min after it was taken) + optional loading document → same record, «مكتمل» ────────
await stage(rA.id, 'pre-fill');
const taken = new Date(Date.now() - 30 * 60000).toISOString();
p = await photo(d1, rA.id, 'tank-post-fill', GPS, { capturedAt: taken });
const docPhoto = await photo(d1, rA.id, 'loading-doc');
ok(docPhoto.s === 200 && docPhoto.j.kind === 'loading-doc', 'optional «سند التحميل» photo accepted (camera upload, same rules)');
let s = await stage(rA.id, 'post-fill', { loadedQty: 11950, loadingDocNo: `ARM-${tag}` });
ok(s.s === 200 && s.j.loadingDocNo === `ARM-${tag}` && s.j.photos.length === 6 && s.j.optionalPhotos?.[0]?.kind === 'loading-doc' && s.j.optionalPhotos[0].id, 'post-fill stage saves the loading document no.; the doc photo is an optional slot, not one of the 6');
ok(!s.j.missing.some(m => m.includes('سند')), 'the loading document is not in the missing list');
row = await rowOf(rA.id);
l = await list(sup, `q=${encodeURIComponent(tA.tripNo)}`);
ok(l.total === 1 && row?.status === 'Complete' && row.statusLabel === 'مكتمل', `post-fill photo joins the SAME record (${l.total} row, same id) → «${row?.statusLabel}»`);
ok(row?.postFill?.capturedAt && Date.parse(row.postFill.capturedAt) < Date.parse(row.postFill.uploadedAt) && row.postFill.lateMinutes >= 29 && row.postFill.lateMinutes <= 32,
  `capture time kept (not upload time): sent ${row?.postFill?.lateMinutes} min later → «وصلت بعد …»`);
ok(row?.preFill?.lateMinutes == null, 'a photo sent right away has no delay note');
ok(row?.loadedQty === 11950 && row.loadingDocNo === `ARM-${tag}` && row.loadingDocNoFrom === 'driver' && row.loadingDoc?.id, `loaded ${row?.loadedQty} L, Aramco document no. ${row?.loadingDocNo} (typed by the driver) and its image`);

// ── 3. reasons: photo without location, capture older than 48 h ────────────────────────────────────────────────────
const rB = (await P(d1, '/api/fill-reports/start', { tripId: tB.tripId })).j;
await photo(d1, rB.id, 'tank-pre-fill', { locationNote: 'لا توجد إشارة GPS في المكان' });
row = await rowOf(rB.id);
ok(row?.status === 'NeedsReview' && row.statusLabel === 'يحتاج مراجعة' && row.reasons.some(x => x.includes('بدون موقع') && x.includes('GPS')), `photo without location → «يحتاج مراجعة»: ${row?.reasons?.join(' | ')}`);
ok(row?.source === 'Station' && row.sourceLabel === 'محطة' && row.loadingDocLabel === 'رقم فاتورة المحطة', 'station source labelled «محطة», document labelled «رقم فاتورة المحطة»');
const rC = (await P(d1, '/api/fill-reports/start', { tripId: tC.tripId })).j;
await photo(d1, rC.id, 'tank-pre-fill', GPS, { capturedAt: new Date(Date.now() - 3 * 864e5).toISOString() });
row = await rowOf(rC.id);
ok(row?.status === 'NeedsReview' && row.reasons.some(x => x.includes('48 ساعة')) && row.sourceLabel === 'حوش ميسان', `capture 3 days before upload → reason: ${row?.reasons?.[0]} («${row?.sourceLabel}»)`);

// ── 4. retake request on a sent report ───────────────────────────────────────────────────────────────────────────
for (const k of ['tank-arrival', 'meter-before']) await photo(d1, rA.id, k);
await stage(rA.id, 'arrival', { meterBefore: 50000 });
for (const k of ['tank-post-unload', 'meter-after']) await photo(d1, rA.id, k);
await stage(rA.id, 'post-unload', { meterAfter: 61950, tankState: 'فارغ' });
const sub = (await P(d1, `/api/fill-reports/${rA.id}/submit`)).j;
ok(sub.status === 'Submitted', 'report A sent (locked for the driver)');
ok((await P(sup, `/api/fill-reports/${rA.id}/retakes`, { kind: 'tank-post-fill', note: '' })).s === 400, 'retake request without a note refused');
ok((await P(sup, `/api/fill-reports/${rA.id}/retakes`, { kind: 'selfie', note: 'صورة أخرى' })).s === 400, 'unknown photo slot refused');
ok((await P(d1, `/api/fill-reports/${rA.id}/retakes`, { kind: 'tank-post-fill', note: 'أنا السائق' })).s === 403, 'a driver cannot request retakes');
const note = `الصورة مظلمة ولا يظهر مستوى الوقود ${tag}`;
let rq = await P(sup, `/api/fill-reports/${rA.id}/retakes`, { kind: 'tank-post-fill', note });
ok(rq.s === 200 && rq.j.label === 'الصهريج بعد التعبئة', 'supervisor (Fill reports: Edit) requests «إعادة التصوير» of the post-fill photo with a note');
ok((await P(sup, `/api/fill-reports/${rA.id}/retakes`, { kind: 'tank-post-fill', note: 'مرة ثانية' })).s === 400, 'only one open request per photo');
const notes = (await G(d1, '/api/notifications')).j;
const n1 = notes.find(n => n.text.includes(tag));
ok(n1 && n1.text.includes(sub.no) && n1.text.includes(tA.tripNo) && n1.text.includes('الصهريج بعد التعبئة'), 'driver notified with the note, report no. and trip no.: ' + n1?.text);
row = await rowOf(rA.id);
ok(row?.status === 'NeedsReview' && row.reasons[0] === `طلب إعادة تصوير (الصهريج بعد التعبئة): ${note}` && row.openRetakes === 1, 'open request → «يحتاج مراجعة» with the request as reason');
const mineRow = (await G(d1, '/api/fill-reports/mine')).j.find(x => x.tripId === tA.tripId);
ok(mineRow?.report?.retakes?.length === 1 && mineRow.report.retakes[0].note === note && mineRow.report.retakes[0].kind === 'tank-post-fill', 'driver app list (/mine) carries the open request (badge «مطلوب إعادة تصوير»)');
const drvDet = (await G(d1, `/api/fill-reports/${rA.id}`)).j;
ok(drvDet.retakeRequests?.length === 1 && drvDet.retakeRequests[0].label === 'الصهريج بعد التعبئة', 'report detail for the app shows the request on that slot');
ok((await photo(d1, rA.id, 'tank-pre-fill')).s === 400, 'other slots stay locked after sending');
const cid = randomUUID();
p = await photo(d1, rA.id, 'tank-post-fill', GPS, { clientId: cid });
const again = await photo(d1, rA.id, 'tank-post-fill', GPS, { clientId: cid });
ok(p.s === 200 && again.j?.duplicate === true && again.j.id === p.j.id, 'driver retakes THAT slot on the sent report; a repeated upload (same clientId) is not stored twice');
ok((await photo(d1, rA.id, 'tank-post-fill')).s === 400, 'once resolved, the slot is locked again');
let det = (await G(sup, `/api/fill-reports/${rA.id}/loading`)).j;
const post = det.slots.find(x => x.kind === 'tank-post-fill');
const req = det.retakeRequests.find(x => x.kind === 'tank-post-fill');
ok(req?.resolvedAt && req.resolvedAttachmentId === p.j.id && !post.openRetake, 'the upload resolves the request (linked to the new photo)');
ok(post.versions.length === 2 && post.versions[0].current === false && post.versions[1].current && post.versions[1].id === p.j.id && post.versions[1].retakeNote === note && post.versions[0].by && post.versions[1].by
  && Date.parse(post.versions[0].uploadedAt) < Date.parse(post.versions[1].uploadedAt), 'both versions listed oldest → newest, with who/when and the request note; the old photo stays in the history');
ok(det.record.status === 'Complete' && det.record.postFill.id === p.j.id && det.record.id === rA.id, 'the new photo shows in the same record, back to «مكتمل»');
ok((await G(sup, `/api/fill-reports/${rA.id}`)).j.status === 'Submitted', 'the fill report itself keeps its status');
ok((await G(sup, '/api/notifications')).j.some(n => n.text.includes('أعاد السائق') && n.ref === `/loading-docs/${rA.id}`), 'the supervisor who asked is notified, with a link to the record');
ok(det.canRequestRetake === true && det.slots.length === 7, 'detail: all slots (6 + loading doc) with versions; supervisor may request');
// generic: any slot of the report, also after approval
ok((await P(sup, `/api/fill-reports/${rA.id}/approve`, { note: 'مطابق' })).j.status === 'Approved', 'report approved');
ok((await P(sup, `/api/fill-reports/${rA.id}/retakes`, { kind: 'meter-after', note: 'رقم العداد غير واضح' })).s === 200, 'retake requested on another slot (meter after unloading) of an approved report');
row = await rowOf(rA.id);
ok(row?.status === 'NeedsReview' && row.reasons.some(x => x.includes('العداد السري بعد التفريغ')), 'any open request makes the record «يحتاج مراجعة»');
ok((await photo(d1, rA.id, 'meter-after')).s === 200 && (await photo(d1, rA.id, 'meter-before')).s === 400, 'approved report: only the requested slot accepts a photo');
ok((await rowOf(rA.id))?.status === 'Complete', 'resolved → «مكتمل»');

// ── 5. search and filters ─────────────────────────────────────────────────────────────────────────────────────────
for (const [q, what] of [[me1.vehicles[0].plate, 'plate'], ['سالم', 'driver name'], [order.no, 'order no.'], [tB.tripNo, 'trip no.'], [`ARM-${tag}`, 'loading document no.']]) {
  const ids = (await list(sup, `q=${encodeURIComponent(q)}`)).items.map(x => x.id);
  ok(what === 'trip no.' ? ids.length === 1 && ids[0] === rB.id : ids.includes(rA.id), `search by ${what} «${q}»`);
}
ok((await list(sup, 'q=zzzz-none')).total === 0, 'search with no match → empty');
const ids = async (qs) => (await list(sup, `q=${order.no}&${qs}`)).items.map(x => x.id).sort().join();
ok(await ids('source=Aramco') === rA.id && await ids('source=Station') === rB.id && await ids('source=MaysanYard') === rC.id, 'filter by loading source: أرامكو / محطة / حوش ميسان');
ok(await ids('status=NeedsReview') === [rB.id, rC.id].sort().join() && await ids('status=Complete') === rA.id, 'filter by documentation status');
const today = ksaDate();
ok(await ids(`from=${today}&to=${today}`) === [rA.id, rB.id].sort().join(), 'filter by date (KSA, first photo): today → A and B; C was photographed 3 days ago');
ok(await ids(`from=${ksaDate(-3)}&to=${ksaDate(-3)}`) === rC.id && await ids(`to=${ksaDate(-4)}`) === '', 'C falls on the day its first photo was taken');
const lo = await list(sup, `q=${order.no}`);
ok(lo.items.map(x => x.id).join() === [rB.id, rA.id, rC.id].join(), 'newest first by first photo time (A’s post-fill photo was taken 30 min earlier than its upload, so A starts before B)');

// ── 6. totals ────────────────────────────────────────────────────────────────────────────────────────────────────
const rD = (await P(d1, '/api/fill-reports/start', { tripId: (await dispatch(aramco, 'D')).tripId })).j;
await photo(d1, rD.id, 'tank-pre-fill');
const all = await list(sup);
const t = all.totals;
ok(t.all === before.totals.all + 4 && t.awaitingPostFill === before.totals.awaitingPostFill + 1 && t.needsReview === before.totals.needsReview + 2 && t.complete === before.totals.complete + 1,
  `totals: all ${t.all}, awaiting post-fill ${t.awaitingPostFill}, needs review ${t.needsReview}, complete ${t.complete}`);
ok(t.all === t.awaitingPostFill + t.needsReview + t.complete && all.total === t.all, 'totals add up');
const filtered = await list(sup, 'status=AwaitingPostFill');
ok(filtered.total === t.awaitingPostFill && filtered.totals.all === t.all && filtered.items.every(x => x.status === 'AwaitingPostFill'), 'clicking a total filters the list; the totals stay');

// ── 7. Excel ─────────────────────────────────────────────────────────────────────────────────────────────────────
const x = await fetch(`${B}/api/fill-reports/loading?format=xlsx&q=${order.no}`, { headers: { authorization: 'Bearer ' + admin } });
const buf = Buffer.from(await x.arrayBuffer());
ok(x.status === 200 && buf.subarray(0, 2).toString() === 'PK' && /spreadsheetml/.test(x.headers.get('content-type')), `Excel export (${buf.length} bytes, starts with PK)`);

// ── 8. permissions and company isolation ─────────────────────────────────────────────────────────────────────────
ok((await G(d1, '/api/fill-reports/loading')).s === 403 && (await G(d1, `/api/fill-reports/${rA.id}/loading`)).s === 403, 'a driver cannot list the report or open a record');
await P(admin, '/api/users', { username: `ldview.${tag}`, displayName: 'مطلع التوثيق', password: 'Loading-Viewer-Pass-26', role: 'Custom', levels: { FillReports: 'View' } });
await P(admin, '/api/users', { username: `ldops.${tag}`, displayName: 'عمليات بلا تقارير', password: 'Loading-Ops-Pass-2026', role: 'Custom', levels: { Ops: 'Edit' } });
const viewer = (await login(`ldview.${tag}`, 'Loading-Viewer-Pass-26')).token, ops = (await login(`ldops.${tag}`, 'Loading-Ops-Pass-2026')).token;
const vd = await G(viewer, `/api/fill-reports/${rA.id}/loading`);
ok((await G(viewer, '/api/fill-reports/loading')).s === 200 && vd.s === 200 && vd.j.canRequestRetake === false, 'Fill reports: View → sees the report and the record (no retake button)');
const dl = await fetch(`${B}/api/attachments/${vd.j.record.postFill.id}`, { headers: { authorization: 'Bearer ' + viewer } });
ok(dl.status === 200 && dl.headers.get('content-type') === 'image/jpeg', 'Fill reports: View → can open and download the photos');
ok((await P(viewer, `/api/fill-reports/${rA.id}/retakes`, { kind: 'tank-pre-fill', note: 'محاولة' })).s === 403, 'Fill reports: View cannot request a retake (needs Edit)');
ok((await fetch(`${B}/api/fill-reports/loading?format=xlsx`, { headers: { authorization: 'Bearer ' + viewer } })).status === 403, 'Excel needs the Export permission, as in the other reports');
ok((await G(ops, '/api/fill-reports/loading')).s === 403 && (await G(ops, `/api/fill-reports/${rA.id}/loading`)).s === 403
  && (await fetch(`${B}/api/attachments/${vd.j.record.postFill.id}`, { headers: { authorization: 'Bearer ' + ops } })).status === 403, 'a user without Fill reports cannot see the report, a record or its photos');
ok((await G(d2, `/api/fill-reports/${rA.id}`)).s === 403, "another driver cannot open the report");
const gl = await list(gulf);
ok(gl && !gl.items.some(x => x.id === rA.id) && (await G(gulf, `/api/fill-reports/${rA.id}/loading`)).s === 404, 'another company does not see the records (404)');
ok((await P(gulf, `/api/fill-reports/${rA.id}/retakes`, { kind: 'tank-pre-fill', note: 'من شركة أخرى' })).s === 404, 'another company cannot request a retake');
