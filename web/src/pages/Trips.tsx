import { useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router-dom'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, can, fmt, ksa, post, signed, type Paged, type Trip, type TripStatus, type TripView } from '../api'
import { useMe } from '../App'
import { Field, formData, useAction, useCustomers, useDrivers, useStations, useVehicles } from '../ui'

const FILTERS: [TripStatus | '', string][] = [['', 'الكل'], ['EnRoute', 'في الطريق'], ['AwaitingApproval', 'بانتظار الاعتماد'], ['UnderReview', 'تحت المراجعة'], ['Approved', 'المعتمدة']]

export default function Trips() {
  const [status, setStatus] = useState<TripStatus | ''>('')
  const [page, setPage] = useState(1)
  const [params, setParams] = useSearchParams()
  const open = params.get('open')
  const setOpen = (id: string | null) => setParams(id ? { open: id } : {})
  const { data, isLoading } = useQuery({ queryKey: ['trips', status, page], queryFn: () => api<Paged<TripView>>(`/api/trips?page=${page}&size=20${status ? '&status=' + status : ''}`) })
  const pages = data ? Math.max(1, Math.ceil(data.total / data.size)) : 1
  const { data: me } = useMe()
  const [creating, setCreating] = useState(false)
  return (
    <>
      <div className="spread"><h1>الرحلات</h1>
        {can(me, 'Ops', 'Edit') && <button className="btn pri" onClick={() => setCreating(!creating)}>{creating ? 'إغلاق' : 'طلب نقل جديد'}</button>}</div>
      {creating && <NewTrip onDone={() => setCreating(false)} />}
      <div className="spread" style={{ marginTop: 10 }}><span /><div className="row">{FILTERS.map(([k, l]) => <button key={k} className={`btn ${status === k ? 'pri' : ''}`} onClick={() => { setStatus(k); setPage(1) }}>{l}</button>)}</div></div>
      <section className="panel" style={{ marginTop: 14 }}>
        {isLoading ? <div className="sub" style={{ padding: 16 }}>جارٍ التحميل…</div> : (
          <div className="tblw"><table>
            <thead><tr><th>رقم الرحلة</th><th>طلب العميل</th><th>الحالة</th><th>العميل</th><th>السائق / المركبة</th><th>المحمّلة</th><th>المسلّمة (عداد)</th><th>حسب السند</th><th>الفرق</th></tr></thead>
            <tbody>{data?.items.map(({ trip: t, reconciliation: c, statusLabel, orderNo }) => {
              const bad = c.unsettled != null && Math.abs(c.unsettled) > c.tolerance
              return (
                <tr key={t.id} className="click" onClick={() => setOpen(t.id)}>
                  <td className="num">{t.no}</td><td className="num">{orderNo && t.orderId ? <Link to={`/orders/${t.orderId}`} onClick={e => e.stopPropagation()}>{orderNo}</Link> : <span className="sub">—</span>}</td>
                  <td><span className={`chip st-${t.status}`}>{statusLabel}</span></td><td>{t.snapshot?.customerName}</td>
                  <td>{t.snapshot?.driverName}<div className="sub num">{t.snapshot?.plate}</div></td>
                  <td className="num">{fmt(c.loaded)}</td><td className="num">{fmt(c.delivered)}</td><td className="num">{fmt(c.receipt)}</td><td className={`num ${bad ? 'diff' : ''}`}>{signed(c.unsettled)}</td>
                </tr>)
            })}</tbody>
          </table></div>)}
        {pages > 1 && <div className="pager">{Array.from({ length: pages }, (_, i) => i + 1).map(p => <button key={p} className={p === page ? 'on' : ''} onClick={() => setPage(p)}>{p}</button>)}</div>}
      </section>
      {open && <TripDrawer id={open} onClose={() => setOpen(null)} />}
    </>
  )
}

/** A dispatcher creates a transport order; with no driver chosen, the customer's default driver gets it (server rule). */
function NewTrip({ onDone }: { onDone: () => void }) {
  const act = useAction()
  const customers = useCustomers(), stations = useStations(), drivers = useDrivers(), vehicles = useVehicles()
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const body = formData(e.currentTarget, ['qtyRequested'])
    act.run(async () => { await post('/api/trips', body); onDone() }, 'أُنشئ الطلب وأُرسل إشعار للسائق.')
  }
  return (
    <form className="filter" onSubmit={submit} style={{ marginTop: 12 }}>
      {act.banner}
      <div className="fg">
        <Field label="العميل *"><select name="customerId" required><option value="">اختر</option>{customers.data?.map(c => <option key={c.id} value={c.id}>{c.name} — {c.site}</option>)}</select></Field>
        <Field label="موقع التعبئة *"><select name="stationId" required><option value="">اختر</option>{stations.data?.map(s => <option key={s.id} value={s.id}>{s.name}</option>)}</select></Field>
        <Field label="السائق (افتراضي العميل إن تُرك)"><select name="driverId"><option value="">— السائق الافتراضي —</option>{drivers.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
        <Field label="المركبة"><select name="vehicleId"><option value="">— يحددها السائق —</option>{vehicles.data?.map(v => <option key={v.id} value={v.id}>{v.plate}</option>)}</select></Field>
        <Field label="رقم طلب التحميل *"><input name="sourceRef" required maxLength={60} /></Field>
        <Field label="المنتج *"><input name="product" required defaultValue="ديزل" maxLength={40} /></Field>
        <Field label="الكمية المطلوبة (لتر) *"><input name="qtyRequested" type="number" min={1} max={200000} required /></Field>
      </div>
      <div className="actions"><button className="btn pri" disabled={act.busy}>إنشاء الطلب</button></div>
    </form>
  )
}

function TripDrawer({ id, onClose }: { id: string; onClose: () => void }) {
  const qc = useQueryClient()
  const { data: me } = useMe()
  const [note, setNote] = useState('')
  const { data } = useQuery({ queryKey: ['trip', id], queryFn: () => api<{ view: TripView; audit: { at: string; action: string; detail?: string }[] }>(`/api/trips/${id}`) })
  const act = useMutation({
    mutationFn: (kind: 'approve' | 'resolve') => post(`/api/trips/${id}/${kind}`, { note }),
    onSuccess: () => { qc.invalidateQueries({ queryKey: ['trips'] }); qc.invalidateQueries({ queryKey: ['trip', id] }) },
  })
  if (!data) return null
  const { trip: t, reconciliation: c, statusLabel, orderNo, customerRef } = data.view
  const canApprove = can(me, 'Approve', 'Edit')
  return (
    <div className="drawer-scrim" onClick={e => e.target === e.currentTarget && onClose()}>
      <div className="drawer" role="dialog" aria-label={`الرحلة ${t.no}`}>
        <div className="spread"><div className="row"><h2 className="num">{t.no}</h2><span className={`chip st-${t.status}`}>{statusLabel}</span></div><button className="btn" onClick={onClose}>إغلاق</button></div>
        {orderNo && t.orderId && <div className="card" style={{ padding: 12 }}>ضمن طلب العميل <Link className="num" to={`/orders/${t.orderId}`}><b>{orderNo}</b></Link>{customerRef && <span className="sub"> · أمر الشراء {customerRef}</span>} · {t.snapshot?.customerName}</div>}
        <div className="q3">
          <div className="q"><div className="sub">المحمّلة</div><div className="v">{fmt(c.loaded)}</div><div className="sub">سند {t.loading?.docNo ?? '—'}</div></div>
          <div className="q"><div className="sub">المسلّمة (عداد التفريغ)</div><div className="v">{fmt(c.delivered)}</div></div>
          <div className="q"><div className="sub">حسب سند الاستلام</div><div className="v">{fmt(c.receipt)}</div></div>
        </div>
        <div className="card" style={{ padding: 12 }}>الفرق غير المسوّى: <b className="num">{signed(c.unsettled)}</b> لتر · حد التنبيه <span className="num">{fmt(c.tolerance)}</span></div>
        {t.reviewReason && <div className="card" style={{ padding: 12, color: 'var(--crit)' }}>سبب المراجعة: {t.reviewReason}</div>}
        {t.loading?.meterStart != null && <div className="card" style={{ padding: 12 }}>عداد التعبئة: <span className="num">{fmt(t.loading.meterStart)} ← {fmt(t.loading.meterEnd)}</span></div>}
        {canApprove && (t.status === 'AwaitingApproval' || t.status === 'UnderReview') && (
          <div className="card" style={{ padding: 12 }}>
            <label className="fl" htmlFor="note">{t.status === 'UnderReview' ? 'قرار المراجعة وسببه *' : 'ملاحظة الاعتماد'}<textarea id="note" value={note} onChange={e => setNote(e.target.value)} /></label>
            {act.error && <div className="err">{(act.error as Error).message}</div>}
            <div className="actions"><button className="btn ok" disabled={act.isPending || (t.status === 'UnderReview' && !note.trim())} onClick={() => act.mutate(t.status === 'UnderReview' ? 'resolve' : 'approve')}>اعتماد العملية</button></div>
          </div>)}
        <DrawerActions trip={t} canOps={can(me, 'Ops', 'Edit')} canApprove={canApprove} />
        <section className="panel"><div className="hd"><h3>سجل التدقيق</h3></div>
          <div className="tblw"><table><tbody>{data.audit.slice().reverse().map((a, i) => <tr key={i}><td className="num">{ksa(a.at)}</td><td><b>{a.action}</b></td><td style={{ whiteSpace: 'normal' }}>{a.detail}</td></tr>)}</tbody></table></div>
        </section>
      </div>
    </div>
  )
}

type Correction = { id: string; field: string; oldValue?: string; newValue: string; reason: string; state: string }
type FullTrip = Trip & { corrections?: Correction[] }
const fieldName: Record<string, string> = { qty: 'الكمية', docNo: 'رقم السند', odometer: 'عداد الكيلومترات' }

function DrawerActions({ trip, canOps, canApprove }: { trip: FullTrip; canOps: boolean; canApprove: boolean }) {
  const act = useAction()
  const drivers = useDrivers()
  const open = !['AwaitingApproval', 'Approved', 'UnderReview', 'Cancelled'].includes(trip.status)
  const pending = (trip.corrections ?? []).filter(c => c.state === 'Pending')
  function handover(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const b = formData(e.currentTarget, ['meterReading', 'remaining'])
    act.run(() => post(`/api/trips/${trip.id}/handover`, { ...b, moveVehicle: b.moveVehicle === 'on' }), 'نُقل الطلب، وسيؤكد السائق المستلم من التطبيق.')
  }
  function cancel(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    act.run(() => post(`/api/trips/${trip.id}/cancel`, formData(e.currentTarget)), 'أُلغي الطلب.')
  }
  return (
    <>
      {act.banner}
      {pending.length > 0 && <section className="panel"><div className="hd"><h3>طلبات تصحيح بانتظار القرار</h3></div>
        <div className="tblw"><table><tbody>{pending.map(c => (
          <tr key={c.id}><td>{fieldName[c.field] ?? c.field}: <span className="num">{c.oldValue} ← {c.newValue}</span><div className="sub">{c.reason}</div></td>
            {canApprove && <td className="row"><button className="btn ok" onClick={() => act.run(() => post(`/api/trips/${trip.id}/corrections/${c.id}/decide`, { approve: true }), 'اعتُمد التصحيح.')}>اعتماد</button>
              <button className="btn" onClick={() => act.run(() => post(`/api/trips/${trip.id}/corrections/${c.id}/decide`, { approve: false }), 'رُفض التصحيح.')}>رفض</button></td>}</tr>))}</tbody></table></div></section>}
      {canOps && open && <section className="panel"><div className="hd"><h3>نقل الطلب لسائق آخر</h3></div>
        <form className="filter" onSubmit={handover} style={{ boxShadow: 'none' }}><div className="fg">
          <Field label="إلى السائق *"><select name="toDriverId" required><option value="">اختر</option>{drivers.data?.filter(d => d.id !== trip.driverId).map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
          {trip.loading && <Field label="قراءة العداد عند التسليم"><input name="meterReading" type="number" min={0} /></Field>}
          {trip.loading && <Field label="الكمية المتبقية (لتر)"><input name="remaining" type="number" min={0} /></Field>}
          <Field label="السبب *"><input name="note" required maxLength={1000} /></Field>
          <label className="fl" style={{ flexDirection: 'row', gap: 8, alignItems: 'center' }}><input type="checkbox" name="moveVehicle" /> نقل المركبة معه</label>
        </div><div className="actions"><button className="btn pri" disabled={act.busy}>نقل</button></div></form></section>}
      {canOps && open && <section className="panel"><div className="hd"><h3>إلغاء الطلب</h3></div>
        <form className="filter" onSubmit={cancel} style={{ boxShadow: 'none' }}><div className="fg"><Field label="سبب الإلغاء *"><input name="note" required maxLength={1000} /></Field></div>
          <div className="actions"><button className="btn" disabled={act.busy}>إلغاء الطلب</button></div></form></section>}
    </>
  )
}
