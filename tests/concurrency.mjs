// Two simultaneous loads on the same meter: exactly one must succeed (row lock in PostgreSQL).
import { call, token } from './fuauth.mjs';
const j = call;
const login = token;
const sup=await login('supervisor'),d2=await login('driver2');
const cs=(await j(sup,'GET','/api/master/customers')).j.find(c=>c.name==='مصنع الدمام');const st=(await j(sup,'GET','/api/master/stations')).j[0];
const me=(await j(d2,'GET','/api/me')).j;const v=me.vehicles[0];
const a=(await j(sup,'POST','/api/trips',{customerId:cs.id,stationId:st.id,sourceRef:'C1',product:'ديزل',qtyRequested:1000})).j.trip;
const b=(await j(sup,'POST','/api/trips',{customerId:cs.id,stationId:st.id,sourceRef:'C2',product:'ديزل',qtyRequested:1000})).j.trip;
const body={vehicleId:v.id,qty:1000,docNo:'X',odometer:1,meterStart:500000,meterEnd:501000,meterBaseline:500000};
const [r1,r2]=await Promise.all([j(d2,'POST',`/api/trips/${a.id}/load`,{...body,docNo:'C1'}),j(d2,'POST',`/api/trips/${b.id}/load`,{...body,docNo:'C2'})]);
console.log('statuses',r1.s,r2.s,'|',(r1.j.error||r2.j.error||'').slice(0,80));
const led=(await j(sup,'GET',`/api/meters/${v.meterId}/ledger`)).j;console.log('ledger readings',led.readings.map(r=>r.reading).join(','),'last',led.last,'baselinePending',led.baselinePending);

const okc=[r1.s,r2.s].sort().join(',')==='200,400';console.log((okc?'PASS':'FAIL')+' concurrent loads on one meter: one accepted, one rejected');if(!okc)process.exitCode=1;

// 30 orders created at the same instant (several dispatchers): all must succeed with distinct numbers (load-test finding).
const burst = await Promise.all(Array.from({ length: 30 }, (_, i) => j(sup, 'POST', '/api/trips', { customerId: cs.id, stationId: st.id, sourceRef: `PAR-${i}`, product: 'ديزل', qtyRequested: 100 })));
const nos = burst.filter(r => r.s === 200).map(r => r.j.trip.no);
const okp = nos.length === 30 && new Set(nos).size === 30;
console.log((okp ? 'PASS' : 'FAIL') + ` 30 simultaneous orders: ${nos.length} created, ${new Set(nos).size} distinct numbers (${burst.filter(r => r.s !== 200).map(r => r.s).join(',') || 'no errors'})`);
if (!okp) process.exitCode = 1;
