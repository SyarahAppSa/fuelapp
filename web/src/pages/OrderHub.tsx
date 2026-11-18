// The order hub: customer, type, prices, every truck and driver on the order, loading documents, delivered quantities,
// fill reports with their photos, and the history. Later phases add the invoice and collection here.
import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, blobUrl, can, fmt, ksa, post, signed } from '../api'
import { useMe } from '../App'
import { Field, Panel, useAction } from '../ui'
import { dateOnly, fillChip, fillLabel, orderChip, sar, type Hub } from '../orders'
import { OrderForm } from './Orders'

function Photo({ id, label, onOpen }: { id: string; label: string; onOpen: (url: string, label: string) => void }) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    let u: string | null = null
    blobUrl(`/api/attachments/${id}`).then(x => { u = x; setUrl(x) }).catch(() => setUrl(null))
    return () => { if (u) URL.revokeObjectURL(u) }
  }, [id])
  return (
    <figure className="frp">
      {url ? <button className="frp-img" onClick={() => onOpen(url, label)} aria-label={`تكبير ${label}`}><img src={url} alt={label} /></button> : <div className="frp-img empty">جارٍ التحميل…</div>}
      <figcaption><b>{label}</b></figcaption>
    </figure>
  )
}

export default function OrderHub() {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const { data: me } = useMe()
  const act = useAction()
  const [mode, setMode] = useState<'' | 'edit' | 'close' | 'cancel' | 'link'>('')
  const [zoom, setZoom] = useState<{ url: string; label: string } | null>(null)
  const q = useQuery({ queryKey: ['order', id], queryFn: () => api<Hub>(`/api/orders/${id}`) })
  const linkable = useQuery({ queryKey: ['order', id, 'linkable'], queryFn: () => api<{ id: string; no: string; statusLabel: string; qtyRequested: number; createdAt: string; driver?: string; plate?: string }[]>(`/api/orders/${id}/linkable`), enabled: mode === 'link' })
  if (q.isError) return <div className="err">{(q.error as Error).message}</div>
  const h = q.data
  if (!h) return <div className="sub">جارٍ التحميل…</div>
  const o = h.order, t = o.totals
  const edit = can(me, 'Ops', 'Edit') && o.status !== 'Closed' && o.status !== 'Cancelled'

  function reasonAction(e: FormEvent<HTMLFormElement>, kind: 'close' | 'cancel') {
    e.preventDefault()
    const reason = String(new FormData(e.currentTarget).get('reason') ?? '')
    act.run(async () => { await post(`/api/orders/${id}/${kind}`, { reason }); setMode('') }, kind === 'close' ? 'أُغلق الطلب.' : 'أُلغي الطلب.')
  }
  function link(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const tripId = String(new FormData(e.currentTarget).get('tripId') ?? '')
    act.run(async () => { await post(`/api/orders/${id}/link-trip`, { tripId }); setMode('') }, 'رُبطت الرحلة بالطلب.')
  }

  return (
    <>
      <div className="spread noprint">
        <div className="row"><button className="btn" onClick={() => nav(-1)}>← رجوع</button><Link className="btn" to="/orders">طلبات العملاء</Link>
          <h1 className="num">{o.no}</h1><span className={`chip ${orderChip[o.status]}`}>{o.statusLabel}</span>
          <span className={`chip ${o.priority === 'Urgent' ? 'st-UnderReview' : 'st-WaitingLoad'}`}>{o.priorityLabel}</span>{t.late && <span className="chip st-UnderReview">متأخر</span>}</div>
        <div className="row">
          {edit && <Link className="btn pri" to={`/dispatch?order=${o.id}`}>إسناد شاحنة</Link>}
          {edit && <button className="btn" onClick={() => setMode(mode === 'edit' ? '' : 'edit')}>تعديل</button>}
          {edit && <button className="btn" onClick={() => setMode(mode === 'link' ? '' : 'link')}>ربط رحلة سابقة</button>}
          {edit && <button className="btn" onClick={() => setMode(mode === 'close' ? '' : 'close')}>إغلاق الطلب</button>}
          {edit && <button className="btn" onClick={() => setMode(mode === 'cancel' ? '' : 'cancel')}>إلغاء الطلب</button>}
          <button className="btn" onClick={() => window.print()}>طباعة</button>
        </div>
      </div>
      {act.banner}
      {mode === 'edit' && <OrderForm order={o} onDone={() => setMode('')} />}
      {(mode === 'close' || mode === 'cancel') && (
        <form className="filter" onSubmit={e => reasonAction(e, mode)}>
          <div className="fg"><Field label={mode === 'close' ? 'سبب الإغلاق *' : 'سبب الإلغاء *'}><input name="reason" required minLength={3} maxLength={500} /></Field></div>
          <div className="sub">{mode === 'close' ? 'يُغلق الطلب بعد اكتمال تفريغ كل رحلاته.' : 'يُلغى الطلب فقط إذا لم تبدأ أي رحلة؛ الرحلات المنتظرة تُلغى معه.'}</div>
          <div className="actions"><button className="btn pri" disabled={act.busy}>{mode === 'close' ? 'إغلاق الطلب' : 'إلغاء الطلب'}</button></div>
        </form>)}
      {mode === 'link' && (
        <form className="filter" onSubmit={link}>
          <div className="fg"><Field label="رحلة سابقة لنفس العميل غير مرتبطة بطلب"><select name="tripId" required><option value="">اختر</option>
            {linkable.data?.map(x => <option key={x.id} value={x.id}>{x.no} · {x.statusLabel} · {fmt(x.qtyRequested)} لتر · {x.plate ?? ''} · {dateOnly(x.createdAt)}</option>)}</select></Field></div>
          {linkable.data?.length === 0 && <div className="sub">لا توجد رحلات غير مرتبطة لهذا العميل.</div>}
          <div className="actions"><button className="btn pri" disabled={act.busy}>ربط</button></div>
        </form>)}

      <section className="hero" style={{ marginTop: 14 }}>
        <div>
          <div>{o.typeLabel} · {o.product}</div>
          <div className="big" style={{ fontSize: 26, direction: 'rtl', fontFamily: 'var(--f-body)' }}>{o.customerName}</div>
          <div>{o.site}</div>
          <div className="num">{o.customerRef ? `أمر الشراء ${o.customerRef} · ` : ''}التسليم {dateOnly(o.requestedFrom)} ← {dateOnly(o.requestedTo)}</div>
          <div style={{ fontSize: 12.5, opacity: .85 }}>أنشأه {h.createdBy ?? '—'} · {ksa(o.createdAt)}{o.statusReason ? ` · ${o.statusReason}` : ''}</div>
        </div>
        <div className="tiles">
          <div className="tile"><div>المطلوب / المخطط</div><div className="v">{fmt(t.requested)} / {fmt(t.planned)}</div><div>{t.trips} رحلة · مفتوحة {t.openTrips} · للإسناد {fmt(t.toDispatch)}</div></div>
          <div className="tile"><div>المحمّل / المسلّم</div><div className="v">{fmt(t.loaded)} / {fmt(t.delivered)}</div><div className="num">حسب سندات الاستلام {fmt(t.receipt)}</div></div>
          <div className="tile"><div>المتبقي للتسليم</div><div className="v">{fmt(t.remaining)}</div><div>{t.late ? 'متأخر عن موعد التسليم' : 'ضمن الموعد'}</div></div>
          <div className="tile"><div>فرق المحمّل عن المسلّم</div><div className="v">{signed(t.difference)}</div><div>{t.differenceFlag ? 'يتجاوز المسموح' : 'ضمن المسموح'} (±{fmt(t.tolerance)})</div></div>
          {h.money && <div className="tile"><div>الأجرة / سعر الوقود (ريال/لتر)</div><div className="v">{sar(h.money.transportRatePerLiter)}{o.type === 'TransportAndSupply' ? ` / ${sar(h.money.fuelPricePerLiter)}` : ''}</div><div>قيمة الطلب {sar(h.money.orderValue)}</div></div>}
          {h.money && <div className="tile"><div>الإيراد التقديري (المسلّم)</div><div className="v">{sar(h.money.revenueEstimate)}</div><div>ريال · {sar(h.money.ratePerLiter)} للتر</div></div>}
        </div>
      </section>
      {o.notes && <div className="card" style={{ padding: 12, marginTop: 12 }}><b>ملاحظات:</b> {o.notes}</div>}

      <Panel title={`الشاحنات والرحلات (${h.trips.length})`}>
        <div className="tblw"><table>
          <thead><tr><th>الرحلة</th><th>الحالة</th><th>السائق</th><th>الشاحنة / المقطورة</th><th>موقع التعبئة</th><th>طلب التحميل / سند التعبئة</th><th>المخطط</th><th>المحمّل</th><th>المسلّم (عداد)</th><th>سند الاستلام</th><th>الفرق</th><th>تقرير التعبئة</th><th>التواريخ</th></tr></thead>
          <tbody>{h.trips.map(x => {
            const bad = x.difference != null && Math.abs(x.difference) > x.tolerance
            return (
              <tr key={x.id}>
                <td className="num"><Link to={`/trips?open=${x.id}`}><b>{x.no}</b></Link></td>
                <td><span className={`chip st-${x.status}`}>{x.statusLabel}</span></td>
                <td>{x.driver}</td><td className="num">{x.plate}{x.trailerPlate && <div className="sub">مقطورة {x.trailerPlate}</div>}</td>
                <td>{x.station}</td><td className="num">{x.sourceRef}<div className="sub">{x.loadingDocNo ?? '—'}</div></td>
                <td className="num">{fmt(x.qtyRequested)}</td><td className="num">{fmt(x.loaded)}</td><td className="num">{fmt(x.delivered)}</td>
                <td className="num">{fmt(x.receipt)}<div className="sub">{x.receiptNo || '—'}</div></td>
                <td className={`num ${bad ? 'diff' : ''}`}>{signed(x.difference)}</td>
                <td>{x.fillReport ? <Link to={`/fill-reports/${x.fillReport.id}`} className="num">{x.fillReport.no} <span className={`chip ${fillChip[x.fillReport.status]}`}>{fillLabel[x.fillReport.status]}</span></Link> : <span className="sub">—</span>}</td>
                <td className="num" style={{ fontSize: 12 }}>{ksa(x.createdAt)}<div className="sub">تحميل {ksa(x.loadedAt)} · تسليم {ksa(x.deliveredAt)}</div></td>
              </tr>)
          })}</tbody>
        </table></div>
        {h.trips.length === 0 && <div className="sub" style={{ padding: 16 }}>لم تُسند شاحنات لهذا الطلب بعد.</div>}
      </Panel>

      {h.photos && (
        <Panel title={`صور آخر تقرير تعبئة — ${h.photos.reportNo} (الرحلة ${h.photos.tripNo})`} actions={<Link className="btn" to={`/fill-reports/${h.photos.reportId}`}>فتح التقرير</Link>}>
          <div className="frphotos" style={{ padding: 14 }}>{h.photos.items.map(p => <Photo key={p.id} id={p.id} label={p.label} onOpen={(url, label) => setZoom({ url, label })} />)}</div>
        </Panel>)}

      <Panel title="سجل الطلب">
        <div className="tblw"><table><thead><tr><th>الوقت</th><th>الإجراء</th><th>بواسطة</th><th>التفاصيل</th><th>القيمة السابقة</th><th>القيمة الجديدة</th></tr></thead>
          <tbody>{h.history.slice().reverse().map((x, i) => <tr key={i}><td className="num">{ksa(x.at)}</td><td><b>{x.action}</b></td><td>{x.by ?? '—'}</td><td style={{ whiteSpace: 'normal' }}>{x.detail}</td>
            <td className="num" style={{ whiteSpace: 'normal', maxWidth: 320, fontSize: 12 }}>{x.oldValue ?? ''}</td><td className="num" style={{ whiteSpace: 'normal', maxWidth: 320, fontSize: 12 }}>{x.newValue ?? ''}</td></tr>)}</tbody></table></div>
      </Panel>
      {zoom && <div className="lightbox" role="dialog" aria-label={zoom.label} onClick={() => setZoom(null)}><img src={zoom.url} alt={zoom.label} /><div className="lightbox-cap">{zoom.label} — اضغط للإغلاق</div></div>}
    </>
  )
}
