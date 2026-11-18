// Fleet documents (وثائق المركبات والسائقين): default types, statuses, report (search, filters, order, Excel), copies and who may see them,
// daily expiry alerts (once per document/expiry/offset, link to the record, fleet manager + admins), renewal with history, corrections,
// configurable mandatory types blocking dispatch, company isolation.
// Run on a fresh database with the demo seed: API=http://127.0.0.1:5005 node tests/documents.mjs
import { call as rawCall, token, login, B } from './fuauth.mjs';
async function call(...args) { for (let i = 0; ; i++) { const r = await rawCall(...args); if (r.s !== 429 || i > 30) return r; await new Promise(res => setTimeout(res, 3000)); } }
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const P = (t, p, b) => call(t, 'POST', p, b ?? {});
const G = (t, p) => call(t, 'GET', p);
const tag = Date.now().toString(36).slice(-5);
const ksaToday = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh' }).format(new Date());
const ksaDay = (d) => { const x = new Date(ksaToday + 'T12:00:00Z'); x.setUTCDate(x.getUTCDate() + d); return x.toISOString().slice(0, 10); };

const admin = await token('admin'), sup = await token('supervisor'), d1 = await token('driver1'), gulf = await token('gulf.admin');
const vehicles = (await G(admin, '/api/master/vehicles')).j, drivers = (await G(admin, '/api/master/drivers')).j;
const V1 = vehicles.find(v => v.plate === 'ر ط أ 4821'), V2 = vehicles.find(v => v.plate === 'ب ح د 1307');
const dr2 = drivers.find(d => d.name === 'خالد الدوسري');
const users = (await G(admin, '/api/users')).j;
const supUser = (users.items ?? users).find(u => u.username === 'supervisor');

ok((await P(admin, '/api/users', { username: `docview.${tag}`, displayName: 'مطلع التشغيل', password: 'Docs-Viewer-Pass-2026', role: 'Custom', levels: { Ops: 'View' } })).s === 200, 'admin creates an operations reader without the Documents section');
const reader = (await login(`docview.${tag}`, 'Docs-Viewer-Pass-2026')).token;

// ── types and settings ────────────────────────────────────────────────────────────────────────────────────────────
let meta = (await G(sup, '/api/documents/meta')).j;
const T = Object.fromEntries(meta.types.map(t => [t.name, t]));
ok(['الفحص الدوري', 'كرت التشغيل', 'التأمين', 'استمارة المركبة', 'رخصة القيادة'].every(n => T[n]), 'five default document types seeded');
ok(T['التأمين'].fields.map(f => f.key).join() === 'number,issuer,issueDate,expiry,image' && T['رخصة القيادة'].appliesTo === 'driver' && T['رخصة القيادة'].fields.some(f => f.key === 'category' && f.label === 'الفئة'), 'insurance: policy no., insurer, coverage start/end, copy; licence: category, on the driver file');
ok(['التأمين', 'استمارة المركبة', 'رخصة القيادة'].every(n => T[n].mandatoryForDispatch) && !T['الفحص الدوري'].mandatoryForDispatch, 'mandatory for dispatch by default: registration, insurance, driving licence');
ok(meta.offsets.join() === '30,7,1,0' && meta.warnDays === 30, 'default alert offsets 30, 7, 1 and on the expiry day');
let r = await call(sup, 'PUT', '/api/documents/settings', { offsets: [30, 7, 1, 0], fleetManagerUserId: supUser.id });
ok(r.s === 204, 'supervisor (Documents: Edit) designates the «مسؤول الأسطول»');
ok((await call(reader, 'PUT', '/api/documents/settings', { offsets: [5] })).s === 403, 'reader cannot change alert settings');
r = await P(sup, '/api/documents/types', { name: `شهادة سلامة الصهريج ${tag}`, appliesTo: 'vehicle', fields: [{ key: 'number', label: 'رقم الشهادة', required: true }, { key: 'image', label: 'صورة الشهادة', required: false }], mandatoryForDispatch: false });
const tankType = r.j;
ok(r.s === 200 && tankType.fields.length === 2, 'a new document type is added from settings (fields chosen, no expiry date)');

// ── register ─────────────────────────────────────────────────────────────────────────────────────────────────────
const insExpiry = ksaDay(7), licExpiry = ksaDay(-10);
r = await P(sup, '/api/documents', { typeId: T['التأمين'].id, ownerId: V2.id, number: `POL-${tag}`, issueDate: ksaDay(-358), expiry: insExpiry });
ok(r.s === 400 && r.j.error === 'أدخل «شركة التأمين».', 'missing required field named exactly: ' + r.j.error);
r = await P(sup, '/api/documents', { typeId: T['التأمين'].id, ownerId: V2.id, number: `POL-${tag}`, issuer: 'التعاونية للتأمين', issueDate: ksaDay(-358), expiry: insExpiry });
const ins = r.j;
ok(r.s === 200 && ins.id && ins.versionId, 'insurance of truck V2 registered, expiring in 7 days');
r = await P(sup, '/api/documents', { typeId: T['رخصة القيادة'].id, ownerId: dr2.id, number: `LIC-${tag}`, category: 'نقل ثقيل', issueDate: ksaDay(-3000), expiry: licExpiry });
const lic = r.j;
ok(r.s === 200, 'driving licence of خالد الدوسري registered, expired 10 days ago');
ok((await P(sup, '/api/documents', { typeId: T['رخصة القيادة'].id, ownerId: V2.id, number: 'X', expiry: licExpiry })).s === 404, 'a driver document cannot be attached to a truck');
r = await P(sup, '/api/documents', { typeId: T['التأمين'].id, ownerId: V2.id, number: 'DUP', issuer: 'x', expiry: ksaDay(100) });
ok(r.s === 400 && r.j.error.includes('تجديد الوثيقة'), 'second insurance for the same truck refused → use renewal: ' + r.j.error);
r = await P(sup, '/api/documents', { typeId: T['الفحص الدوري'].id, ownerId: V2.id, number: `INS-${tag}`, issueDate: ksaDay(-5), expiry: ksaDay(-6) });
ok(r.s === 400 && r.j.error.includes('بعد تاريخ الإصدار'), 'expiry before issue date refused');
await P(sup, '/api/documents', { typeId: T['الفحص الدوري'].id, ownerId: V2.id, number: `INS-${tag}`, issueDate: ksaDay(-5), expiry: ksaDay(360) });
await P(sup, '/api/documents', { typeId: tankType.id, ownerId: V2.id, number: `TS-${tag}` });
ok((await P(reader, '/api/documents', { typeId: T['كرت التشغيل'].id, ownerId: V2.id, number: '1', expiry: ksaDay(10) })).s === 403, 'reader cannot register documents');
ok((await P(d1, '/api/documents', { typeId: T['كرت التشغيل'].id, ownerId: V1.id, number: '1', expiry: ksaDay(10) })).s === 403, 'driver cannot register documents');

// ── report ───────────────────────────────────────────────────────────────────────────────────────────────────────
let rep = (await G(sup, '/api/documents')).j;
const row = (id) => rep.rows.find(x => x.id === id);
ok(row(ins.id)?.statusLabel === 'قريبة من الانتهاء' && row(ins.id).daysLeft === 7 && row(ins.id).ownerName === V2.plate, 'insurance «قريبة من الانتهاء», 7 days left, shows the plate');
ok(row(lic.id)?.statusLabel === 'منتهية' && row(lic.id).daysLeft === -10 && row(lic.id).ownerName === dr2.name, 'licence «منتهية», shows the driver name');
ok(rep.rows.find(x => x.typeName === tankType.name)?.statusLabel === 'تاريخ الانتهاء غير مسجّل', 'document without expiry: «تاريخ الانتهاء غير مسجّل»');
ok(rep.rows[0].id === lic.id && rep.rows[1].id === ins.id && rep.rows[rep.rows.length - 1].expiry === null, 'sorted nearest expiry first, rows without a date last');
ok(rep.summary.expired === 1 && rep.summary.soon === 1 && rep.summary.noDate === 1, 'summary counts expired / soon / no date');
ok((await G(sup, `/api/documents?q=${encodeURIComponent('خالد')}`)).j.rows.every(x => x.ownerName === dr2.name), 'search by driver name');
ok((await G(sup, `/api/documents?q=${encodeURIComponent('1307')}`)).j.rows.length === 3, 'search by plate');
ok((await G(sup, `/api/documents?typeId=${T['التأمين'].id}`)).j.rows.every(x => x.typeName === 'التأمين'), 'filter by document type');
ok((await G(sup, '/api/documents?status=expired')).j.rows.map(x => x.id).join() === lic.id, 'filter by status');
const exp = (await G(sup, '/api/documents/expiring?days=30')).j;
ok(exp.expired.length === 1 && exp.expiring.some(x => x.id === ins.id), 'expired and expiring within 30 days across trucks and drivers');
const x = await fetch(B + '/api/documents?format=xlsx', { headers: { authorization: 'Bearer ' + sup } }); const buf = Buffer.from(await x.arrayBuffer());
ok(x.status === 200 && buf.slice(0, 2).toString() === 'PK', 'Excel export ' + buf.length + ' bytes');
ok((await G(reader, '/api/documents')).s === 200, 'operations reader sees dates and statuses');
ok((await G(d1, '/api/documents')).s === 403, 'driver cannot open the documents report');

// ── copies: uploaded per version, visible only with the Documents section ─────────────────────────────────────────
const jpg = Buffer.concat([Buffer.from([0xFF, 0xD8, 0xFF, 0xE0, 0, 16, 0x4A, 0x46, 0x49, 0x46, 0, 1]), Buffer.from(tag), Buffer.from([0xFF, 0xD9])]);
const upload = async (tok, versionId) => {
  const fd = new FormData(); fd.append('file', new Blob([jpg], { type: 'image/jpeg' }), 'policy.jpg'); fd.append('ownerType', 'docversion'); fd.append('ownerId', versionId); fd.append('kind', 'document');
  const res = await fetch(B + '/api/attachments', { method: 'POST', headers: { authorization: 'Bearer ' + tok }, body: fd });
  return { s: res.status, j: await res.json().catch(() => ({})) };
};
const up = await upload(sup, ins.versionId);
ok(up.s === 200 && up.j.id, 'policy copy attached to the insurance');
ok((await upload(reader, ins.versionId)).s === 403, 'reader cannot attach copies');
let det = (await G(sup, `/api/documents/${ins.id}`)).j;
ok(det.canSeeCopies && det.versions[0].files.length === 1 && det.document.hasCopy, 'document record lists its copy');
const rdet = (await G(reader, `/api/documents/${ins.id}`)).j;
ok(rdet.canSeeCopies === false && rdet.versions[0].files === null && rdet.document.expiry === insExpiry, 'reader sees the dates but not the copies');
ok((await fetch(`${B}/api/attachments/${up.j.id}`, { headers: { authorization: 'Bearer ' + reader } })).status === 403, 'reader cannot download the copy (403)');
ok((await fetch(`${B}/api/attachments/${up.j.id}`, { headers: { authorization: 'Bearer ' + sup } })).status === 200, 'Documents viewer downloads the copy');

// ── daily alerts ─────────────────────────────────────────────────────────────────────────────────────────────────
ok((await P(sup, '/api/documents/run-alerts')).s === 403, 'only an administrator runs the check by hand');
const before = (await G(sup, '/api/notifications')).j.length;
r = await P(admin, '/api/documents/run-alerts');
const insAlert = r.j.alerts?.find(a => a.documentId === ins.id);
ok(r.s === 200 && insAlert?.offset === 7 && insAlert.link === `/documents/${ins.id}` && insAlert.recipients === 2, `insurance alert sent once (7-day step) to admin + fleet manager → ${insAlert?.link}`);
ok(r.j.alerts.some(a => a.documentId === lic.id && a.offset === 0), 'expired licence alert sent');
let notes = (await G(sup, '/api/notifications')).j;
const n = notes.filter(n => n.ref === `/documents/${ins.id}`);
ok(n.length === 1 && n[0].text.includes('«التأمين»') && n[0].text.includes(V2.plate) && n[0].text.includes(insExpiry), 'fleet manager notification: document name, plate, expiry date and link: ' + n[0]?.text);
ok((await G(admin, '/api/notifications')).j.some(n => n.ref === `/documents/${lic.id}` && n.text.includes(dr2.name)), 'admin notified of the expired licence with the driver name');
r = await P(admin, '/api/documents/run-alerts');
notes = (await G(sup, '/api/notifications')).j;
ok(r.j.sent === 0 && notes.length === before + 2, 'second run sends nothing (no duplicates)');

// ── renewal ──────────────────────────────────────────────────────────────────────────────────────────────────────
const newExpiry = ksaDay(372);
r = await P(sup, `/api/documents/${ins.id}/renew`, { number: `POL-${tag}-2`, issuer: 'التعاونية للتأمين', issueDate: ksaDay(-1), expiry: ksaDay(3) });
ok(r.s === 400 && r.j.error.includes('بعد الحالي'), 'renewal must move the expiry forward');
r = await P(sup, `/api/documents/${ins.id}/renew`, { number: `POL-${tag}-2`, issuer: 'التعاونية للتأمين', issueDate: ksaDay(7), expiry: newExpiry, note: 'تجديد سنوي' });
ok(r.s === 200 && r.j.version === 2, 'insurance renewed for one more year (version 2)');
ok((await upload(sup, r.j.versionId)).s === 200, 'renewed copy attached');
det = (await G(sup, `/api/documents/${ins.id}`)).j;
ok(det.document.statusLabel === 'سارية' && det.document.expiry === newExpiry && det.document.number === `POL-${tag}-2`, 'after renewal: «سارية» with the new number and expiry');
ok(det.versions.length === 2 && det.versions[1].number === `POL-${tag}` && det.versions[1].expiry === insExpiry && det.versions[1].files.length === 1 && det.versions[0].current, 'previous version and its copy kept in the history');
r = await P(admin, '/api/documents/run-alerts');
ok(!r.j.alerts.some(a => a.documentId === ins.id), 'alert cycle restarts from the new expiry: no alert now');
r = await call(sup, 'PUT', `/api/documents/${ins.id}`, { number: `POL-${tag}-2A`, issuer: 'التعاونية للتأمين', issueDate: ksaDay(7), expiry: newExpiry });
ok(r.s === 400, 'correction without a reason refused');
r = await call(sup, 'PUT', `/api/documents/${ins.id}`, { number: `POL-${tag}-2A`, issuer: 'التعاونية للتأمين', issueDate: ksaDay(7), expiry: newExpiry, reason: 'خطأ في إدخال رقم البوليصة' });
det = (await G(sup, `/api/documents/${ins.id}`)).j;
ok(r.s === 200 && det.history.some(h => h.action === 'تصحيح بيانات الوثيقة' && h.oldValue.includes(`POL-${tag}-2"`) && h.newValue.includes(`POL-${tag}-2A`)), 'correction audited with reason, old and new values');
ok(['تسجيل وثيقة', 'إرفاق نسخة الوثيقة', 'تجديد الوثيقة'].every(a => det.history.some(h => h.action === a)), 'history: registration, copy, renewal');

// ── readiness: mandatory types (configurable) ────────────────────────────────────────────────────────────────────
const C = (await G(admin, '/api/master/customers')).j[0], st = (await G(admin, '/api/master/stations')).j[0];
const order = (await P(sup, '/api/orders', { customerId: C.id, type: 'Transport', product: 'ديزل', qtyRequested: 3000, priority: 'Scheduled' })).j.order;
r = await P(sup, `/api/orders/${order.id}/trips`, { vehicleId: V2.id, stationId: st.id, sourceRef: `D-${tag}`, qty: 3000 });
ok(r.s === 400 && r.j.error === `لا يمكن إسناد الرحلة: وثيقة «رخصة القيادة» للسائق ${dr2.name} منتهية بتاريخ ${licExpiry}. للإسناد رغم ذلك اكتب سبب التجاوز.`, 'expired licence of the truck\'s driver refuses dispatch: ' + r.j.error);
const lt = T['رخصة القيادة'];
await call(sup, 'PUT', `/api/documents/types/${lt.id}`, { ...lt, mandatoryForDispatch: false });
r = await P(sup, `/api/orders/${order.id}/trips`, { vehicleId: V2.id, stationId: st.id, sourceRef: `D-${tag}`, qty: 3000 });
ok(r.s === 200 && r.j.warnings.some(w => w.includes('رخصة القيادة')), 'licence made non-mandatory in settings → dispatch allowed with a warning');
await call(sup, 'PUT', `/api/documents/types/${lt.id}`, { ...lt, mandatoryForDispatch: true });

// ── company isolation ────────────────────────────────────────────────────────────────────────────────────────────
ok((await G(gulf, `/api/documents/${ins.id}`)).s === 404, 'other company cannot open the document');
ok(!(await G(gulf, '/api/documents')).j.rows.some(x => x.id === ins.id), 'other company does not see it in the report');
ok((await P(gulf, `/api/documents/${ins.id}/renew`, { number: 'X', issuer: 'x', expiry: ksaDay(900) })).s === 404, 'other company cannot renew it');
ok(!(await G(gulf, '/api/notifications')).j.some(n => n.ref === `/documents/${ins.id}`), 'other company admin received no alert for it');
