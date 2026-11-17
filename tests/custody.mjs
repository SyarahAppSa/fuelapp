// Custody & truck spare parts: duplicate serials, double install, issue rules, replacement fate, driver change, hand-over/accept,
// stock never negative (also under concurrency), permissions, company isolation, reports + Excel, append-only history.
// Run on a fresh database with the demo seed: API=http://127.0.0.1:5005 node tests/custody.mjs
import { call as rawCall, token, login, B } from './fuauth.mjs';
// the API limits requests per user per minute: wait and retry on 429 instead of failing a check
async function call(...args) { for (let i = 0; ; i++) { const r = await rawCall(...args); if (r.s !== 429 || i > 30) return r; await new Promise(res => setTimeout(res, 3000)); } }
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const P = (t, p, b) => call(t, 'POST', p, b ?? {});
const G = (t, p) => call(t, 'GET', p);
const tag = Date.now().toString(36).slice(-5).toUpperCase();

const admin = await token('admin'), d1 = await token('driver1'), d2 = await token('driver2'), gulf = await token('gulf.admin');
const drivers = (await G(admin, '/api/master/drivers')).j, vehicles = (await G(admin, '/api/master/vehicles')).j;
const dr1 = drivers.find(d => d.name === 'سالم العتيبي'), dr2 = drivers.find(d => d.name === 'خالد الدوسري');
const V1 = vehicles.find(v => v.plate === 'ر ط أ 4821'), V2 = vehicles.find(v => v.plate === 'ب ح د 1307');

// accounts: a storekeeper (Custody: Edit) and a read-only custody viewer
const skUser = `store.${tag.toLowerCase()}`, vwUser = `custview.${tag.toLowerCase()}`;
ok((await P(admin, '/api/users', { username: skUser, displayName: 'أمين المستودع', password: 'Store-Keeper-Pass-2026', role: 'Storekeeper' })).s === 200, 'admin creates a storekeeper account');
ok((await P(admin, '/api/users', { username: vwUser, displayName: 'مطلع العهد', password: 'Custody-Viewer-Pass-26', role: 'Custom', levels: { Custody: 'View' } })).s === 200, 'admin creates a custody viewer (view only)');
const sk = (await login(skUser, 'Store-Keeper-Pass-2026')).token, vw = (await login(vwUser, 'Custody-Viewer-Pass-26')).token;
ok(sk && vw, 'storekeeper and viewer sign in');

// ── master data ─────────────────────────────────────────────────────────────────────────────────────────────────
const wh = (await P(sk, '/api/custody/warehouses', { name: `المستودع الرئيسي ${tag}`, location: 'الدمام' })).j;
const wh2 = (await P(sk, '/api/custody/warehouses', { name: `مستودع الموقع ${tag}` })).j;
const tech = (await P(sk, '/api/custody/employees', { name: `فني الإطارات ${tag}`, role: 'فني' })).j;
const cat = async (b) => (await P(sk, '/api/custody/catalog', b)).j;
const tyre = await cat({ code: `TYR-${tag}`, name: 'إطار 315/80R22.5', tracking: 'Individual', category: 'إطار', brand: 'Michelin', minQty: 4, reorderQty: 8 });
const batt = await cat({ code: `BAT-${tag}`, name: 'بطارية 12V 200Ah', tracking: 'Individual', category: 'بطارية', brand: 'Varta' });
const oil = await cat({ code: `OIL-${tag}`, name: 'زيت محرك 15W40', tracking: 'Quantity', category: 'زيوت', unit: 'لتر', minQty: 20, reorderQty: 100, unitCost: 15 });
const filt = await cat({ code: `FLT-${tag}`, name: 'فلتر زيت', tracking: 'Quantity', category: 'فلاتر', unit: 'حبة', minQty: 2, unitCost: 45 });
ok(wh.id && tech.id && tyre.id && oil.id, 'storekeeper creates warehouses, a technician and catalog items');
ok((await P(sk, '/api/custody/catalog', { code: `tyr-${tag}`, name: 'مكرر', tracking: 'Individual', category: 'إطار' })).s === 400, 'duplicate item code refused (case-insensitive)');

// ── receiving parts: internal numbers and duplicate serials ────────────────────────────────────────────────────
const part = async (b) => P(sk, '/api/custody/parts', { warehouseId: wh.id, ...b });
const t1 = (await part({ catalogId: tyre.id, serial: `MX-${tag}-1`, supplier: 'مؤسسة الإطارات', invoiceNo: 'INV-1', cost: 1450, purchaseDate: '2026-10-01', warrantyEnd: '2027-10-01' })).j;
ok(/^P-\d{6}$/.test(t1.internalNo) && t1.status === 'Stock' && t1.brand === 'Michelin', `part received with a server-issued internal number ${t1.internalNo} (brand from the catalog)`);
const dup = await part({ catalogId: tyre.id, serial: ` mx-${tag.toLowerCase()}-1 `, brand: 'michelin' });
ok(dup.s === 400 && dup.j.error.includes(t1.internalNo), 'same serial from the same maker refused (spaces/case ignored): ' + dup.j.error);
ok((await part({ catalogId: tyre.id, serial: `MX-${tag}-1`, brand: 'Bridgestone' })).s === 200, 'the same serial from a different maker is allowed');
const t2 = (await part({ catalogId: tyre.id, serial: `MX-${tag}-2`, cost: 1450 })).j;
const t3 = (await part({ catalogId: tyre.id, serial: `MX-${tag}-3`, cost: 1500 })).j;
const soon = new Date(Date.now() + 10 * 864e5).toISOString().slice(0, 10);
const b1 = (await part({ catalogId: batt.id, cost: 900, warrantyEnd: soon, supplier: 'الوكيل' })).j;
ok(b1.internalNo && !b1.serial && b1.warranty === 'ينتهي قريبًا', `part without a factory serial gets internal number ${b1.internalNo}; warranty «${b1.warranty}»`);
ok((await part({ catalogId: oil.id })).s === 400, 'a quantity item cannot be received as an individual part');

// ── issue rules ────────────────────────────────────────────────────────────────────────────────────────────────
ok((await P(sk, `/api/custody/parts/${b1.id}/issue`, { holderKind: 'driver', holderId: dr1.id })).s === 400, 'issue without a work order or reason refused');
ok((await P(sk, `/api/custody/parts/${b1.id}/issue`, { holderKind: 'driver', holderId: dr1.id, reason: 'لأنني أريد' })).s === 400, 'issue with a reason that is not approved refused');
ok((await P(sk, `/api/custody/parts/${t1.id}/install`, { vehicleId: V1.id, position: 'أمامي يمين', km: 100000 })).s === 400, 'install from stock without a work order or reason refused');
const wo = (await P(sk, '/api/custody/workorders', { vehicleId: V1.id, type: 'استبدال إطارات', description: 'تغيير الإطارات الأمامية', laborCost: 250, km: 100000 })).j;
ok(/^WO-\d{5}$/.test(wo.no), `work order opened ${wo.no}`);
const woOther = (await P(sk, '/api/custody/workorders', { vehicleId: V2.id, type: 'صيانة دورية', description: 'صيانة' })).j;
ok((await P(sk, `/api/custody/parts/${t1.id}/install`, { vehicleId: V1.id, position: 'أمامي يمين', workOrderId: woOther.id })).s === 400, "a work order of another truck is refused");

// ── install: one truck at a time, one part per position ────────────────────────────────────────────────────────
ok((await P(sk, `/api/custody/parts/${t1.id}/install`, { vehicleId: V1.id, workOrderId: wo.id, km: 100000 })).s === 400, 'a tyre needs its position on the truck');
const ins = await P(sk, `/api/custody/parts/${t1.id}/install`, { vehicleId: V1.id, position: 'أمامي يمين', km: 100000, hours: 1200, workOrderId: wo.id });
ok(ins.s === 200 && ins.j.status === 'Installed', 'tyre installed on truck 1 (front right) under the work order');
const twice = await P(sk, `/api/custody/parts/${t1.id}/install`, { vehicleId: V2.id, position: 'أمامي يمين', reason: 'صيانة دورية' });
ok(twice.s === 400 && twice.j.error.includes('شاحنتين'), 'the same part cannot be installed on a second truck: ' + twice.j.error);
ok((await P(sk, `/api/custody/parts/${t2.id}/install`, { vehicleId: V1.id, position: 'أمامي يمين', reason: 'صيانة دورية' })).s === 400, 'a second part on an occupied position refused (use replace)');
const race = await Promise.all([
  P(sk, `/api/custody/parts/${t2.id}/install`, { vehicleId: V1.id, position: 'أمامي يسار', reason: 'صيانة دورية' }),
  P(sk, `/api/custody/parts/${t2.id}/install`, { vehicleId: V2.id, position: 'أمامي يسار', reason: 'صيانة دورية' })]);
ok(race.map(r => r.s).sort().join() === '200,400', 'two simultaneous installs of one part on two trucks: exactly one succeeds');
const t2now = (await G(sk, `/api/custody/parts/${t2.id}`)).j.part;
ok(t2now.status === 'Installed' && !!t2now.vehicleId, `part is on exactly one truck (${t2now.plate})`);
const t2OnV1 = t2now.vehicleId === V1.id ? 1450 : 0;   // which truck won the race decides where its cost goes

// ── replacement: the old part's fate is mandatory ──────────────────────────────────────────────────────────────
const rep = (b) => P(sk, '/api/custody/replace', { oldPartId: t1.id, newPartId: t3.id, km: 112500, hours: 1350, reason: 'تآكل غير منتظم', workOrderId: wo.id, ...b });
ok((await rep({})).s === 400, 'replacement without choosing the old part fate refused');
ok((await rep({ oldPartFate: { fate: 'throw-away' } })).s === 400, 'an unknown fate refused');
ok((await rep({ oldPartFate: { fate: 'repair' } })).s === 400, 'fate «repair» needs the workshop');
ok((await rep({ oldPartFate: { fate: 'scrap' } })).s === 400, 'fate «scrap» needs an approval reference');
ok((await rep({ workOrderId: undefined, oldPartFate: { fate: 'repair', vendor: 'ورشة الخليج' } })).s === 400, 'replacement without a work order or approved reason refused');
const r1 = await rep({ oldPartFate: { fate: 'repair', vendor: 'ورشة الخليج للإطارات', expectedReturn: soon, estimatedCost: 200 } });
ok(r1.s === 200 && r1.j.oldStatus === 'Repair' && r1.j.newStatus === 'Installed', 'replacement done: old tyre sent for repair, new tyre installed');
const t1d = (await G(sk, `/api/custody/parts/${t1.id}`)).j, t3d = (await G(sk, `/api/custody/parts/${t3.id}`)).j;
ok(t3d.part.position === 'أمامي يمين' && t3d.part.plate === V1.plate && t1d.part.repairVendor === 'ورشة الخليج للإطارات', 'new part took the same position; old part is at the workshop');
ok(t1d.life.length === 1 && t1d.life[0].km === 12500 && t1d.life[0].hours === 150, 'part life recorded: 12,500 km and 150 operating hours on the truck');
ok(t1d.movements.map(m => m.type).reverse().join() === 'receive,install,replace-out,repair-send', 'history: receive → install → replace (old) → sent for repair');

// ── hand-over and acceptance ───────────────────────────────────────────────────────────────────────────────────
const iss = await P(sk, `/api/custody/parts/${b1.id}/issue`, { holderKind: 'driver', holderId: dr1.id, reason: 'عهدة شخصية للموظف', handoverBy: 'أمين المستودع' });
ok(iss.s === 200 && iss.j.status === 'Pending' && /^TR-\d{5}$/.test(iss.j.no), `issue recorded as hand-over ${iss.j.no}, waiting for the receiver`);
ok((await G(sk, `/api/custody/parts/${b1.id}`)).j.part.status === 'Stock', 'until accepted the part is not yet in the driver\'s custody');
ok((await P(sk, `/api/custody/parts/${b1.id}/install`, { vehicleId: V2.id, reason: 'صيانة دورية' })).s === 400, 'a part in a pending hand-over cannot be used elsewhere');
ok((await P(d2, `/api/custody/transfers/${iss.j.id}/accept`, {})).s === 403, 'another driver cannot accept it');
const mine = (await G(d1, '/api/custody/my')).j;
ok(mine.pending.some(t => t.id === iss.j.id), 'the receiving driver sees it waiting for his acceptance');
ok((await G(d1, '/api/notifications')).j.some(n => n.text.includes(b1.internalNo)), 'the driver is notified');
const acc = await P(d1, `/api/custody/transfers/${iss.j.id}/accept`, { note: 'استلمت البطارية سليمة' });
ok(acc.s === 200 && acc.j.status === 'Accepted' && acc.j.decidedBy === 'سالم العتيبي', 'the driver accepts the hand-over himself');
ok((await P(d1, `/api/custody/transfers/${iss.j.id}/accept`, {})).s === 400, 'accepting twice refused');
const b1d = (await G(sk, `/api/custody/parts/${b1.id}`)).j.part;
ok(b1d.status === 'Issued' && b1d.holder === 'سالم العتيبي' && !b1d.vehicleId, 'battery now in the custody of سالم (a person, not a truck)');
const tr = (await P(sk, `/api/custody/parts/${b1.id}/transfer`, { toKind: 'employee', toId: tech.id, handoverBy: 'سالم العتيبي' })).j;
const rej = await P(sk, `/api/custody/transfers/${tr.id}/reject`, { reason: 'البطارية لا تخص قسم الإطارات' });
ok(rej.s === 200 && (await G(sk, `/api/custody/parts/${b1.id}`)).j.part.holder === 'سالم العتيبي', 'a rejected hand-over leaves the custody with the giver');
ok((await P(sk, `/api/custody/transfers/${tr.id}/reject`, { reason: 'مرة أخرى' })).s === 400, 'a decided hand-over cannot be decided again');

// ── a driver change on the truck never moves installed parts or custody ────────────────────────────────────────
ok((await P(admin, `/api/assignments/vehicles/${V1.id}`, { driverId: dr2.id, note: 'تبديل سائق' })).s === 204, 'truck 1 is reassigned from سالم to خالد');
const file = (await G(sk, `/api/custody/vehicles/${V1.id}`)).j;
ok(file.driver.name === 'خالد الدوسري' && file.installed.some(x => x.part.id === t3.id), 'truck file: new driver shown, installed parts unchanged');
ok((await G(sk, `/api/custody/parts/${b1.id}`)).j.part.holder === 'سالم العتيبي', 'the battery stays in سالم\'s custody after the driver change');
ok(!file.driverCustody.parts.some(p => p.id === b1.id), "truck file lists the new driver's custody, not the previous driver's");
ok(file.replacements.some(m => m.type === 'replace-out' && m.partId === t1.id) && file.costs.labor === 250 && file.costs.parts === 2950 + t2OnV1, `truck file shows replacement history and cost (parts ${file.costs?.parts}, labor ${file.costs?.labor})`);
await P(admin, `/api/assignments/vehicles/${V1.id}`, { driverId: dr1.id, note: 'إعادة' });

// ── quantity stock never goes negative ─────────────────────────────────────────────────────────────────────────
ok((await P(sk, '/api/custody/stock/receive', { catalogId: oil.id, warehouseId: wh.id, qty: 30, unitCost: 15, supplier: 'بترومين', invoiceNo: 'PM-77' })).j.qty === 30, 'receive 30 L of oil');
ok((await P(sk, '/api/custody/stock/issue', { catalogId: oil.id, warehouseId: wh.id, qty: 25, vehicleId: V1.id })).s === 400, 'quantity issue without work order or reason refused');
ok((await P(sk, '/api/custody/stock/issue', { catalogId: oil.id, warehouseId: wh.id, qty: 25, vehicleId: V1.id, workOrderId: wo.id, km: 112500 })).s === 200, 'issue 25 L to truck 1 under the work order');
const over = await P(sk, '/api/custody/stock/issue', { catalogId: oil.id, warehouseId: wh.id, qty: 10, vehicleId: V1.id, reason: 'استهلاك تشغيلي' });
ok(over.s === 400 && over.j.error.includes('5'), 'issuing more than available refused: ' + over.j.error);
const burst = await Promise.all([1, 2, 3].map(() => P(sk, '/api/custody/stock/issue', { catalogId: oil.id, warehouseId: wh.id, qty: 2, vehicleId: V2.id, reason: 'استهلاك تشغيلي' })));
const stockNow = (await G(sk, `/api/custody/stock?warehouseId=${wh.id}`)).j.balances.find(b => b.catalogId === oil.id).qty;
ok(burst.filter(r => r.s === 200).length === 2 && stockNow === 1, `three simultaneous issues of 2 L from 5 L: two succeed, balance ${stockNow} (never negative)`);
ok((await P(sk, '/api/custody/stock/adjust', { catalogId: oil.id, warehouseId: wh.id, newQty: -3, reason: 'تجربة' })).s === 400, 'a negative stock adjustment is refused');
const qi = (await P(sk, '/api/custody/stock/issue', { catalogId: oil.id, warehouseId: wh.id, qty: 1, holderKind: 'driver', holderId: dr1.id, reason: 'طوارئ على الطريق' })).j;
ok(qi.status === 'Pending' && qi.qty === 1, 'quantity issued to a driver waits for his acceptance');
await P(d1, `/api/custody/transfers/${qi.id}/reject`, { reason: 'لا أحتاجها' });
ok((await G(sk, `/api/custody/stock?warehouseId=${wh.id}`)).j.balances.find(b => b.catalogId === oil.id).qty === 1, 'rejected quantity goes back to the warehouse');
await P(sk, '/api/custody/stock/receive', { catalogId: filt.id, warehouseId: wh.id, qty: 6, bin: 'رف B-2' });
const fi = (await P(sk, '/api/custody/stock/issue', { catalogId: filt.id, warehouseId: wh.id, qty: 2, holderKind: 'employee', holderId: tech.id, reason: 'صيانة دورية', receiverSigned: true })).j;
ok(fi.status === 'Accepted', 'quantity issue signed at the counter is accepted at once');
ok((await P(sk, '/api/custody/stock/return', { catalogId: filt.id, warehouseId: wh.id, qty: 5, holderKind: 'employee', holderId: tech.id })).s === 400, 'returning more than the person holds refused');

// ── count, damage, loss, scrap, clearance ──────────────────────────────────────────────────────────────────────
const cnt = (await P(sk, '/api/custody/counts', { warehouseId: wh.id, lines: [{ catalogId: filt.id, actual: 3, bin: 'رف B-2' }], foundParts: [] })).j;
ok(/^CNT-/.test(cnt.no) && cnt.lines[0].recorded === 4 && cnt.lines[0].actual === 3, `stock count ${cnt.no}: recorded 4, counted 3, adjusted`);
ok((await P(sk, `/api/custody/parts/${t2.id}/lost`, { reason: 'x' })).s === 400, 'declaring a part lost needs a real reason');
ok((await P(sk, `/api/custody/parts/${t2.id}/damaged`, { reason: 'انفجار الإطار على الطريق' })).j.status === 'Damaged', 'installed tyre declared damaged (detached from the truck, last location kept)');
ok((await P(sk, `/api/custody/parts/${t2.id}/scrap`, { reason: 'غير قابل للإصلاح' })).s === 400, 'scrap without approval refused');
ok((await P(sk, `/api/custody/parts/${t2.id}/review`, { outcome: 'قضاء وقدر، لا تحميل على السائق' })).s === 200, 'review outcome recorded');
ok((await P(sk, `/api/custody/parts/${t2.id}/scrap`, { reason: 'غير قابل للإصلاح', approval: 'اعتماد المدير 55' })).j.status === 'Scrapped', 'approved scrap');
const clr = (await P(sk, '/api/custody/clearance', { holderKind: 'employee', holderId: tech.id, warehouseId: wh.id, note: 'نقل الفني لفرع آخر' })).j;
ok(clr.quantities.length === 1 && clr.quantities[0].qty === 2, 'custody clearance returns everything the technician held');
const clRep = (await G(sk, `/api/custody/reports/clearance?holderKind=employee&holderId=${tech.id}`)).j;
ok(clRep.subtitle.includes('مُخلاة') && clRep.tables[0].rows.length >= 1, 'clearance report: what he returned, nothing remaining');

// ── search / barcode ───────────────────────────────────────────────────────────────────────────────────────────
ok((await G(sk, `/api/custody/search?q=${encodeURIComponent(t3.internalNo)}`)).j.exactPartId === t3.id, 'scanning the internal number (barcode/QR) finds the part');
ok((await G(sk, `/api/custody/search?q=${encodeURIComponent(`mx-${tag}-3`)}`)).j.exactPartId === t3.id, 'serial search finds the part');
ok((await G(sk, '/api/custody/search?q=4821')).j.vehicles.some(v => v.id === V1.id), 'plate search finds the truck');
ok((await G(sk, `/api/custody/search?q=${encodeURIComponent('سالم')}`)).j.holders.some(h => h.id === dr1.id), 'employee/driver name search');

// ── attachments (part photo / invoice) ─────────────────────────────────────────────────────────────────────────
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==', 'base64');
const fd = new FormData();
fd.append('file', new Blob([png], { type: 'image/png' }), 'tyre.png'); fd.append('ownerType', 'part'); fd.append('ownerId', t3.id); fd.append('kind', 'part-photo');
const up = await fetch(B + '/api/attachments', { method: 'POST', headers: { authorization: 'Bearer ' + sk }, body: fd });
const att = await up.json();
ok(up.status === 200 && att.id, 'storekeeper attaches a photo of the part');
ok((await G(sk, `/api/custody/parts/${t3.id}`)).j.attachments.length === 1, 'the part shows its attachment');
ok((await fetch(`${B}/api/attachments/${att.id}`, { headers: { authorization: 'Bearer ' + d1 } })).status === 403, 'a driver cannot open custody attachments');

// ── permissions and company isolation ──────────────────────────────────────────────────────────────────────────
ok((await P(d1, '/api/custody/parts', { catalogId: tyre.id, warehouseId: wh.id })).s === 403, 'a driver cannot receive parts');
ok((await G(d1, '/api/custody/parts')).s === 403, 'a driver cannot list the parts');
ok((await P(d1, `/api/custody/parts/${t3.id}/remove`, { reason: 'تجربة', fate: { fate: 'stock', warehouseId: wh.id } })).s === 403, 'a driver cannot remove parts');
ok((await G(vw, '/api/custody/parts')).s === 200, 'the viewer can see parts');
ok((await P(vw, '/api/custody/stock/receive', { catalogId: oil.id, warehouseId: wh.id, qty: 1 })).s === 403, 'the viewer cannot change stock');
ok((await P(vw, '/api/custody/workorders', { vehicleId: V1.id, type: 'x', description: 'تجربة' })).s === 403, 'the viewer cannot open work orders');
ok((await G(vw, '/api/custody/reports/costs')).s === 403, 'the viewer (no cost permission) cannot see the cost report');
ok((await G(vw, '/api/custody/parts')).j.every(p => p.cost == null), 'costs are hidden from the viewer');
const gl = (await G(gulf, '/api/custody/parts')).j;
ok(Array.isArray(gl) && !gl.some(p => p.id === t1.id), "another company's admin does not see these parts");
ok((await G(gulf, `/api/custody/parts/${t1.id}`)).s === 404, "another company cannot open a part (404)");
ok((await P(gulf, `/api/custody/parts/${b1.id}/return`, { warehouseId: wh.id })).s === 404, "another company cannot act on a part");
ok((await G(gulf, `/api/custody/vehicles/${V1.id}`)).s === 404, "another company cannot open the truck file");
ok(!(await G(gulf, `/api/custody/search?q=${encodeURIComponent(t3.internalNo)}`)).j.exactPartId, 'another company cannot find it by barcode');

// ── reports (JSON for all 14, Excel) ───────────────────────────────────────────────────────────────────────────
const list = (await G(admin, '/api/custody/reports')).j;
ok(list.length === 14, '14 reports listed');
for (const r of list) {
  const qs = r.key === 'history' ? `?partId=${t1.id}` : '';
  const res = await G(admin, `/api/custody/reports/${r.key}${qs}`);
  ok(res.s === 200 && res.j.tables.length >= 1, `report ${r.no} «${r.title}»: ${res.j.tables?.map(t => t.rows.length).join('/')} rows`);
}
const rows = async (k, qs = '') => (await G(admin, `/api/custody/reports/${k}${qs}`)).j.tables;
ok((await rows('shortages')).at(0).rows.some(r => r[0] === oil.code), 'shortage report lists the oil (1 L ≤ minimum 20)');
ok((await rows('replacements')).at(0).rows.some(r => r[4] === t1.internalNo && r[6] === t3.internalNo && r[9] === 'إرسال للإصلاح'), 'replacement report: old, new and the old part fate');
ok((await rows('issues')).at(0).rows.some(r => r[4] === b1.internalNo && r[7] === 'سالم العتيبي' && r[10] === 'أمين المستودع'), 'issue report: receiver and approving user');
ok((await rows('warranty')).at(0).rows.some(r => r[0] === b1.internalNo && r[8] === 'ينتهي قريبًا'), 'warranty report flags the battery expiring soon');
ok((await rows('repair')).at(0).rows.some(r => r[0] === t1.internalNo && r[3] === 'ورشة الخليج للإطارات'), 'parts-under-repair report');
ok((await rows('damaged')).at(0).rows.some(r => r[0] === t2.internalNo && r[8].includes('قضاء')), 'damaged & lost report with the review outcome');
ok((await rows('transfers')).at(0).rows.some(r => r[0] === tr.no && r[9] === 'مرفوض'), 'transfer report shows hand-overs and their outcome');
ok((await rows('costs', `?vehicleId=${V1.id}`)).at(0).rows[0][5] === 2950 + t2OnV1 + 250 + 375, 'maintenance cost of truck 1 = parts 2,950 + oil 375 + labor 250');
for (const k of ['installed', 'costs', 'history']) {
  const x = await fetch(`${B}/api/custody/reports/${k}?format=xlsx${k === 'history' ? `&partId=${t1.id}` : ''}`, { headers: { authorization: 'Bearer ' + sk } });
  const buf = Buffer.from(await x.arrayBuffer());
  ok(x.status === 200 && /spreadsheetml/.test(x.headers.get('content-type') || '') && buf.slice(0, 2).toString() === 'PK', `Excel export of «${k}» (${buf.length} bytes)`);
}
ok((await fetch(`${B}/api/custody/reports/installed?format=xlsx`, { headers: { authorization: 'Bearer ' + vw } })).status === 403, 'Excel export needs the export permission');

// ── history is never deleted ───────────────────────────────────────────────────────────────────────────────────
const before = (await G(sk, `/api/custody/parts/${t1.id}`)).j.movements;
const ed = await call(sk, 'PUT', `/api/custody/parts/${t1.id}`, { catalogId: tyre.id, serial: `MX-${tag}-1`, supplier: 'مؤسسة الإطارات الحديثة', invoiceNo: 'INV-1', cost: 1450, warrantyEnd: '2027-10-01' });
ok(ed.s === 400, 'editing part data without a reason refused');
await call(sk, 'PUT', `/api/custody/parts/${t1.id}`, { catalogId: tyre.id, serial: `MX-${tag}-1`, supplier: 'مؤسسة الإطارات الحديثة', invoiceNo: 'INV-1', cost: 1450, warrantyEnd: '2027-10-01', reason: 'تصحيح اسم المورد' });
const after = (await G(sk, `/api/custody/parts/${t1.id}`)).j.movements;
ok(after.length === before.length + 1 && after[0].type === 'edit' && before.every(m => after.some(x => x.id === m.id)), 'a correction adds a new movement; earlier movements are kept');
for (const [m, p] of [['DELETE', `/api/custody/parts/${t1.id}`], ['DELETE', `/api/custody/transfers/${tr.id}`]])
  ok([404, 405].includes((await call(admin, m, p)).s), `${m} ${p.split('/').slice(0, 4).join('/')}… does not exist`);
ok((await P(sk, `/api/custody/parts/${t1.id}/repair-return`, { fixed: true, cost: 180, warehouseId: wh2.id })).j.status === 'Stock', 'tyre returns from repair (cost 180) to the site warehouse');
const hist = (await rows('history', `?q=${t1.internalNo}`))[0].rows.map(r => r[1]);
ok(hist.join('|') === 'استلام للمستودع|تركيب على شاحنة|استبدال: القطعة القديمة|إرسال للإصلاح|تعديل بيانات|عودة من الإصلاح', 'part history report: ' + hist.join(' → '));
