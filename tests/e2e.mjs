// Functional regression test (29 checks): full trip cycle, meter rules, approval, review, handover, reset, report, dashboard, audit.
import { call, token, B } from './fuauth.mjs';
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const login = token;

const admin = await login('admin'), sup = await login('supervisor'), d1 = await login('driver1'), d2 = await login('driver2');
ok(admin && sup && d1, 'login admin/supervisor/driver1');
ok((await call(null, 'POST', '/api/auth/login', { username: 'admin', password: 'wrong' })).s === 401, 'wrong password rejected');
const me = (await call(d1, 'GET', '/api/me')).j; ok(me.vehicles.length === 1, 'driver1 sees 1 assigned vehicle: ' + me.vehicles[0]?.plate);
const vehicles = (await call(admin, 'GET', '/api/master/vehicles')).j; const v1 = vehicles.find(v => v.plate === 'ر ط أ 4821'), v2 = vehicles.find(v => v.plate !== v1.plate);
const customers0 = (await call(admin, 'GET', '/api/master/customers')).j; const customers = [customers0.find(c => c.name === 'مقاولات الجبيل')]; const stations = (await call(admin, 'GET', '/api/master/stations')).j;
ok((await call(d1, 'POST', '/api/trips', { customerId: customers[0].id, stationId: stations[0].id, sourceRef: 'X', product: 'ديزل', qtyRequested: 3000 })).s === 403, 'driver cannot create orders (403)');
const created = (await call(sup, 'POST', '/api/trips', { customerId: customers[0].id, stationId: stations[2].id, sourceRef: 'AR-1', product: 'ديزل', qtyRequested: 3000 })).j;
const trip = created.trip; ok(trip.driverId === me.driverId, 'default driver of customer auto-assigned: ' + trip.no);
const list = (await call(d1, 'GET', '/api/trips')).j; ok(list.items.every(x => x.trip.driverId === me.driverId), 'driver lists only own orders (' + list.total + ')');
ok((await call(d1, 'POST', `/api/trips/${trip.id}/load`, { vehicleId: v2.id, qty: 3000, docNo: 'D1', odometer: 1000, meterStart: 125000, meterEnd: 128000 })).j.error === 'المركبة المختارة ليست مسندة لك.', 'vehicle not assigned to driver rejected');
let r = await call(d1, 'POST', `/api/trips/${trip.id}/load`, { vehicleId: v1.id, qty: 3000, docNo: 'D1', odometer: 1000, meterStart: 124900, meterEnd: 128000 });
ok(r.s === 400 && r.j.error.includes('«125,000»'), 'reading 124,900 below 125,000 rejected: ' + r.j.error);
r = await call(d1, 'POST', `/api/trips/${trip.id}/load`, { vehicleId: v1.id, qty: 3000, docNo: 'D1', odometer: 1000, meterStart: 125000, meterEnd: 125000 });
ok(r.s === 400 && r.j.error.includes('أكبر من قراءة البداية'), 'equal start/end rejected for positive quantity');
r = await call(d1, 'POST', `/api/trips/${trip.id}/load`, { vehicleId: v1.id, qty: 3000, docNo: 'D1', odometer: 1000, meterStart: 125000, meterEnd: 128000 });
ok(r.s === 200 && r.j.trip.status === 'Loaded' && r.j.reconciliation.loadMeterQty === 3000, 'load accepted, meter qty 3,000');
ok((await call(d1, 'POST', `/api/trips/${trip.id}/arrive`, { location: 'x' })).s === 400, 'cannot skip from Loaded to Arrived');
await call(d1, 'POST', `/api/trips/${trip.id}/depart`);
await call(d1, 'POST', `/api/trips/${trip.id}/arrive`, { location: customers[0].site });
await call(d1, 'POST', `/api/trips/${trip.id}/unload`, { sealsOk: true });
r = await call(d1, 'POST', `/api/trips/${trip.id}/deliver`, { meterBefore: 127000, meterAfter: 131000, received: 3000, leftInTank: 0, returned: 0, receiptNo: 'RC1', hasMoreStops: false });
ok(r.s === 400 && r.j.error.includes('«128,000»'), 'unloading start below last reading rejected');
r = await call(d1, 'POST', `/api/trips/${trip.id}/deliver`, { meterBefore: 128000, meterAfter: 131000, received: 3000, leftInTank: 0, returned: 0, receiptNo: 'RC1', hasMoreStops: false });
ok(r.j.trip.status === 'AwaitingApproval', 'clean match → awaiting approval (unsettled ' + r.j.reconciliation.unsettled + ')');
ok((await call(d1, 'POST', `/api/trips/${trip.id}/approve`, {})).s === 403, 'driver cannot approve');
r = await call(sup, 'POST', `/api/trips/${trip.id}/approve`, { note: 'تمت المطابقة' }); ok(r.j.trip.status === 'Approved' && r.j.trip.approvedBy, 'supervisor approves, approver recorded');
// second trip with a difference -> review
const t2 = (await call(sup, 'POST', '/api/trips', { customerId: customers[0].id, stationId: stations[0].id, sourceRef: 'ST-2', product: 'ديزل', qtyRequested: 4000 })).j.trip;
await call(d1, 'POST', `/api/trips/${t2.id}/load`, { vehicleId: v1.id, qty: 4000, docNo: 'D2', odometer: 1200, meterStart: 131000, meterEnd: 135000 });
for (const s of ['depart']) await call(d1, 'POST', `/api/trips/${t2.id}/${s}`);
await call(d1, 'POST', `/api/trips/${t2.id}/arrive`, { location: customers[0].site }); await call(d1, 'POST', `/api/trips/${t2.id}/unload`, { sealsOk: true });
r = await call(d1, 'POST', `/api/trips/${t2.id}/deliver`, { meterBefore: 135000, meterAfter: 138800, received: 4000, leftInTank: 0, returned: 0, receiptNo: 'RC2', hasMoreStops: false });
ok(r.j.trip.status === 'UnderReview', 'difference → under review: ' + r.j.trip.reviewReason);
ok((await call(sup, 'POST', `/api/trips/${t2.id}/approve`, {})).s === 400, 'under-review trip cannot be plain-approved');
ok((await call(sup, 'POST', `/api/trips/${t2.id}/resolve`, { note: 'فرق حرارة موثق' })).j.trip.status === 'Approved', 'resolved with written decision');
// handover mid-trip
const t3 = (await call(sup, 'POST', '/api/trips', { customerId: customers[0].id, stationId: stations[1].id, sourceRef: 'MY-3', product: 'ديزل', qtyRequested: 2000 })).j.trip;
await call(d1, 'POST', `/api/trips/${t3.id}/load`, { vehicleId: v1.id, qty: 2000, docNo: 'D3', odometer: 1300, meterStart: 138800, meterEnd: 140800 });
const drivers = (await call(admin, 'GET', '/api/master/drivers')).j; const dr2 = drivers.find(d => d.name === 'خالد الدوسري');
r = await call(sup, 'POST', `/api/trips/${t3.id}/handover`, { toDriverId: dr2.id, remaining: 2000, meterReading: 140799, note: 'مرض السائق', moveVehicle: true });
ok(r.s === 400 && r.j.error.includes('«140,800»'), 'handover reading below last rejected');
r = await call(sup, 'POST', `/api/trips/${t3.id}/handover`, { toDriverId: dr2.id, remaining: 2000, meterReading: 140800, note: 'مرض السائق', moveVehicle: true });
ok(r.j.trip.driverId === dr2.id && r.j.trip.handovers.length === 1, 'handover recorded to driver2');
ok((await call(d2, 'POST', `/api/trips/${t3.id}/handover/ack`, { confirmed: true })).j.trip.handovers[0].ack === 'Confirmed', 'receiving driver confirmed');
const notes = (await call(d2, 'GET', '/api/notifications')).j; ok(notes.some(n => n.text.includes('نُقل إليك')), 'driver2 notified: ' + notes[0]?.text);
// meter reset needs supervisor
const meters = (await call(admin, 'GET', '/api/master/meters')).j; const m1 = meters.find(m => m.serial === 'LC-M7-40112');
ok((await call(d1, 'POST', `/api/meters/${m1.id}/reset`, { type: 'تصفير للصيانة', reason: 'x', newBaseline: 0 })).s === 403, 'driver cannot reset meter');
ok((await call(sup, 'POST', `/api/meters/${m1.id}/reset`, { type: 'استبدال العداد', reason: 'عطل', newBaseline: 0, newSerial: 'LC-M7-99999' })).s === 204, 'supervisor reset with new serial');
const led = (await call(sup, 'GET', `/api/meters/${m1.id}/ledger`)).j; ok(led.last === 0 && led.readings.length >= 9 && led.resets.length === 1, 'ledger kept full history (' + led.readings.length + ' readings), last=0');
// report + excel
const rep = (await call(sup, 'GET', '/api/reports/fill-operations')).j; ok(rep.rows.length === 3 && rep.summary.flagged === 1, 'fill report rows=3 flagged=1');
const x = await fetch(B + '/api/reports/fill-operations?format=xlsx', { headers: { authorization: 'Bearer ' + sup } }); const buf = Buffer.from(await x.arrayBuffer());
ok(x.status === 200 && buf.slice(0, 2).toString() === 'PK', 'excel export ' + buf.length + ' bytes');
const dash = (await call(sup, 'GET', '/api/dashboard/summary')).j; ok(dash.operations === 3 && dash.loaded === 9000, 'dashboard summary loaded=' + dash.loaded);
const tr = (await call(sup, 'GET', `/api/trips/${t2.id}`)).j; ok(tr.audit.length >= 6, 'audit trail entries: ' + tr.audit.length);
