import { useEffect, useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, blobUrl, can, fmt, ksa, post, signed } from '../api'
import { useMe } from '../App'
import { Field, Panel, useAction } from '../ui'

type Status = 'Draft' | 'Submitted' | 'NeedsReview' | 'Returned' | 'Approved'
interface Row { id: string; no: string; tripNo: string; plate: string; driverName: string; customerName: string; deliverySite: string; status: Status; startedAt: string; submittedAt?: string; qtyRequested: number; loadedQty?: number; deliveredQty?: number; difference?: number; flagged: boolean; stagesDone: number }
interface Photo { kind: string; stage: string; label: string; id?: string; capturedAt?: string; uploadedAt: string; lat?: number; lng?: number; locationNote?: string; fromGallery: boolean; retakes: number }
interface Detail {
  id: string; no: string; tripNo: string; status: Status; startedAt: string; submittedAt?: string; plate: string; trailerPlate?: string; customerName: string; deliverySite: string; loadingSite: string; product: string
  driver: { name: string; nationalId?: string; phone?: string; masked: boolean }
  stages: { preFill: { done: boolean; at?: string }; postFill: { done: boolean; at?: string; loadedQty?: number; loadedFromTrip: boolean }; arrival: { done: boolean; at?: string; meterBefore?: number; secretMeterSerial?: string }; postUnload: { done: boolean; at?: string; meterAfter?: number; outcome: 'Full' | 'Partial' | 'Failed'; outcomeReason?: string; tankState?: string } }
  quantities: { requested: number; loaded?: number; delivered?: number; difference?: number; allowed: number }
  photos: Photo[]; missing: string[]; reviewReasons?: string; reviewNote?: string; reviewedAt?: string; reviewedBy?: string
  history: { at: string; action: string; detail?: string; oldValue?: string; newValue?: string; by?: string }[]
}

const statusLabel: Record<Status, string> = { Draft: 'قيد التوثيق', Submitted: 'مُرسل', NeedsReview: 'بحاجة إلى مراجعة', Returned: 'أُعيد للسائق', Approved: 'معتمد' }
const statusChip: Record<Status, string> = { Draft: 'st-WaitingLoad', Submitted: 'st-AwaitingApproval', NeedsReview: 'st-UnderReview', Returned: 'st-Cancelled', Approved: 'st-Approved' }
const outcomeLabel = { Full: 'تفريغ كامل', Partial: 'تفريغ جزئي', Failed: 'تعذّر التفريغ' }
const stageNames: [keyof Detail['stages'], string][] = [['preFill', 'قبل التعبئة'], ['postFill', 'بعد التعبئة'], ['arrival', 'الوصول إلى العميل'], ['postUnload', 'بعد التفريغ']]

/** A protected photo: loaded with the reviewer's own session, opened full size on click. */
function Thumb({ p, onOpen }: { p: Photo; onOpen: (url: string, label: string) => void }) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    if (!p.id) return
    let u: string | null = null
    blobUrl(`/api/attachments/${p.id}`).then(x => { u = x; setUrl(x) }).catch(() => setUrl(null))
    return () => { if (u) URL.revokeObjectURL(u) }
  }, [p.id])
  return (
    <figure className="frp">
      {p.id ? (url ? <button className="frp-img" onClick={() => onOpen(url, p.label)} aria-label={`تكبير صورة ${p.label}`}><img src={url} alt={p.label} /></button> : <div className="frp-img empty">جارٍ التحميل…</div>)
        : <div className="frp-img empty missing">لم تُلتقط</div>}
      <figcaption><b>{p.label}</b>
        {p.id && <div className="sub num">{ksa(p.capturedAt ?? p.uploadedAt)}{p.retakes > 0 && ` · أُعيدت ${p.retakes}`}</div>}
        {p.id && <div className="sub">{p.lat != null ? <span className="num">📍 {p.lat.toFixed(4)}, {p.lng?.toFixed(4)}</span> : <>📍 {p.locationNote}</>}</div>}
        {p.fromGallery && <div className="err" style={{ padding: '2px 6px' }}>وقت الالتقاط قديم أو غير مرسل</div>}</figcaption>
    </figure>
  )
}

export default function FillReports() {
  const { data: me } = useMe()
  const [filters, setFilters] = useState<{ q: string; status: string; from: string; to: string }>({ q: '', status: '', from: '', to: '' })
  const [openId, setOpenId] = useState<string | null>(null)
  const qs = new URLSearchParams(Object.entries(filters).filter(([, v]) => v)).toString()
  const list = useQuery({ queryKey: ['fill-reports', qs], queryFn: () => api<{ total: number; items: Row[] }>(`/api/fill-reports?${qs}`) })

  function search(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setFilters({ q: String(f.get('q') ?? ''), status: String(f.get('status') ?? ''), from: String(f.get('from') ?? ''), to: String(f.get('to') ?? '') })
  }

  if (openId) return <ReportView id={openId} canEdit={can(me, 'FillReports', 'Edit')} onBack={() => setOpenId(null)} />
  return (
    <>
      <h1>تقارير التعبئة</h1>
      <form className="filter" onSubmit={search} style={{ marginTop: 14 }}>
        <div className="fg">
          <Field label="بحث: رقم التقرير، اللوحة، السائق، العميل"><input name="q" defaultValue={filters.q} maxLength={60} /></Field>
          <Field label="الحالة"><select name="status" defaultValue={filters.status}><option value="">الكل</option>{(Object.keys(statusLabel) as Status[]).map(s => <option key={s} value={s}>{statusLabel[s]}</option>)}</select></Field>
          <Field label="من تاريخ"><input name="from" type="date" defaultValue={filters.from} /></Field>
          <Field label="إلى تاريخ"><input name="to" type="date" defaultValue={filters.to} /></Field>
        </div>
        <div className="actions"><button className="btn pri">بحث</button></div>
      </form>
      <Panel title={`التقارير (${list.data?.total ?? 0})`}>
        <div className="tblw"><table>
          <thead><tr><th>رقم التقرير</th><th>التاريخ</th><th>اللوحة</th><th>السائق</th><th>العميل</th><th>المحمّلة</th><th>المسلّمة (العداد)</th><th>الفرق</th><th>المراحل</th><th>الحالة</th></tr></thead>
          <tbody>{list.data?.items.map(r => (
            <tr key={r.id} className={`click ${r.flagged ? 'flag' : ''}`} onClick={() => setOpenId(r.id)}>
              <td className="num"><b>{r.no}</b><div className="sub">{r.tripNo}</div></td><td className="num">{ksa(r.submittedAt ?? r.startedAt)}</td><td className="num">{r.plate}</td>
              <td>{r.driverName}</td><td>{r.customerName}<div className="sub">{r.deliverySite}</div></td><td className="num">{fmt(r.loadedQty)}</td><td className="num">{fmt(r.deliveredQty)}</td>
              <td className={`num ${r.flagged ? 'diff' : ''}`}>{signed(r.difference)}</td><td className="num">{r.stagesDone}/4</td>
              <td><span className={`chip ${statusChip[r.status]}`}>{statusLabel[r.status]}</span></td>
            </tr>))}</tbody>
        </table></div>
        {list.data?.items.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد تقارير مطابقة.</div>}
      </Panel>
    </>
  )
}

function ReportView({ id, canEdit, onBack }: { id: string; canEdit: boolean; onBack: () => void }) {
  const act = useAction()
  const q = useQuery({ queryKey: ['fill-report', id], queryFn: () => api<Detail>(`/api/fill-reports/${id}`) })
  const [zoom, setZoom] = useState<{ url: string; label: string } | null>(null)
  const r = q.data
  if (!r) return <div className="sub">جارٍ التحميل…</div>
  const reviewable = r.status === 'Submitted' || r.status === 'NeedsReview'
  const qd = r.quantities
  const over = qd.difference != null && Math.abs(qd.difference) > qd.allowed

  function decide(e: FormEvent<HTMLFormElement>, kind: 'approve' | 'return') {
    e.preventDefault()
    const note = String(new FormData(e.currentTarget).get('note') ?? '')
    act.run(() => post(`/api/fill-reports/${id}/${kind}`, { note }), kind === 'approve' ? 'اعتُمد التقرير.' : 'أُعيد التقرير للسائق مع ملاحظتك.')
  }
  function edit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const body = { field: f.get('field'), value: f.get('value'), reason: f.get('reason') }
    act.run(() => api(`/api/fill-reports/${id}/fields`, { method: 'PUT', body: JSON.stringify(body) }), 'حُفظ التعديل في سجل التعديلات.')
  }
  const photosOf = (stage: string) => r.photos.filter(p => p.stage === stage)
  const stageKey = { preFill: 'pre-fill', postFill: 'post-fill', arrival: 'arrival', postUnload: 'post-unload' } as const

  return (
    <>
      <div className="spread"><div className="row"><button className="btn" onClick={onBack}>← القائمة</button><h1 className="num">{r.no}</h1>
        <span className={`chip ${statusChip[r.status]}`}>{statusLabel[r.status]}</span></div></div>
      {act.banner}

      <section className="hero" style={{ marginTop: 14 }}>
        <div><div>{r.customerName}</div><div className="big" style={{ fontSize: 26 }}>{r.plate}{r.trailerPlate && <small> · مقطورة {r.trailerPlate}</small>}</div>
          <div>{r.loadingSite} ← {r.deliverySite}</div><div className="num">الرحلة {r.tripNo} · {r.product}</div></div>
        <div className="tiles">
          <div className="tile"><div>السائق</div><div className="v" style={{ fontSize: 17 }}>{r.driver.name}</div>
            <div className="num">الهوية {r.driver.nationalId ?? '—'} · الجوال {r.driver.phone ?? '—'}</div>{r.driver.masked && <div style={{ fontSize: 11 }}>مخفية جزئيًا حسب صلاحيتك</div>}</div>
          <div className="tile"><div>المطلوب / المحمّل / المسلّم</div><div className="v num" style={{ fontSize: 18 }}>{fmt(qd.requested)} / {fmt(qd.loaded)} / {fmt(qd.delivered)}</div>
            <div className="num">الفرق {signed(qd.difference)} لتر (المسموح ±{fmt(qd.allowed)})</div></div>
        </div>
      </section>

      {(r.reviewReasons || over) && <div className="card" style={{ padding: 12, marginTop: 12, color: 'var(--crit)' }}><b>أسباب المراجعة:</b> {r.reviewReasons ?? 'الفرق يتجاوز المسموح'}</div>}
      {r.reviewNote && <div className="card" style={{ padding: 12, marginTop: 12 }}><b>ملاحظة المراجعة:</b> {r.reviewNote} {r.reviewedBy && <span className="sub">— {r.reviewedBy} {ksa(r.reviewedAt)}</span>}</div>}
      {r.missing.length > 0 && <div className="card" style={{ padding: 12, marginTop: 12 }}><b>ناقص:</b> {r.missing.join('، ')}</div>}

      {stageNames.map(([k, name], i) => {
        const st = r.stages[k]
        return (
          <Panel key={k} title={`${i + 1}. ${name}`} actions={<span className={`chip ${st.done ? 'st-Approved' : 'st-WaitingLoad'}`}>{st.done ? `تم · ${ksa(st.at)}` : 'لم يُوثّق'}</span>}>
            <div className="frstage">
              <div className="frphotos">{photosOf(stageKey[k]).map(p => <Thumb key={p.kind} p={p} onOpen={(url, label) => setZoom({ url, label })} />)}</div>
              <div className="frfacts">
                {k === 'postFill' && <div><span className="sub">الكمية المحمّلة</span><div className="v num">{fmt(r.stages.postFill.loadedQty)} لتر</div><div className="sub">{r.stages.postFill.loadedFromTrip ? 'من سجل التحميل' : 'أدخلها السائق'}</div></div>}
                {k === 'arrival' && <div><span className="sub">قراءة العداد قبل التفريغ</span><div className="v num">{fmt(r.stages.arrival.meterBefore)}</div><div className="sub num">العداد {r.stages.arrival.secretMeterSerial ?? '—'}</div></div>}
                {k === 'postUnload' && <>
                  <div><span className="sub">قراءة العداد بعد التفريغ</span><div className="v num">{fmt(r.stages.postUnload.meterAfter)}</div></div>
                  <div><span className="sub">الكمية المسلّمة (بعد − قبل)</span><div className={`v num ${over ? 'diff' : ''}`}>{fmt(qd.delivered)} لتر</div></div>
                  <div><span className="sub">النتيجة</span><div>{outcomeLabel[r.stages.postUnload.outcome]}{r.stages.postUnload.outcomeReason && ` — ${r.stages.postUnload.outcomeReason}`}</div><div className="sub">حالة الصهريج: {r.stages.postUnload.tankState ?? '—'}</div></div>
                </>}
              </div>
            </div>
          </Panel>)
      })}

      {canEdit && reviewable && (
        <Panel title="القرار">
          <div className="frdecide">
            <form className="filter" style={{ boxShadow: 'none' }} onSubmit={e => decide(e, 'approve')}>
              <Field label={r.status === 'NeedsReview' ? 'قرار المراجعة (مطلوب)' : 'ملاحظة (اختياري)'}><input name="note" maxLength={1000} required={r.status === 'NeedsReview'} /></Field>
              <div className="actions"><button className="btn ok" disabled={act.busy}>اعتماد التقرير</button></div>
            </form>
            <form className="filter" style={{ boxShadow: 'none' }} onSubmit={e => decide(e, 'return')}>
              <Field label="المطلوب من السائق تحديدًا"><input name="note" required maxLength={1000} placeholder="مثال: صورة العداد بعد التفريغ غير واضحة، أعد تصويرها" /></Field>
              <div className="actions"><button className="btn" disabled={act.busy}>إعادة للسائق</button></div>
            </form>
          </div>
          <form className="filter" style={{ boxShadow: 'none' }} onSubmit={edit}>
            <div className="fg">
              <Field label="تعديل حقل"><select name="field" required><option value="meterBefore">قراءة العداد قبل التفريغ</option><option value="meterAfter">قراءة العداد بعد التفريغ</option>
                <option value="loadedQty">الكمية المحمّلة</option><option value="secretMeterSerial">الرقم التسلسلي للعداد</option><option value="tankState">حالة الصهريج</option></select></Field>
              <Field label="القيمة الجديدة"><input name="value" required maxLength={60} /></Field>
              <Field label="سبب التعديل"><input name="reason" required minLength={3} maxLength={500} /></Field>
            </div>
            <div className="actions"><button className="btn" disabled={act.busy}>حفظ التعديل</button></div>
          </form>
        </Panel>)}

      <Panel title="سجل التقرير والتعديلات">
        <div className="tblw"><table><thead><tr><th>الوقت</th><th>الإجراء</th><th>بواسطة</th><th>التفاصيل</th><th>القيمة الأصلية</th><th>القيمة الجديدة</th></tr></thead>
          <tbody>{r.history.map((h, i) => <tr key={i}><td className="num">{ksa(h.at)}</td><td><b>{h.action}</b></td><td>{h.by ?? '—'}</td><td style={{ whiteSpace: 'normal' }}>{h.detail}</td><td className="num">{h.oldValue ?? ''}</td><td className="num">{h.newValue ?? ''}</td></tr>)}</tbody></table></div>
      </Panel>

      {zoom && (
        <div className="lightbox" role="dialog" aria-label={zoom.label} onClick={() => setZoom(null)}>
          <img src={zoom.url} alt={zoom.label} /><div className="lightbox-cap">{zoom.label} — اضغط للإغلاق</div>
        </div>)}
    </>
  )
}
