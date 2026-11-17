// Truck file: all parts installed now, replacement history, maintenance cost, and the custody held by its current driver.
// Installed parts belong to the truck; the driver's custody is listed separately (a driver change does not move parts).
import { useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, fmt, ksa } from '../api'
import { Field, Panel } from '../ui'
import { day, money, useMeta, type Movement, type Part } from '../custody'
import { MovesTable, PartsTable, StatusChip } from './Custody'

interface File {
  vehicle: { id: string; plate: string; internalNo?: string; make?: string; model?: string; year?: number; vin?: string; trailerPlate?: string }
  driver?: { id: string; name: string; empNo?: string; phone?: string }; lastKm?: number; meter?: { serial: string; kind?: string; lastCalibration?: string; nextCalibration?: string }
  installed: { part: Part; kmSinceInstall?: number; daysInstalled?: number }[]; replacements: Movement[]; movements: Movement[]
  workOrders: { id: string; no: string; type: string; description: string; closed: boolean; openedAt: string; closedAt?: string; laborCost?: number; partsCost?: number }[]
  costs?: { parts: number; labor: number; total: number }; driverCustody?: { parts: Part[]; quantities: { catalogId: string; code: string; name: string; qty: number; since: string }[] }
}

export default function TruckFile() {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const meta = useMeta()
  const [range, setRange] = useState({ from: '', to: '' })
  const qs = new URLSearchParams(Object.entries(range).filter(([, v]) => v)).toString()
  const q = useQuery({ queryKey: ['custody', 'truck', id, qs], queryFn: () => api<File>(`/api/custody/vehicles/${id}?${qs}`) })
  const f = q.data
  if (q.isError) return <div className="err">{(q.error as Error).message}</div>
  if (!f) return <div className="sub">جارٍ التحميل…</div>
  const v = f.vehicle
  const byCat = new Map<string, File['installed']>()
  for (const x of f.installed) { const k = x.part.category ?? 'أخرى'; byCat.set(k, [...(byCat.get(k) ?? []), x]) }
  return (
    <>
      <div className="row noprint"><button className="btn" onClick={() => nav(-1)}>← رجوع</button><Link className="btn" to="/custody">العُهد وقطع الغيار</Link><button className="btn" onClick={() => window.print()}>طباعة / PDF</button></div>
      <section className="hero" style={{ marginTop: 14 }}>
        <div><div>ملف الشاحنة</div><div className="big">{v.plate}</div><div>{v.make} {v.model} {v.year ?? ''}{v.internalNo ? ` · ${v.internalNo}` : ''}{v.trailerPlate ? ` · مقطورة ${v.trailerPlate}` : ''}</div>
          <div className="num">{f.meter ? `العداد ${f.meter.serial} · المعايرة القادمة ${day(f.meter.nextCalibration)}` : 'بدون عداد مسجل'}</div></div>
        <div className="tiles">
          <div className="tile"><div>السائق الحالي</div><div className="v" style={{ fontSize: 18 }}>{f.driver?.name ?? '—'}</div><div className="num">{f.driver?.empNo ?? ''} {f.driver?.phone ?? ''}</div></div>
          <div className="tile"><div>آخر قراءة عداد معروفة</div><div className="v">{fmt(f.lastKm)} كم</div><div>{f.installed.length} قطعة مركّبة</div></div>
          {f.costs && <div className="tile"><div>تكلفة الصيانة {range.from || range.to ? 'للفترة' : '(الكل)'}</div><div className="v">{money(f.costs.total)}</div><div className="num">قطع {money(f.costs.parts)} · أجور {money(f.costs.labor)}</div></div>}
          <div className="tile"><div>أوامر الصيانة</div><div className="v">{f.workOrders.length}</div><div>مفتوحة {f.workOrders.filter(w => !w.closed).length}</div></div>
        </div>
      </section>
      <Panel title="القطع المركّبة حاليًا">
        {[...byCat.entries()].map(([cat, rows]) => (
          <div key={cat}><div className="hd sub" style={{ padding: '8px 14px', fontWeight: 800 }}>{cat} ({rows.length})</div>
            <div className="tblw"><table>
              <thead><tr><th>الموضع</th><th>رقم القطعة</th><th>الصنف</th><th>الرقم التسلسلي</th><th>تاريخ التركيب</th><th>عداد التركيب</th><th>كم منذ التركيب</th><th>أيام</th><th>الضمان</th><th>الفحص القادم</th></tr></thead>
              <tbody>{rows.map(({ part: p, kmSinceInstall, daysInstalled }) => (
                <tr key={p.id} className="click" onClick={() => nav(`/custody/part/${p.id}`)}><td>{p.position ?? '—'}</td><td className="num"><b>{p.internalNo}</b></td><td>{p.name}<div className="sub">{p.brand} {p.model}</div></td>
                  <td className="num">{p.serial ?? '—'}</td><td className="num">{ksa(p.installedAt)}</td><td className="num">{fmt(p.installKm)}</td><td className="num">{fmt(kmSinceInstall)}</td><td className="num">{daysInstalled ?? ''}</td>
                  <td className={p.warranty === 'ينتهي قريبًا' ? 'diff' : ''}>{p.warranty}</td><td className="num">{day(p.nextInspection)}</td></tr>))}</tbody>
            </table></div></div>))}
        {f.installed.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد قطع مركّبة مسجلة.</div>}
      </Panel>
      <Panel title={`عهدة السائق الحالي${f.driver ? ` — ${f.driver.name}` : ''} (منفصلة عن الشاحنة)`}>
        {f.driverCustody ? <>
          <PartsTable parts={f.driverCustody.parts} />
          {f.driverCustody.quantities.length > 0 && <div className="tblw"><table><thead><tr><th>الصنف</th><th>الكمية</th><th>منذ</th></tr></thead>
            <tbody>{f.driverCustody.quantities.map(x => <tr key={x.catalogId}><td>{x.code} · {x.name}</td><td className="num">{x.qty}</td><td className="num">{ksa(x.since)}</td></tr>)}</tbody></table></div>}
          {f.driverCustody.parts.length + f.driverCustody.quantities.length === 0 && <div className="sub" style={{ padding: 16 }}>لا عهد على السائق.</div>}
        </> : <div className="sub" style={{ padding: 16 }}>لا يوجد سائق مسند للشاحنة.</div>}
      </Panel>
      <Panel title="سجل التركيب والاستبدال"><MovesTable rows={f.replacements} /></Panel>
      <Panel title="أوامر الصيانة والتكلفة" actions={meta.data?.canSeeCost && <div className="row noprint">
        <Field label="من"><input type="date" value={range.from} onChange={e => setRange({ ...range, from: e.target.value })} /></Field>
        <Field label="إلى"><input type="date" value={range.to} onChange={e => setRange({ ...range, to: e.target.value })} /></Field></div>}>
        <div className="tblw"><table>
          <thead><tr><th>الرقم</th><th>النوع</th><th>الوصف</th><th>فُتح</th><th>الحالة</th>{meta.data?.canSeeCost && <><th>القطع</th><th>الأجور</th></>}</tr></thead>
          <tbody>{f.workOrders.map(w => <tr key={w.id}><td className="num"><b>{w.no}</b></td><td>{w.type}</td><td style={{ whiteSpace: 'normal' }}>{w.description}</td><td className="num">{ksa(w.openedAt)}</td>
            <td><span className={`chip ${w.closed ? 'st-Approved' : 'st-AwaitingApproval'}`}>{w.closed ? 'مغلق' : 'مفتوح'}</span></td>{meta.data?.canSeeCost && <><td className="num">{money(w.partsCost)}</td><td className="num">{money(w.laborCost)}</td></>}</tr>)}</tbody>
        </table></div>
      </Panel>
      <Panel title="كل حركات الشاحنة"><MovesTable rows={f.movements} /></Panel>
      <div className="sub" style={{ marginTop: 8 }}>الحالات: {meta.data?.statuses.map(s => <span key={s.value} style={{ marginInlineEnd: 6 }}><StatusChip s={s.value} label={s.label} /></span>)}</div>
    </>
  )
}
