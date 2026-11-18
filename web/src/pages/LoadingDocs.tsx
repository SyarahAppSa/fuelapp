import { useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { api, blobUrl, can, download, fmt, ksa, post } from '../api'
import { useMe } from '../App'
import { Field, Panel, useAction } from '../ui'

// «توثيق التعبئة»: the loading part of every fill report (tank before / after filling, loading document), linked to the order and the trip.
// Viewing and downloading photos: Fill reports «View». «طلب إعادة التصوير»: Fill reports «Edit». Excel: Export, as in the other reports.

type DocStatus = 'AwaitingPostFill' | 'Complete' | 'NeedsReview' | 'NotStarted'
type Source = 'Aramco' | 'Station' | 'MaysanYard'
interface PhotoInfo { id: string; capturedAt?: string; uploadedAt: string; lat?: number; lng?: number; locationNote?: string; fromGallery: boolean; lateMinutes?: number; versions: number }
interface Row {
  id: string; no: string; tripId: string; tripNo: string; orderId?: string; orderNo?: string; driverName: string; plate: string; trailerPlate?: string
  source: Source; sourceLabel: string; facility: string; city?: string; product: string; qtyRequested: number; loadedQty?: number
  loadingDocNo?: string; loadingDocNoFrom?: 'driver' | 'loading' | 'sourceRef'; loadingDocLabel: string
  preFill?: PhotoInfo; postFill?: PhotoInfo; loadingDoc?: PhotoInfo; firstPhotoAt: string; status: DocStatus; statusLabel: string; reasons: string[]; openRetakes: number; reportStatus: string
}
interface Totals { all: number; awaitingPostFill: number; needsReview: number; complete: number }
interface ListResp { totals: Totals; total: number; page: number; size: number; items: Row[] }
interface Version { id: string; version: number; current: boolean; capturedAt?: string; uploadedAt: string; lat?: number; lng?: number; locationNote?: string; fromGallery: boolean; lateMinutes?: number; by?: string; retakeNote?: string }
interface Slot { kind: string; stage: string; label: string; loading: boolean; openRetake?: { id: string; note: string; requestedAt: string; by?: string }; versions: Version[] }
interface DetailResp {
  record: Row; report: { customerName: string; deliverySite: string; startedAt: string; submittedAt?: string }; canRequestRetake: boolean; slots: Slot[]
  retakeRequests: { id: string; kind: string; label: string; note: string; requestedAt: string; by?: string; resolvedAt?: string; resolvedAttachmentId?: string }[]
}

const statusChip: Record<DocStatus, string> = { AwaitingPostFill: 'st-AwaitingApproval', Complete: 'st-Approved', NeedsReview: 'st-UnderReview', NotStarted: 'st-WaitingLoad' }
const statusOptions: [DocStatus, string][] = [['AwaitingPostFill', 'بانتظار صورة بعد التعبئة'], ['Complete', 'مكتمل'], ['NeedsReview', 'يحتاج مراجعة']]
const sourceOptions: [Source, string][] = [['Aramco', 'أرامكو'], ['Station', 'محطة'], ['MaysanYard', 'حوش ميسان']]
const docFrom = { driver: 'أدخله السائق', loading: 'من سجل التحميل', sourceRef: 'رقم أمر التحميل' }

/** «وصلت بعد ساعتين و5 دقائق»: time between taking the photo and its arrival (sent later from the phone's offline queue). */
function lateText(min?: number) {
  if (min == null) return null
  const d = Math.floor(min / 1440), h = Math.floor((min % 1440) / 60), m = min % 60
  return 'وصلت بعد ' + [d && `${d} يوم`, h && `${h} ساعة`, m && `${m} دقيقة`].filter(Boolean).join(' و')
}
const where = (p: { lat?: number; lng?: number; locationNote?: string }) => p.lat != null && p.lng != null
  ? <a className="num" href={`https://maps.google.com/?q=${p.lat},${p.lng}`} target="_blank" rel="noreferrer noopener">📍 {p.lat.toFixed(5)}, {p.lng.toFixed(5)}</a>
  : <span className="diff">📍 بدون موقع{p.locationNote ? ` — ${p.locationNote}` : ''}</span>

/** A protected photo loaded with the viewer's own session (the server checks Fill reports «View»). */
function useBlob(id?: string) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    if (!id) { setUrl(null); return }
    let u: string | null = null, live = true
    blobUrl(`/api/attachments/${id}`).then(x => { u = x; if (live) setUrl(x); else URL.revokeObjectURL(x) }).catch(() => setUrl(null))
    return () => { live = false; if (u) URL.revokeObjectURL(u) }
  }, [id])
  return url
}

function Mini({ p, label }: { p?: PhotoInfo; label: string }) {
  const url = useBlob(p?.id)
  return (
    <span className={`ld-mini ${p ? '' : 'none'}`} title={label}>
      {p ? (url ? <img src={url} alt={label} /> : <i>…</i>) : <i>—</i>}
      <small>{label}</small>
    </span>
  )
}

const PAGE_ICONS = {
  all: <svg viewBox="0 0 24 24" width="30" height="30" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11z" /></svg>,
  wait: <svg viewBox="0 0 24 24" width="30" height="30" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 7v5l3 2M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z" /></svg>,
  review: <svg viewBox="0 0 24 24" width="30" height="30" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d="M12 9v4M12 17h.01M10.3 3.9 1.8 18a2 2 0 0 0 1.7 3h17a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z" /></svg>,
}

export default function LoadingDocs() {
  const { id } = useParams()
  return id ? <LoadingRecord id={id} /> : <LoadingList />
}

function LoadingList() {
  const { data: me } = useMe()
  const nav = useNavigate()
  const act = useAction()
  const [f, setF] = useState({ q: '', from: '', to: '', source: '', status: '' })
  const [page, setPage] = useState(1)
  const qs = new URLSearchParams(Object.entries({ ...f, page: String(page) }).filter(([, v]) => v)).toString()
  const list = useQuery({ queryKey: ['loading-docs', qs], queryFn: () => api<ListResp>(`/api/fill-reports/loading?${qs}`) })
  const t = list.data?.totals
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1

  function search(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const d = new FormData(e.currentTarget)
    const g = (k: string) => String(d.get(k) ?? '')
    setF({ q: g('q').trim(), from: g('from'), to: g('to'), source: g('source'), status: g('status') }); setPage(1)
  }
  const pick = (status: string) => { setF({ ...f, status }); setPage(1) }
  const xlsxQs = new URLSearchParams(Object.entries(f).filter(([, v]) => v)).toString()

  return (
    <>
      <div className="dash-head"><h1>توثيق التعبئة</h1>
        {can(me, 'Export', 'Edit') && <button className="btn" onClick={() => act.run(() => download(`/api/fill-reports/loading?format=xlsx&${xlsxQs}`, 'توثيق-التعبئة.xlsx'), 'نُزّل ملف Excel.')}>تصدير Excel</button>}</div>
      {act.banner}
      <div className="stats ld-stats">
        <button className={`stat ${f.status === '' ? 'on' : ''}`} onClick={() => pick('')} aria-pressed={f.status === ''}>
          <span className="stat-ic">{PAGE_ICONS.all}</span><span className="stat-tx"><span className="stat-l">كل عمليات التعبئة</span><span className="stat-v" data-total="all">{fmt(t?.all)}</span></span></button>
        <button className={`stat warn ${f.status === 'AwaitingPostFill' ? 'on' : ''}`} onClick={() => pick('AwaitingPostFill')} aria-pressed={f.status === 'AwaitingPostFill'}>
          <span className="stat-ic">{PAGE_ICONS.wait}</span><span className="stat-tx"><span className="stat-l">غير مكتملة (بانتظار صورة بعد التعبئة)</span><span className="stat-v" data-total="awaiting">{fmt(t?.awaitingPostFill)}</span></span></button>
        <button className={`stat crit ${f.status === 'NeedsReview' ? 'on' : ''}`} onClick={() => pick('NeedsReview')} aria-pressed={f.status === 'NeedsReview'}>
          <span className="stat-ic">{PAGE_ICONS.review}</span><span className="stat-tx"><span className="stat-l">يحتاج مراجعة</span><span className="stat-v" data-total="review">{fmt(t?.needsReview)}</span></span></button>
      </div>

      <form className="filter" onSubmit={search} style={{ marginTop: 16 }} key={JSON.stringify(f)}>
        <div className="fg">
          <Field label="بحث: اللوحة، السائق، رقم الطلب أو الرحلة"><input name="q" defaultValue={f.q} maxLength={60} /></Field>
          <Field label="من تاريخ (أول صورة)"><input name="from" type="date" defaultValue={f.from} /></Field>
          <Field label="إلى تاريخ"><input name="to" type="date" defaultValue={f.to} /></Field>
          <Field label="مصدر التحميل"><select name="source" defaultValue={f.source}><option value="">الكل</option>{sourceOptions.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
          <Field label="حالة التوثيق"><select name="status" defaultValue={f.status}><option value="">الكل</option>{statusOptions.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
        </div>
        <div className="actions"><button className="btn pri">بحث</button>
          {Object.values(f).some(Boolean) && <button type="button" className="btn" onClick={() => { setF({ q: '', from: '', to: '', source: '', status: '' }); setPage(1) }}>مسح</button>}</div>
      </form>

      <Panel title={`العمليات (${list.data?.total ?? 0})`}>
        {list.error && <div className="err">{(list.error as Error).message}</div>}
        <div className="tblw"><table className="ld-table">
          <thead><tr><th>أول صورة</th><th>الطلب / الرحلة</th><th>السائق / الشاحنة</th><th>مصدر التحميل</th><th>قبل / بعد التعبئة</th><th>الوقود (لتر)</th><th>سند التحميل</th><th>حالة التوثيق</th></tr></thead>
          <tbody>{list.data?.items.map(r => (
            <tr key={r.id} className={`click ${r.status === 'NeedsReview' ? 'flag' : ''}`} onClick={() => nav(`/loading-docs/${r.id}`)} data-id={r.id}>
              <td className="num">{ksa(r.firstPhotoAt)}</td>
              <td className="num"><b>{r.orderNo ?? '—'}</b><div className="sub">{r.tripNo}</div></td>
              <td>{r.driverName}<div className="sub num">{r.plate}</div></td>
              <td><span className={`chip src-${r.source}`}>{r.sourceLabel}</span><div className="sub">{r.facility}{r.city && ` — ${r.city}`}</div></td>
              <td><span className="ld-pair"><Mini p={r.preFill} label="قبل" /><Mini p={r.postFill} label="بعد" /></span></td>
              <td className="num">{r.product}<div className="sub">المطلوب {fmt(r.qtyRequested)}</div><div className="sub">المحمّل <b>{fmt(r.loadedQty)}</b></div></td>
              <td className="num">{r.loadingDocNo ?? '—'}{r.loadingDoc && <div className="sub">📎 صورة السند</div>}</td>
              <td><span className={`chip ${statusChip[r.status]}`}>{r.statusLabel}</span>
                {r.reasons.length > 0 && <ul className="ld-reasons">{r.reasons.map((x, i) => <li key={i}>{x}</li>)}</ul>}</td>
            </tr>))}</tbody>
        </table></div>
        {list.data?.items.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد عمليات تعبئة مطابقة. تظهر العملية هنا بمجرد وصول أول صورة تعبئة من تطبيق السائق.</div>}
        {pages > 1 && <div className="row" style={{ padding: 12, gap: 8 }}>
          <button className="btn" disabled={page <= 1} onClick={() => setPage(page - 1)}>السابق</button><span className="num">{page} / {pages}</span>
          <button className="btn" disabled={page >= pages} onClick={() => setPage(page + 1)}>التالي</button></div>}
      </Panel>
    </>
  )
}

function PhotoCard({ slot, rec, canRetake, onZoom, children }: { slot: Slot; rec: Row; canRetake: boolean; onZoom: (url: string, label: string, id: string) => void; children?: ReactNode }) {
  const cur = slot.versions[slot.versions.length - 1]
  const url = useBlob(cur?.id)
  const act = useAction()
  const qc = useQueryClient()
  const [asking, setAsking] = useState(false)
  function ask(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const note = String(new FormData(e.currentTarget).get('note') ?? '').trim()
    act.run(async () => { await post(`/api/fill-reports/${rec.id}/retakes`, { kind: slot.kind, note }); setAsking(false); await qc.invalidateQueries({ queryKey: ['loading-doc', rec.id] }); qc.invalidateQueries({ queryKey: ['loading-docs'] }) },
      'أُرسل طلب إعادة التصوير إلى السائق.')
  }
  return (
    <figure className={`ld-photo ${slot.openRetake ? 'retake' : ''}`} data-kind={slot.kind}>
      <figcaption className="ld-photo-h"><b>{slot.label}</b>{slot.versions.length > 1 && <span className="chip st-WaitingLoad">النسخة {slot.versions.length}</span>}</figcaption>
      {cur ? (url ? <button className="frp-img" onClick={() => onZoom(url, slot.label, cur.id)} aria-label={`تكبير صورة ${slot.label}`}><img src={url} alt={slot.label} /></button> : <div className="frp-img empty">جارٍ التحميل…</div>)
        : <div className="frp-img empty missing">{slot.kind === 'loading-doc' ? 'لم يُرفق (اختياري)' : 'لم تصل بعد'}</div>}
      {cur && <div className="ld-meta">
        <div><span className="sub">وقت الالتقاط (السعودية)</span> <b className="num">{ksa(cur.capturedAt ?? cur.uploadedAt)}</b></div>
        <div><span className="sub">وصلت إلى النظام</span> <span className="num">{ksa(cur.uploadedAt)}</span>{cur.lateMinutes != null && <span className="chip st-AwaitingApproval" style={{ marginInlineStart: 6 }}>{lateText(cur.lateMinutes)}</span>}</div>
        <div>{where(cur)}</div>
        {cur.fromGallery && <div className="err" style={{ padding: '2px 6px' }}>وقت الالتقاط أقدم من 48 ساعة عند الرفع أو غير مرسل</div>}
        {cur.by && <div className="sub">بواسطة {cur.by}</div>}
      </div>}
      {children}
      {slot.openRetake && <div className="ld-open">⟳ طلب إعادة تصوير مفتوح: {slot.openRetake.note}<div className="sub">{slot.openRetake.by} · {ksa(slot.openRetake.requestedAt)}</div></div>}
      {act.banner}
      <div className="row" style={{ gap: 6, flexWrap: 'wrap', marginTop: 8 }}>
        {cur && <button className="btn" onClick={() => act.run(() => download(`/api/attachments/${cur.id}`, `${rec.tripNo}-${slot.kind}-v${cur.version}.jpg`), 'نُزّلت الصورة.')}>تنزيل</button>}
        {canRetake && !slot.openRetake && !asking && <button className="btn" onClick={() => setAsking(true)}>طلب إعادة التصوير</button>}
      </div>
      {asking && <form className="ld-ask" onSubmit={ask}>
        <Field label="ملاحظة للسائق (مطلوبة)"><textarea name="note" required minLength={3} maxLength={500} rows={2} placeholder="مثال: الصورة مظلمة ولا يظهر مستوى الوقود" /></Field>
        <div className="row" style={{ gap: 6 }}><button className="btn pri" disabled={act.busy}>إرسال الطلب</button><button type="button" className="btn" onClick={() => setAsking(false)}>إلغاء</button></div>
      </form>}
    </figure>
  )
}

function VersionThumb({ v, label, onZoom }: { v: Version; label: string; onZoom: (url: string, label: string, id: string) => void }) {
  const url = useBlob(v.id)
  return url ? <button className="ld-vthumb" onClick={() => onZoom(url, `${label} — النسخة ${v.version}`, v.id)}><img src={url} alt={`${label} النسخة ${v.version}`} /></button> : <span className="ld-vthumb" />
}

function LoadingRecord({ id }: { id: string }) {
  const nav = useNavigate()
  const q = useQuery({ queryKey: ['loading-doc', id], queryFn: () => api<DetailResp>(`/api/fill-reports/${id}/loading`) })
  const [zoom, setZoom] = useState<{ url: string; label: string; id: string } | null>(null)
  const act = useAction()
  const qc = useQueryClient()
  if (q.error) return <><button className="btn" onClick={() => nav('/loading-docs')}>← القائمة</button><div className="err" style={{ marginTop: 12 }}>{(q.error as Error).message}</div></>
  const d = q.data
  if (!d) return <div className="sub">جارٍ التحميل…</div>
  const r = d.record
  const slot = (k: string) => d.slots.find(s => s.kind === k)!
  const onZoom = (url: string, label: string, pid: string) => setZoom({ url, label, id: pid })
  const history = d.slots.filter(s => s.versions.length > 1 || d.retakeRequests.some(x => x.kind === s.kind))

  function askOther(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const form = e.currentTarget
    act.run(async () => { await post(`/api/fill-reports/${id}/retakes`, { kind: f.get('kind'), note: String(f.get('note') ?? '').trim() }); form.reset(); await qc.invalidateQueries({ queryKey: ['loading-doc', id] }) }, 'أُرسل طلب إعادة التصوير إلى السائق.')
  }

  return (
    <>
      <div className="spread"><div className="row"><button className="btn" onClick={() => nav('/loading-docs')}>← توثيق التعبئة</button>
        <h1 className="num">{r.orderNo ?? r.tripNo}</h1><span className={`chip ${statusChip[r.status]}`} data-status={r.status}>{r.statusLabel}</span></div>
        <Link className="btn" to={`/fill-reports/${r.id}`}>تقرير التعبئة الكامل {r.no}</Link></div>

      <section className="hero" style={{ marginTop: 14 }}>
        <div>
          <div>طلب العميل {r.orderNo && r.orderId ? <Link to={`/orders/${r.orderId}`} className="num" style={{ color: '#fff', fontWeight: 800 }}>{r.orderNo}</Link> : '—'} · الرحلة <span className="num">{r.tripNo}</span></div>
          <div className="big" style={{ fontSize: 26 }}>{r.plate}{r.trailerPlate && <small> · مقطورة {r.trailerPlate}</small>}</div>
          <div>السائق {r.driverName}</div>
          <div>{d.report.customerName} — {d.report.deliverySite}</div>
        </div>
        <div className="tiles">
          <div className="tile"><div>مصدر التحميل</div><div className="v" style={{ fontSize: 18 }}>{r.sourceLabel}</div><div>{r.facility}{r.city && ` — ${r.city}`}</div></div>
          <div className="tile"><div>{r.product} · المطلوب / المحمّل فعليًا</div><div className="v num" style={{ fontSize: 18 }}>{fmt(r.qtyRequested)} / {fmt(r.loadedQty)} لتر</div></div>
          <div className="tile"><div>{r.loadingDocLabel}</div><div className="v num" style={{ fontSize: 18 }}>{r.loadingDocNo ?? '—'}</div>{r.loadingDocNoFrom && <div>{docFrom[r.loadingDocNoFrom]}</div>}</div>
        </div>
      </section>

      {r.reasons.length > 0 && <div className="card" style={{ padding: 12, marginTop: 12, color: 'var(--crit)' }}><b>أسباب المراجعة:</b><ul className="ld-reasons">{r.reasons.map((x, i) => <li key={i}>{x}</li>)}</ul></div>}
      {r.status === 'AwaitingPostFill' && <div className="card" style={{ padding: 12, marginTop: 12 }}>وصلت صورة ما قبل التعبئة؛ بانتظار صورة الصهريج بعد التعبئة من السائق.</div>}

      <Panel title="صور التعبئة">
        <div className="ld-photos">
          <PhotoCard slot={slot('tank-pre-fill')} rec={r} canRetake={d.canRequestRetake} onZoom={onZoom} />
          <PhotoCard slot={slot('tank-post-fill')} rec={r} canRetake={d.canRequestRetake} onZoom={onZoom} />
          <PhotoCard slot={slot('loading-doc')} rec={r} canRetake={d.canRequestRetake} onZoom={onZoom}>
            <div className="sub" style={{ marginTop: 4 }}>{r.loadingDocLabel}: <b className="num">{r.loadingDocNo ?? '—'}</b></div>
          </PhotoCard>
        </div>
        <div className="sub" style={{ padding: '0 14px 14px' }}>التاريخ والوقت والموقع مطبوعة أيضًا على كل صورة من تطبيق السائق لحظة الالتقاط (الكاميرا فقط، لا يُسمح بالمعرض).</div>
      </Panel>

      {d.canRequestRetake && (
        <Panel title="طلب إعادة تصوير صورة أخرى من التقرير">
          {act.banner}
          <form className="filter" style={{ boxShadow: 'none' }} onSubmit={askOther}>
            <div className="fg">
              <Field label="الصورة"><select name="kind" required>{d.slots.filter(s => !s.openRetake).map(s => <option key={s.kind} value={s.kind}>{s.label}</option>)}</select></Field>
              <Field label="ملاحظة للسائق (مطلوبة)"><input name="note" required minLength={3} maxLength={500} /></Field>
            </div>
            <div className="actions"><button className="btn" disabled={act.busy}>طلب إعادة التصوير</button></div>
          </form>
        </Panel>)}

      {history.length > 0 && (
        <Panel title="سجل نسخ الصور (من الأقدم إلى الأحدث)">
          <div className="tblw"><table>
            <thead><tr><th>الصورة</th><th>النسخة</th><th></th><th>التقطها</th><th>وقت الالتقاط</th><th>وصلت</th><th>الموقع</th><th>بطلب</th></tr></thead>
            <tbody>{history.flatMap(s => s.versions.map(v => (
              <tr key={v.id} data-version={`${s.kind}-${v.version}`}>
                <td><b>{s.label}</b></td><td className="num">{v.version}{v.current && <span className="chip st-Approved" style={{ marginInlineStart: 6 }}>الحالية</span>}</td>
                <td><VersionThumb v={v} label={s.label} onZoom={onZoom} /></td><td>{v.by ?? '—'}</td>
                <td className="num">{ksa(v.capturedAt)}</td><td className="num">{ksa(v.uploadedAt)}{v.lateMinutes != null && <div className="sub">{lateText(v.lateMinutes)}</div>}</td>
                <td>{where(v)}</td><td style={{ whiteSpace: 'normal' }}>{v.retakeNote ?? ''}</td>
              </tr>)))}</tbody>
          </table></div>
        </Panel>)}

      {d.retakeRequests.length > 0 && (
        <Panel title="طلبات إعادة التصوير">
          <div className="tblw"><table>
            <thead><tr><th>الصورة</th><th>الملاحظة</th><th>طلبها</th><th>الوقت</th><th>الحالة</th></tr></thead>
            <tbody>{d.retakeRequests.map(x => <tr key={x.id}><td><b>{x.label}</b></td><td style={{ whiteSpace: 'normal' }}>{x.note}</td><td>{x.by ?? '—'}</td><td className="num">{ksa(x.requestedAt)}</td>
              <td>{x.resolvedAt ? <span className="chip st-Approved">أُعيد التصوير {ksa(x.resolvedAt)}</span> : <span className="chip st-UnderReview">مفتوح — بانتظار السائق</span>}</td></tr>)}</tbody>
          </table></div>
        </Panel>)}

      {zoom && (
        <div className="lightbox" role="dialog" aria-label={zoom.label} onClick={() => setZoom(null)}>
          <img src={zoom.url} alt={zoom.label} />
          <div className="lightbox-cap">{zoom.label} — اضغط للإغلاق{' '}
            <button className="btn" onClick={e => { e.stopPropagation(); download(`/api/attachments/${zoom.id}`, `${r.tripNo}-${zoom.id.slice(0, 8)}.jpg`) }}>تنزيل</button></div>
        </div>)}
    </>
  )
}
