// Customer orders (طلبات العملاء) — the hub: both order types, dispatch of two trucks on one order, totals from the real trip workflow,
// late flag, expired document refusing dispatch + audited override, close/cancel rules, edits with reason, permissions (Costs hides money),
// company isolation, legacy trip linking, dispatch board, order no. in trip views and the driver's fill-report list.
// Run on a fresh database with the demo seed: API=http://127.0.0.1:5005 node tests/orders.mjs
import { call as rawCall, token, login } from './fuauth.mjs';
async function call(...args) { for (let i = 0; ; i++) { const r = await rawCall(...args); if (r.s !== 429 || i > 30) return r; await new Promise(res => setTimeout(res, 3000)); } }
const ok = (c, m) => { console.log((c ? 'PASS ' : 'FAIL ') + m); if (!c) process.exitCode = 1 };
const P = (t, p, b) => call(t, 'POST', p, b ?? {});
const G = (t, p) => call(t, 'GET', p);
const tag = Date.now().toString(36).slice(-5);
const day = (d) => new Date(Date.now() + d * 864e5).toISOString().slice(0, 10);
const ksaToday = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh' }).format(new Date());
const ksaDay = (d) => { const x = new Date(ksaToday + 'T12:00:00Z'); x.setUTCDate(x.getUTCDate() + d); return x.toISOString().slice(0, 10); };

const admin = await token('admin'), sup = await token('supervisor'), d1 = await token('driver1'), d2 = await token('driver2'), gulf = await token('gulf.admin');
ok(admin && sup && d1 && d2 && gulf, 'sign in admin, supervisor, two drivers and the other company');
const vehicles = (await G(admin, '/api/master/vehicles')).j, customers = (await G(admin, '/api/master/customers')).j, stations = (await G(admin, '/api/master/stations')).j;
const drivers = (await G(admin, '/api/master/drivers')).j;
const V1 = vehicles.find(v => v.plate === 'ر ط أ 4821'), V2 = vehicles.find(v => v.plate === 'ب ح د 1307');
const C1 = customers.find(c => c.name === 'مقاولات الجبيل'), C2 = customers.find(c => c.name === 'مصنع الدمام');
const dr1 = drivers.find(d => d.name === 'سالم العتيبي'), dr2 = drivers.find(d => d.name === 'خالد الدوسري');
const aramco = stations.find(s => s.type === 'Aramco');

// accounts: a viewer (Viewer preset) and an operations reader without Costs
ok((await P(admin, '/api/users', { username: `ordview.${tag}`, displayName: 'مطلع الطلبات', password: 'Orders-Viewer-Pass-26', role: 'Custom', levels: { Ops: 'View' } })).s === 200, 'admin creates an orders reader (Ops: View, no Costs)');
const reader = (await login(`ordview.${tag}`, 'Orders-Viewer-Pass-26')).token;

// ── create both types ─────────────────────────────────────────────────────────────────────────────────────────────
let r = await P(sup, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 12000, priority: 'Scheduled', requestedFrom: day(-1), requestedTo: day(5), customerRef: `PO-${tag}`, transportRatePerLiter: 0.12, fuelPricePerLiter: 1.7 });
const tOrder = r.j.order;
ok(r.s === 200 && /^SO-\d{4}-\d{4}$/.test(tOrder.no) && tOrder.typeLabel === 'نقل فقط' && tOrder.status === 'New', `transport-only order created ${tOrder?.no} (status «${tOrder?.statusLabel}»)`);
ok(r.j.money && r.j.money.fuelPricePerLiter === null && r.j.money.transportRatePerLiter === 0.12, 'transport only: fuel price is not kept, transport rate is');
r = await P(sup, '/api/orders', { customerId: C2.id, type: 'TransportAndSupply', product: 'ديزل', qtyRequested: 7000, priority: 'Urgent', requestedTo: day(2), transportRatePerLiter: 0.12, fuelPricePerLiter: 1.7, notes: 'مولدات المصنع' });
const sOrder = r.j.order;
ok(r.s === 200 && sOrder.typeLabel === 'نقل وتوريد' && sOrder.priorityLabel === 'عاجل' && r.j.money.fuelPricePerLiter === 1.7, `transport & supply order ${sOrder.no}: urgent, fuel price 1.70`);
ok(Number(sOrder.no.slice(-4)) === Number(tOrder.no.slice(-4)) + 1, 'order numbers are sequential');
ok((await P(sup, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 0, priority: 'Scheduled' })).s === 400, 'quantity 0 refused');
r = await P(sup, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 100, priority: 'Scheduled', requestedFrom: day(3), requestedTo: day(1) });
ok(r.s === 400 && r.j.error.includes('بعد بدايتها'), 'delivery window ending before it starts refused: ' + r.j.error);
ok((await P(d1, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 100, priority: 'Scheduled' })).s === 403, 'driver cannot create an order (403)');
ok((await P(reader, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 100, priority: 'Scheduled' })).s === 403, 'orders reader (view only) cannot create an order (403)');
ok((await G(d1, '/api/orders')).s === 403, 'driver cannot list customer orders');

// ── dispatch two trucks on the supply order ───────────────────────────────────────────────────────────────────────
r = await P(sup, `/api/orders/${sOrder.id}/trips`, { vehicleId: V1.id, stationId: aramco.id, sourceRef: `AR-${tag}-1`, qty: 3000 });
const trip1 = r.j;
ok(r.s === 200 && /^FU-/.test(trip1.tripNo) && trip1.warnings.length === 0, `truck 1 dispatched ${trip1.tripNo} (driver taken from the truck assignment)`);
r = await P(sup, `/api/orders/${sOrder.id}/trips`, { vehicleId: V2.id, stationId: aramco.id, sourceRef: `AR-${tag}-2`, qty: 4500 });
const trip2 = r.j;
ok(r.s === 200 && trip2.warnings.some(w => w.includes('تتجاوز كمية الطلب')), 'truck 2 dispatched; planned 7,500 > 7,000 only warns: ' + trip2.warnings[0]);
let hub = (await G(sup, `/api/orders/${sOrder.id}`)).j;
ok(hub.trips.length === 2 && hub.order.status === 'Planned' && hub.order.totals.planned === 7500 && hub.order.totals.toDispatch === 0, `hub: 2 trips, status «${hub.order.statusLabel}», planned 7,500`);
ok(hub.trips[0].driver === dr1.name && hub.trips[1].driver === dr2.name && hub.trips[1].plate === V2.plate, 'each trip shows its driver and truck');
let tv = (await G(sup, `/api/trips/${trip1.tripId}`)).j;
ok(tv.view.orderNo === sOrder.no && tv.view.trip.orderId === sOrder.id, 'trip detail links back to the order no. ' + tv.view.orderNo);
const tl = (await G(sup, '/api/trips?size=50')).j;
ok(tl.items.find(x => x.trip.id === trip2.tripId)?.orderNo === sOrder.no, 'trip list shows the order no.');
const mine = (await G(d1, '/api/fill-reports/mine')).j;
ok(mine.find(x => x.tripId === trip1.tripId)?.orderNo === sOrder.no, 'driver app list (fill-reports/mine) carries the order no.');

// ── execute through the existing trip API, watch totals ───────────────────────────────────────────────────────────
r = await P(d1, `/api/trips/${trip1.tripId}/load`, { vehicleId: V1.id, qty: 3000, docNo: `DOC-${tag}-1`, odometer: 1000, meterStart: 125000, meterEnd: 128000 });
ok(r.s === 200, 'driver 1 loads 3,000 L');
hub = (await G(sup, `/api/orders/${sOrder.id}`)).j;
ok(hub.order.status === 'InProgress' && hub.order.totals.loaded === 3000 && hub.trips[0].loadingDocNo === `DOC-${tag}-1`, `after loading: «${hub.order.statusLabel}», loaded 3,000, loading document shown`);
await P(d1, `/api/trips/${trip1.tripId}/depart`); await P(d1, `/api/trips/${trip1.tripId}/arrive`, { location: C2.site }); await P(d1, `/api/trips/${trip1.tripId}/unload`, { sealsOk: true });
r = await P(d1, `/api/trips/${trip1.tripId}/deliver`, { meterBefore: 128000, meterAfter: 130990, received: 2990, leftInTank: 0, returned: 0, receiptNo: `RC-${tag}-1`, hasMoreStops: false });
ok(r.s === 200, 'driver 1 delivers 2,990 L by the meter');
r = await P(d2, `/api/trips/${trip2.tripId}/load`, { vehicleId: V2.id, qty: 4500, docNo: `DOC-${tag}-2`, odometer: 500, meterBaseline: 50000, meterStart: 50000, meterEnd: 54500 });
ok(r.s === 200, 'driver 2 loads 4,500 L');
hub = (await G(sup, `/api/orders/${sOrder.id}`)).j;
ok(hub.order.status === 'InProgress' && hub.order.totals.delivered === 2990 && hub.order.totals.remaining === 4010, `one truck delivered: delivered 2,990, remaining ${hub.order.totals.remaining}`);
await P(d2, `/api/trips/${trip2.tripId}/depart`); await P(d2, `/api/trips/${trip2.tripId}/arrive`, { location: C2.site }); await P(d2, `/api/trips/${trip2.tripId}/unload`, { sealsOk: true });
await P(d2, `/api/trips/${trip2.tripId}/deliver`, { meterBefore: 54500, meterAfter: 59000, received: 4500, leftInTank: 0, returned: 0, receiptNo: `RC-${tag}-2`, hasMoreStops: false });
hub = (await G(sup, `/api/orders/${sOrder.id}`)).j;
const T = hub.order.totals;
ok(hub.order.status === 'Delivered' && T.loaded === 7500 && T.delivered === 7490 && T.receipt === 7490 && T.remaining === 0, `both trucks delivered → «${hub.order.statusLabel}»: loaded ${T.loaded}, delivered ${T.delivered}, receipt ${T.receipt}`);
ok(T.difference === -10 && T.differenceFlag === false && T.tolerance === 40, `difference loaded vs delivered ${T.difference} L within tolerance ${T.tolerance}`);
ok(hub.trips[1].receiptNo === `RC-${tag}-2` && hub.trips[0].delivered === 2990 && hub.trips[0].receipt === 2990, 'per-trip delivered (meter), receipt quantity and receipt no.');
ok(hub.money && hub.money.ratePerLiter === 1.82 && Math.abs(hub.money.revenueEstimate - 13631.8) < 0.01, `revenue estimate 7,490 × (0.12 + 1.70) = ${hub.money?.revenueEstimate}`);
ok(hub.history.some(h => h.action === 'تغيّر حالة الطلب' && h.newValue === 'Delivered'), 'status changes are in the order history');

// ── permissions: Costs hides money; company isolation ────────────────────────────────────────────────────────────
const rh = await G(reader, `/api/orders/${sOrder.id}`);
ok(rh.s === 200 && rh.j.money === null && rh.j.order.money === null, 'reader without Costs sees the order but no prices or revenue');
const rl = (await G(reader, '/api/orders')).j;
ok(rl.items.length >= 2 && rl.items.every(x => x.money === null), 'list hides prices without Costs');
ok((await G(gulf, `/api/orders/${sOrder.id}`)).s === 404, 'other company cannot open the order (404)');
ok(!(await G(gulf, '/api/orders')).j.items.some(x => x.id === sOrder.id), 'other company does not see it in the list');
ok(!(await G(gulf, '/api/dispatch')).j.orders.some(x => x.id === tOrder.id), 'other company does not see it on the dispatch board');
ok((await P(gulf, `/api/orders/${sOrder.id}/trips`, { vehicleId: V1.id, stationId: aramco.id, sourceRef: 'X', qty: 10 })).s === 404, 'other company cannot dispatch on it');

// ── late flag ─────────────────────────────────────────────────────────────────────────────────────────────────────
r = await P(sup, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 5000, priority: 'Scheduled', requestedFrom: day(-3), requestedTo: day(-1), customerRef: `LATE-${tag}` });
const lOrder = r.j.order;
ok(lOrder.totals.late === true, 'order whose window ended with quantity missing is late');
ok((await G(sup, '/api/orders?late=true')).j.items.some(x => x.id === lOrder.id) && !(await G(sup, '/api/orders?late=true')).j.items.some(x => x.id === tOrder.id), 'late-only filter');
ok((await G(sup, `/api/orders?q=LATE-${tag}`)).j.items.length === 1, 'search by customer PO no.');
ok((await G(sup, `/api/orders?q=${sOrder.no}`)).j.items[0]?.id === sOrder.id, 'search by order no.');
ok((await G(sup, '/api/orders?type=TransportAndSupply&priority=Urgent')).j.items.every(x => x.type === 'TransportAndSupply' && x.priority === 'Urgent'), 'filter by type and priority');
const board = (await G(sup, '/api/dispatch')).j;
const pri = board.orders.map(o => o.priority);
ok(board.orders.length >= 2 && (pri.indexOf('Scheduled') === -1 || pri.lastIndexOf('Urgent') < pri.indexOf('Scheduled')), 'dispatch board: urgent orders first');
ok(board.orders.some(o => o.id === lOrder.id && o.late && o.toDispatch === 5000) && !board.orders.some(o => o.id === sOrder.id), 'board lists open orders with quantity left (delivered order gone), late flagged');
ok(board.trucks.find(t => t.id === V1.id)?.driver === dr1.name, 'board shows the truck ↔ driver assignment');

// ── expired document refuses dispatch; override with reason is audited ─────────────────────────────────────────────
const meta = (await G(admin, '/api/documents/meta')).j;
const insurance = meta.types.find(t => t.name === 'التأمين');
const expiredOn = ksaDay(-3);
r = await P(admin, '/api/documents', { typeId: insurance.id, ownerId: V1.id, number: `POL-${tag}`, issuer: 'التعاونية', issueDate: ksaDay(-368), expiry: expiredOn });
ok(r.s === 200, 'insurance of truck 1 registered, expired 3 days ago');
const expectMsg = `لا يمكن إسناد الرحلة: وثيقة «التأمين» للمركبة ${V1.plate} منتهية بتاريخ ${expiredOn}. للإسناد رغم ذلك اكتب سبب التجاوز.`;
r = await P(sup, `/api/orders/${lOrder.id}/trips`, { vehicleId: V1.id, stationId: aramco.id, sourceRef: `AR-${tag}-3`, qty: 5000 });
ok(r.s === 400 && r.j.error === expectMsg, 'dispatch refused with the exact document: ' + r.j.error);
const boardV1 = (await G(sup, '/api/dispatch')).j.trucks.find(t => t.id === V1.id);
ok(boardV1.blocked && boardV1.states.includes('وثيقة منتهية'), 'board marks the truck «وثيقة منتهية»');
r = await P(sup, `/api/orders/${lOrder.id}/trips`, { vehicleId: V1.id, stationId: aramco.id, sourceRef: `AR-${tag}-3`, qty: 5000, overrideReason: 'التجديد قيد الإصدار — موافقة مدير التشغيل' });
const trip3 = r.j;
ok(r.s === 200 && trip3.warnings.some(w => w.startsWith('تجاوز:')), 'operations editor overrides with a written reason');
hub = (await G(sup, `/api/orders/${lOrder.id}`)).j;
ok(hub.history.some(h => h.action === 'تجاوز جاهزية الوثائق' && h.detail.includes('موافقة مدير التشغيل') && h.detail.includes('«التأمين»')), 'override audited on the order with reason and document');
tv = (await G(sup, `/api/trips/${trip3.tripId}`)).j;
ok(tv.audit.some(h => h.action === 'تجاوز جاهزية الوثائق'), 'override audited on the trip too');

// ── close / cancel rules ──────────────────────────────────────────────────────────────────────────────────────────
r = await P(sup, `/api/orders/${sOrder.id}/cancel`, { reason: 'طلب العميل' });
ok(r.s === 400 && r.j.error.includes('بعد بدء التنفيذ'), 'cannot cancel an order whose trips started: ' + r.j.error);
ok((await P(sup, `/api/orders/${sOrder.id}/close`, {})).s === 400, 'close without reason refused');
ok((await P(reader, `/api/orders/${sOrder.id}/close`, { reason: 'اكتمل التسليم' })).s === 403, 'reader cannot close');
r = await P(sup, `/api/orders/${sOrder.id}/close`, { reason: 'اكتمل التسليم وتمت المطابقة' });
ok(r.s === 200 && r.j.order.status === 'Closed' && r.j.order.statusReason === 'اكتمل التسليم وتمت المطابقة', 'delivered order closed with reason');
r = await P(sup, `/api/orders/${sOrder.id}/trips`, { vehicleId: V2.id, stationId: aramco.id, sourceRef: 'X', qty: 100 });
ok(r.s === 400 && r.j.error.includes('مغلق'), 'no dispatch on a closed order: ' + r.j.error);
ok((await call(sup, 'PUT', `/api/orders/${sOrder.id}`, { ...sOrder, customerId: C2.id, reason: 'تعديل' })).s === 400, 'no edit on a closed order');
r = await P(sup, `/api/orders/${lOrder.id}/close`, { reason: 'إنهاء' });
ok(r.s === 400 && r.j.error.includes(trip3.tripNo), 'cannot close while a trip has not finished unloading: ' + r.j.error);
r = await P(sup, `/api/orders/${lOrder.id}/cancel`, { reason: 'ألغى العميل الطلب' });
ok(r.s === 200 && r.j.order.status === 'Cancelled', 'order with a trip not yet loaded can be cancelled');
tv = (await G(sup, `/api/trips/${trip3.tripId}`)).j;
ok(tv.view.trip.status === 'Cancelled', 'its waiting trip is cancelled with it');
ok((await P(sup, `/api/orders/${lOrder.id}/cancel`, { reason: 'مرة أخرى' })).s === 400, 'cancelled order cannot be cancelled again');

// ── edit with reason (old/new in the history) ────────────────────────────────────────────────────────────────────
const edit = { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 15000, priority: 'Urgent', requestedFrom: day(-1), requestedTo: day(6), customerRef: `PO-${tag}-B`, transportRatePerLiter: 0.13 };
ok((await call(sup, 'PUT', `/api/orders/${tOrder.id}`, edit)).s === 400, 'edit without a reason refused');
r = await call(sup, 'PUT', `/api/orders/${tOrder.id}`, { ...edit, reason: 'العميل زاد الكمية' });
const h = r.j.history?.find(x => x.action === 'تعديل طلب العميل');
ok(r.s === 200 && r.j.order.qtyRequested === 15000 && h && h.oldValue.includes('12000') && h.newValue.includes('15000') && h.detail === 'العميل زاد الكمية', 'edit audited with reason, old and new values');

// ── legacy trip linking ──────────────────────────────────────────────────────────────────────────────────────────
const legacy = (await P(sup, '/api/trips', { customerId: C1.id, stationId: aramco.id, sourceRef: `LEG-${tag}`, product: 'ديزل', qtyRequested: 2000 })).j.trip;
const other = (await P(sup, '/api/trips', { customerId: C2.id, driverId: dr2.id, stationId: aramco.id, sourceRef: `LEG2-${tag}`, product: 'ديزل', qtyRequested: 1000 })).j.trip;
ok(legacy.orderId == null, 'a trip created the old way has no order');
ok((await G(sup, `/api/orders/${tOrder.id}/linkable`)).j.some(x => x.id === legacy.id), 'legacy trip offered for linking (same customer)');
r = await P(sup, `/api/orders/${tOrder.id}/link-trip`, { tripId: other.id });
ok(r.s === 400 && r.j.error.includes('لعميل آخر'), 'trip of another customer cannot be linked');
r = await P(sup, `/api/orders/${tOrder.id}/link-trip`, { tripId: legacy.id });
ok(r.s === 200 && r.j.trips.some(t => t.id === legacy.id) && r.j.order.status === 'Planned', 'legacy trip linked; order becomes «مخطط»');
ok((await G(sup, `/api/trips/${legacy.id}`)).j.view.orderNo === tOrder.no, 'linked trip shows the order no.');
const o2 = (await P(sup, '/api/orders', { customerId: C1.id, type: 'Transport', product: 'ديزل', qtyRequested: 100, priority: 'Scheduled' })).j.order;
ok((await P(sup, `/api/orders/${o2.id}/link-trip`, { tripId: legacy.id })).s === 400, 'a trip already linked to an order cannot be linked to another');
ok((await P(reader, `/api/orders/${tOrder.id}/link-trip`, { tripId: legacy.id })).s === 403, 'reader cannot link trips');
ok((await P(gulf, `/api/orders/${tOrder.id}/link-trip`, { tripId: legacy.id })).s === 404, 'other company cannot link');
