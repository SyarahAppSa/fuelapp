// «لوحة التوزيع»: pick an open order, pick a truck (its assigned driver comes with it), then the loading station, loading request no. and quantity.
// Readiness badges come from the server; expired mandatory documents need a written override reason (the server enforces and audits it).
import { useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, fmt, post } from '../api'
import { Field, Panel } from '../ui'
import { dateOnly, fromLocalInput, type OrderStatus, type Priority } from '../orders'

interface DocBadge { type: string; mandatory: boolean; status: 'soon' | 'expired'; label: string; expiry?: string }
interface Board {
  orders: { id: string; no: string; customerName: string; site: string; customerRef?: string; typeLabel: string; priority: Priority; priorityLabel: string; status: OrderStatus; statusLabel: string; product: string; qtyRequested: number; requestedTo?: string; toDispatch: number; remaining: number; late: boolean; trips: number }[]
  trucks: { id: string; plate: string; trailerPlate?: string; internalNo?: string; capacity: number; driverId?: string; driver?: string; state: string; states: string[]; available: boolean; blocked: boolean; openTrip?: string; workOrder?: string; documents: DocBadge[] }[]
  drivers: { id: string; name: string; empNo?: string; plate?: string; state: string; states: string[]; available: boolean; present: boolean; blocked: boolean; openTrip?: string; documents: DocBadge[] }[]
  stations: { id: string; name: string; type: string }[]
  canDispatch: boolean
}

const stateChip = (s: string) => s === 'متاحة' || s === 'متاح' ? 'st-Approved' : s === 'حاضر اليوم' ? 'st-Loaded' : s === 'في رحلة' ? 'st-EnRoute' : s === 'في الصيانة' || s === 'وثيقة تقارب الانتهاء' ? 'st-AwaitingApproval' : 'st-UnderReview'

function Docs({ list }: { list: DocBadge[] }) {
  return <>{list.map((d, i) => <div key={i} className={d.status === 'expired' ? 'diff' : 'sub'} style={{ fontSize: 12 }}>{d.type}: {d.label} {dateOnly(d.expiry)}</div>)}</>
}

export default function Dispatch() {
  const [params] = useSearchParams()
  const qc = useQueryClient()
  const [orderId, setOrderId] = useState<string | null>(params.get('order'))
  const [truckId, setTruckId] = useState<string | null>(null)
  const [needOverride, setNeedOverride] = useState(false)
  const [result, setResult] = useState<{ tripId: string; tripNo: string; warnings: string[] } | null>(null)
  const board = useQuery({ queryKey: ['dispatch'], queryFn: () => api<Board>('/api/dispatch'), refetchInterval: 60_000 })
  const send = useMutation({
    mutationFn: (body: Record<string, unknown>) => post<{ tripId: string; tripNo: string; warnings: string[] }>(`/api/orders/${orderId}/trips`, body),
    onSuccess: (r) => { setResult(r); setTruckId(null); setNeedOverride(false); qc.invalidateQueries() },
    onError: (e) => { if ((e as Error).message.includes('سبب التجاوز')) setNeedOverride(true) },
  })
  const b = board.data
  if (board.isError) return <div className="err">{(board.error as Error).message}</div>
  if (!b) return <div className="sub">جارٍ التحميل…</div>
  const order = b.orders.find(o => o.id === orderId)
  const truck = b.trucks.find(t => t.id === truckId)
  const driverOf = truck?.driverId ? b.drivers.find(d => d.id === truck.driverId) : undefined
  const blocked = !!truck?.blocked || !!driverOf?.blocked

  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const v = (k: string) => { const s = String(f.get(k) ?? '').trim(); return s === '' ? undefined : s }
    setResult(null)
    send.mutate({ vehicleId: truckId, driverId: v('driverId'), stationId: v('stationId'), sourceRef: v('sourceRef'), qty: Number(v('qty')), eta: fromLocalInput(v('eta')), overrideReason: v('overrideReason') })
  }

  return (
    <>
      <h1>لوحة التوزيع</h1>
      {result && <div className="okmsg" role="status" style={{ margin: '10px 0' }}>أُسندت الرحلة <Link to={`/trips?open=${result.tripId}`} className="num">{result.tripNo}</Link> وأُرسل إشعار للسائق.
        {result.warnings.map((w, i) => <div key={i} style={{ color: 'var(--warn)' }}>تنبيه: {w}</div>)}</div>}
      <div className="dispatch">
        <Panel title={`الطلبات المفتوحة (${b.orders.length})`}>
          <div className="tblw"><table>
            <thead><tr><th>الطلب</th><th>العميل</th><th>للإسناد</th><th>الموعد</th></tr></thead>
            <tbody>{b.orders.map(o => (
              <tr key={o.id} className={`click ${o.id === orderId ? 'picked' : ''} ${o.late ? 'flag' : ''}`} onClick={() => { setOrderId(o.id); setResult(null); send.reset() }}>
                <td className="num"><b>{o.no}</b><div className="row" style={{ gap: 4 }}><span className={`chip ${o.priority === 'Urgent' ? 'st-UnderReview' : 'st-WaitingLoad'}`}>{o.priorityLabel}</span>{o.late && <span className="chip st-UnderReview">متأخر</span>}</div></td>
                <td style={{ whiteSpace: 'normal' }}>{o.customerName}<div className="sub">{o.site} · {o.typeLabel}</div></td>
                <td className="num">{fmt(o.toDispatch)}<div className="sub">من {fmt(o.qtyRequested)} · {o.trips} رحلة</div></td>
                <td className="num">{dateOnly(o.requestedTo)}</td>
              </tr>))}</tbody>
          </table></div>
          {b.orders.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد طلبات بحاجة إلى إسناد.</div>}
        </Panel>
        <div>
          <Panel title="الشاحنات">
            <div className="tblw"><table>
              <thead><tr><th>الشاحنة</th><th>السائق المسند</th><th>الحالة</th><th>الوثائق</th></tr></thead>
              <tbody>{b.trucks.map(t => (
                <tr key={t.id} className={`click ${t.id === truckId ? 'picked' : ''}`} onClick={() => { setTruckId(t.id); setNeedOverride(false); send.reset() }}>
                  <td className="num"><b>{t.plate}</b><div className="sub">{t.trailerPlate ? `مقطورة ${t.trailerPlate} · ` : ''}{fmt(t.capacity)} لتر</div></td>
                  <td>{t.driver ?? <span className="sub">—</span>}</td>
                  <td><div className="row" style={{ gap: 4 }}>{t.states.map(s => <span key={s} className={`chip ${stateChip(s)}`}>{s}</span>)}</div>
                    <div className="sub num">{[t.openTrip, t.workOrder].filter(Boolean).join(' · ')}</div></td>
                  <td style={{ whiteSpace: 'normal', minWidth: 160 }}><Docs list={t.documents} /></td>
                </tr>))}</tbody>
            </table></div>
          </Panel>
          <Panel title="السائقون">
            <div className="tblw"><table>
              <thead><tr><th>السائق</th><th>الشاحنة</th><th>الحالة</th><th>الوثائق</th></tr></thead>
              <tbody>{b.drivers.map(d => (
                <tr key={d.id}><td>{d.name}<div className="sub num">{d.empNo}</div></td><td className="num">{d.plate ?? '—'}</td>
                  <td><div className="row" style={{ gap: 4 }}>{d.states.map(s => <span key={s} className={`chip ${stateChip(s)}`}>{s}</span>)}</div><div className="sub num">{d.openTrip}</div></td>
                  <td style={{ whiteSpace: 'normal', minWidth: 160 }}><Docs list={d.documents} /></td></tr>))}</tbody>
            </table></div>
          </Panel>
        </div>
      </div>

      {b.canDispatch && (
        <Panel title="إسناد رحلة">
          {!order || !truck ? <div className="sub" style={{ padding: 16 }}>اختر طلبًا من القائمة ثم شاحنة.</div> : (
            <form className="filter" style={{ boxShadow: 'none' }} onSubmit={submit} key={`${orderId}-${truckId}`}>
              <div className="sub" style={{ marginBottom: 8 }}>الطلب <b className="num">{order.no}</b> — {order.customerName} · الشاحنة <b className="num">{truck.plate}</b>
                {!truck.available && <span className="diff"> · {truck.states.join('، ')}</span>}</div>
              <div className="fg">
                <Field label="السائق"><select name="driverId" defaultValue={truck.driverId ?? ''}><option value="">— السائق المسند للشاحنة —</option>
                  {b.drivers.map(d => <option key={d.id} value={d.id}>{d.name} · {d.state}</option>)}</select></Field>
                <Field label="موقع التعبئة *"><select name="stationId" required><option value="">اختر</option>{b.stations.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></Field>
                <Field label="رقم طلب التحميل *"><input name="sourceRef" required maxLength={60} /></Field>
                <Field label="الكمية (لتر) *"><input name="qty" type="number" min={1} max={200000} required defaultValue={Math.min(order.toDispatch || order.qtyRequested, truck.capacity || order.qtyRequested)} /></Field>
                <Field label="الوصول المتوقع"><input name="eta" type="datetime-local" /></Field>
              </div>
              {(blocked || needOverride) && (
                <div className="card" style={{ padding: 12, marginTop: 10, boxShadow: 'inset 0 0 0 1.5px var(--crit)' }}>
                  <div className="diff">وثيقة إلزامية منتهية: {[...truck.documents, ...(driverOf?.documents ?? [])].filter(d => d.status === 'expired' && d.mandatory).map(d => `${d.type} (${dateOnly(d.expiry)})`).join('، ') || 'راجع رسالة النظام'}</div>
                  <Field label="سبب التجاوز (يُسجّل في سجل الطلب والرحلة)"><textarea name="overrideReason" maxLength={500} rows={2} /></Field>
                </div>)}
              {send.error && <div className="err" role="alert" style={{ marginTop: 8 }}>{(send.error as Error).message}</div>}
              <div className="actions"><button className="btn pri" disabled={send.isPending}>إسناد الرحلة</button></div>
            </form>)}
        </Panel>)}
    </>
  )
}
