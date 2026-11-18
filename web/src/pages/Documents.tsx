// «وثائق المركبات والسائقين»: register fleet documents, follow expiry, renew with history, alert settings.
// Dates are visible to office roles; the copies (images / PDF) only to users with the Documents section (enforced by the server).
import { useEffect, useState, type FormEvent } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, blobUrl, download, fmt, ksa, post, upload } from '../api'
import { Field, Panel, useAction, useDrivers, useVehicles } from '../ui'
import { dateOnly, docChip, fieldKeys, useDocMeta, type DocField, type DocMeta, type DocRow, type DocType, type History } from '../orders'


async function attach(versionId: string, file: File | null) {
  if (!file || file.size === 0) return
  const fd = new FormData()
  fd.append('file', file); fd.append('ownerType', 'docversion'); fd.append('ownerId', versionId); fd.append('kind', 'document')
  await upload('/api/attachments', fd)
}

/** The inputs of one document type, in the type's own order and labels. */
function TypeFields({ type, values }: { type: DocType; values?: Partial<DocRow> }) {
  return <>{type.fields.map(f => {
    const req = f.required ? ' *' : ''
    switch (f.key) {
      case 'number': return <Field key={f.key} label={f.label + req}><input name="number" required={f.required} maxLength={60} defaultValue={values?.number} /></Field>
      case 'category': return <Field key={f.key} label={f.label + req}><input name="category" required={f.required} maxLength={40} defaultValue={values?.category} /></Field>
      case 'issuer': return <Field key={f.key} label={f.label + req}><input name="issuer" required={f.required} maxLength={100} defaultValue={values?.issuer} /></Field>
      case 'issueDate': return <Field key={f.key} label={f.label + req}><input name="issueDate" type="date" required={f.required} defaultValue={values?.issueDate} /></Field>
      case 'expiry': return <Field key={f.key} label={f.label + req}><input name="expiry" type="date" required={f.required} defaultValue={values?.expiry} /></Field>
      case 'image': return <Field key={f.key} label={f.label}><input name="file" type="file" accept="image/*,application/pdf" /></Field>
      default: return null
    }
  })}</>
}

function values(form: HTMLFormElement) {
  const f = new FormData(form)
  const v = (k: string) => { const s = String(f.get(k) ?? '').trim(); return s === '' ? undefined : s }
  return { body: { number: v('number'), category: v('category'), issuer: v('issuer'), issueDate: v('issueDate'), expiry: v('expiry'), note: v('note'), reason: v('reason') }, file: f.get('file') as File | null }
}

/** Register a document; with `ownerKind`/`ownerId` fixed when opened from a truck file or a driver. */
export function RegisterDoc({ meta, ownerKind, ownerId, onDone }: { meta: DocMeta; ownerKind?: 'vehicle' | 'driver'; ownerId?: string; onDone?: () => void }) {
  const act = useAction()
  const vehicles = useVehicles(), drivers = useDrivers()
  const types = meta.types.filter(t => t.isActive && (!ownerKind || t.appliesTo === ownerKind))
  const [typeId, setTypeId] = useState(types[0]?.id ?? '')
  const type = types.find(t => t.id === typeId)
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const { body, file } = values(form)
    const owner = ownerId ?? String(new FormData(form).get('ownerId') ?? '')
    act.run(async () => { const r = await post<{ id: string; versionId: string }>('/api/documents', { ...body, typeId, ownerId: owner }); await attach(r.versionId, file); form.reset(); onDone?.() }, 'سُجّلت الوثيقة.')
  }
  if (!type) return null
  return (
    <form className="filter" onSubmit={submit} key={typeId}>
      {act.banner}
      <div className="fg">
        <Field label="نوع الوثيقة *"><select value={typeId} onChange={e => setTypeId(e.target.value)}>{types.map(t => <option key={t.id} value={t.id}>{t.name} ({t.appliesTo === 'driver' ? 'سائق' : 'مركبة'})</option>)}</select></Field>
        {!ownerId && <Field label={type.appliesTo === 'driver' ? 'السائق *' : 'المركبة *'}><select name="ownerId" required><option value="">اختر</option>
          {type.appliesTo === 'driver' ? drivers.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>) : vehicles.data?.map(v => <option key={v.id} value={v.id}>{v.plate}</option>)}</select></Field>}
        <TypeFields type={type} />
        <Field label="ملاحظة"><input name="note" maxLength={500} /></Field>
      </div>
      <div className="actions"><button className="btn pri" disabled={act.busy}>تسجيل الوثيقة</button></div>
    </form>
  )
}

function DocTable({ rows, showOwner = true }: { rows: DocRow[]; showOwner?: boolean }) {
  const nav = useNavigate()
  return (
    <div className="tblw"><table>
      <thead><tr><th>الوثيقة</th>{showOwner && <th>اللوحة / السائق</th>}<th>الرقم</th><th>تاريخ الانتهاء</th><th>الأيام المتبقية</th><th>الحالة</th><th>نسخة</th></tr></thead>
      <tbody>{rows.map(r => (
        <tr key={r.id} className={`click ${r.status === 'expired' ? 'flag' : ''}`} onClick={() => nav(`/documents/${r.id}`)}>
          <td><b>{r.typeName}</b>{r.mandatory && <div className="sub">إلزامية للتشغيل</div>}</td>
          {showOwner && <td><b>{r.ownerName}</b><div className="sub">{r.ownerKind === 'driver' ? 'سائق' : 'مركبة'}</div></td>}
          <td className="num">{r.number ?? '—'}{(r.category || r.issuer) && <div className="sub">{r.category ?? r.issuer}</div>}</td>
          <td className="num">{r.expiry ?? '—'}</td>
          <td className={`num ${r.status === 'expired' ? 'diff' : ''}`}>{r.daysLeft ?? '—'}</td>
          <td><span className={`chip ${docChip[r.status]}`}>{r.statusLabel}</span></td>
          <td>{r.hasCopy ? 'مرفقة' : <span className="sub">—</span>}{r.version > 1 && <div className="sub">الإصدار {r.version}</div>}</td>
        </tr>))}</tbody>
    </table></div>
  )
}

/** «الوثائق» tab of a truck file or a driver: the owner's documents and a form for the ones not registered yet. */
export function DocsPanel({ ownerKind, ownerId }: { ownerKind: 'vehicle' | 'driver'; ownerId: string }) {
  const meta = useDocMeta()
  const q = useQuery({ queryKey: ['documents', 'owner', ownerId], queryFn: () => api<{ rows: DocRow[] }>(`/api/documents?ownerId=${ownerId}`) })
  const [adding, setAdding] = useState(false)
  if (meta.isError || q.isError) return <div className="err">{((meta.error ?? q.error) as Error).message}</div>
  const have = new Set(q.data?.rows.map(r => r.typeId))
  const missing = meta.data?.types.filter(t => t.isActive && t.appliesTo === ownerKind && !have.has(t.id)) ?? []
  return (
    <Panel title="الوثائق" actions={meta.data?.canEdit && <button className="btn" onClick={() => setAdding(!adding)}>{adding ? 'إغلاق' : 'تسجيل وثيقة'}</button>}>
      {adding && meta.data && <div style={{ padding: '0 14px' }}><RegisterDoc meta={meta.data} ownerKind={ownerKind} ownerId={ownerId} onDone={() => setAdding(false)} /></div>}
      <DocTable rows={q.data?.rows ?? []} showOwner={false} />
      {missing.length > 0 && <div className="sub" style={{ padding: '8px 14px' }}>غير مسجّلة: {missing.map(t => t.name + (t.mandatoryForDispatch ? ' (إلزامية)' : '')).join('، ')}</div>}
    </Panel>
  )
}

export default function Documents() {
  const meta = useDocMeta()
  const [tab, setTab] = useState<'report' | 'settings'>('report')
  const [filters, setFilters] = useState<Record<string, string>>({})
  const [adding, setAdding] = useState(false)
  const act = useAction()
  const qs = new URLSearchParams(Object.entries(filters).filter(([, v]) => v)).toString()
  const list = useQuery({ queryKey: ['documents', 'list', qs], queryFn: () => api<{ summary: { total: number; expired: number; soon: number; noDate: number }; rows: DocRow[] }>(`/api/documents?${qs}`) })
  if (meta.isError) return <div className="err">{(meta.error as Error).message}</div>
  const m = meta.data
  function search(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setFilters({ q: String(f.get('q') ?? ''), typeId: String(f.get('typeId') ?? ''), status: String(f.get('status') ?? ''), ownerKind: String(f.get('ownerKind') ?? '') })
  }
  const s = list.data?.summary
  return (
    <>
      <div className="spread"><h1>وثائق المركبات والسائقين</h1>
        <div className="row">
          {m?.canEdit && tab === 'report' && <button className="btn pri" onClick={() => setAdding(!adding)}>{adding ? 'إغلاق' : 'تسجيل وثيقة'}</button>}
          <button className="btn" onClick={() => act.run(() => download(`/api/documents?format=xlsx&${qs}`, 'fleet-documents.xlsx'), 'نُزّل ملف Excel.')}>تصدير Excel</button>
        </div></div>
      {act.banner}
      {m?.canEdit && <div className="tabs"><button className={tab === 'report' ? 'on' : ''} onClick={() => setTab('report')}>التقرير</button><button className={tab === 'settings' ? 'on' : ''} onClick={() => setTab('settings')}>الأنواع والتنبيهات</button></div>}
      {tab === 'settings' && m ? <DocSettings meta={m} /> : <>
        {adding && m && <RegisterDoc meta={m} onDone={() => setAdding(false)} />}
        {s && <div className="kpis">
          <div className="kpi"><div className="l">كل الوثائق</div><div className="v">{s.total}</div></div>
          <div className={`kpi ${s.expired ? 'attn' : ''}`}><div className="l">منتهية</div><div className="v">{s.expired}</div></div>
          <div className="kpi"><div className="l">قريبة من الانتهاء (خلال {m?.warnDays ?? 30} يومًا)</div><div className="v" style={{ color: 'var(--warn)' }}>{s.soon}</div></div>
          <div className="kpi"><div className="l">تاريخ الانتهاء غير مسجّل</div><div className="v">{s.noDate}</div></div>
        </div>}
        <form className="filter" onSubmit={search}>
          <div className="fg">
            <Field label="بحث: اللوحة أو اسم السائق أو رقم الوثيقة"><input name="q" maxLength={60} /></Field>
            <Field label="نوع الوثيقة"><select name="typeId"><option value="">الكل</option>{m?.types.map(t => <option key={t.id} value={t.id}>{t.name}</option>)}</select></Field>
            <Field label="الحالة"><select name="status"><option value="">الكل</option>{m?.statuses.map(x => <option key={x.value} value={x.value}>{x.label}</option>)}</select></Field>
            <Field label="يخص"><select name="ownerKind"><option value="">المركبات والسائقين</option><option value="vehicle">المركبات</option><option value="driver">السائقين</option></select></Field>
          </div>
          <div className="actions"><button className="btn pri">بحث</button></div>
        </form>
        <Panel title={`الوثائق — الأقرب انتهاءً أولًا (${list.data?.rows.length ?? 0})`}>
          <DocTable rows={list.data?.rows ?? []} />
          {list.data?.rows.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد وثائق مطابقة.</div>}
        </Panel>
      </>}
    </>
  )
}

function DocSettings({ meta }: { meta: DocMeta }) {
  const act = useAction()
  const [editing, setEditing] = useState<DocType | 'new' | null>(null)
  function saveSettings(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const offsets = String(f.get('offsets') ?? '').split(/[,،\s]+/).filter(Boolean).map(Number)
    const fm = String(f.get('fleetManagerUserId') ?? '')
    act.run(() => api('/api/documents/settings', { method: 'PUT', body: JSON.stringify({ offsets, fleetManagerUserId: fm || null }) }), 'حُفظت إعدادات التنبيهات.')
  }
  function saveType(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const fields: DocField[] = fieldKeys.filter(([k]) => f.get(`use_${k}`)).map(([k, l]) => ({ key: k, label: String(f.get(`label_${k}`) || l), required: !!f.get(`req_${k}`) }))
    const body = { name: f.get('name'), appliesTo: f.get('appliesTo'), fields, mandatoryForDispatch: !!f.get('mandatory'), isActive: !!f.get('active'), sortOrder: Number(f.get('sortOrder') || 100) }
    const t = editing
    act.run(async () => { if (t === 'new') await post('/api/documents/types', body); else if (t) await api(`/api/documents/types/${t.id}`, { method: 'PUT', body: JSON.stringify(body) }); setEditing(null) }, 'حُفظ نوع الوثيقة.')
  }
  const t = editing === 'new' ? null : editing
  return (
    <>
      {act.banner}
      <Panel title="مواعيد التنبيه والمستلمون">
        <form className="filter" style={{ boxShadow: 'none' }} onSubmit={saveSettings}>
          <div className="fg">
            <Field label="أيام التنبيه قبل الانتهاء (0 = يوم الانتهاء)"><input name="offsets" defaultValue={meta.offsets.join(', ')} /></Field>
            <Field label="مسؤول الأسطول (يستلم التنبيهات مع مدير المنظومة)"><select name="fleetManagerUserId" defaultValue={meta.fleetManagerUserId ?? ''}><option value="">— لا أحد —</option>
              {meta.users?.map(u => <option key={u.id} value={u.id}>{u.displayName} ({u.username})</option>)}</select></Field>
          </div>
          <div className="sub">يجري الفحص يوميًا الساعة 06:00 بتوقيت السعودية. يُرسل كل تنبيه مرة واحدة لكل وثيقة وتاريخ انتهاء وموعد، ويبدأ الجدول من جديد عند تجديد الوثيقة.</div>
          <div className="actions">
            {meta.isAdmin && <button type="button" className="btn" onClick={() => act.run(async () => { const r = await post<{ sent: number }>('/api/documents/run-alerts'); return r }, 'جرى فحص الانتهاء الآن.')}>تشغيل الفحص الآن</button>}
            <button className="btn pri" disabled={act.busy}>حفظ</button></div>
        </form>
      </Panel>
      <Panel title="أنواع الوثائق" actions={<button className="btn" onClick={() => setEditing(editing ? null : 'new')}>{editing ? 'إغلاق' : 'إضافة نوع'}</button>}>
        {editing && (
          <form className="filter" style={{ boxShadow: 'none' }} onSubmit={saveType} key={t?.id ?? 'new'}>
            <div className="fg">
              <Field label="الاسم *"><input name="name" required maxLength={80} defaultValue={t?.name} /></Field>
              <Field label="يخص *"><select name="appliesTo" defaultValue={t?.appliesTo ?? 'vehicle'}><option value="vehicle">المركبة</option><option value="driver">السائق</option></select></Field>
              <Field label="الترتيب"><input name="sortOrder" type="number" min={0} max={1000} defaultValue={t?.sortOrder ?? 100} /></Field>
              <label className="fl" style={{ flexDirection: 'row', gap: 8, alignItems: 'center' }}><input type="checkbox" name="mandatory" style={{ width: 'auto' }} defaultChecked={t?.mandatoryForDispatch} /> إلزامية للتشغيل (تمنع الإسناد عند انتهائها)</label>
              <label className="fl" style={{ flexDirection: 'row', gap: 8, alignItems: 'center' }}><input type="checkbox" name="active" style={{ width: 'auto' }} defaultChecked={t?.isActive ?? true} /> مفعّلة</label>
            </div>
            <div className="tblw" style={{ marginTop: 10 }}><table><thead><tr><th>الحقل</th><th>يظهر</th><th>التسمية</th><th>مطلوب</th></tr></thead>
              <tbody>{fieldKeys.map(([k, l]) => { const f = t?.fields.find(x => x.key === k); return (
                <tr key={k}><td>{l}</td><td><input type="checkbox" name={`use_${k}`} style={{ width: 'auto' }} defaultChecked={editing === 'new' ? ['number', 'expiry', 'image'].includes(k) : !!f} /></td>
                  <td><input name={`label_${k}`} defaultValue={f?.label ?? l} maxLength={60} /></td><td>{k !== 'image' && <input type="checkbox" name={`req_${k}`} style={{ width: 'auto' }} defaultChecked={editing === 'new' ? ['number', 'expiry'].includes(k) : f?.required} />}</td></tr>) })}</tbody></table></div>
            <div className="actions"><button className="btn pri" disabled={act.busy}>حفظ النوع</button></div>
          </form>)}
        <div className="tblw"><table><thead><tr><th>النوع</th><th>يخص</th><th>الحقول</th><th>إلزامية للتشغيل</th><th>الحالة</th><th></th></tr></thead>
          <tbody>{meta.types.map(x => <tr key={x.id}><td><b>{x.name}</b></td><td>{x.appliesTo === 'driver' ? 'السائق' : 'المركبة'}</td><td style={{ whiteSpace: 'normal' }}>{x.fields.map(f => f.label + (f.required ? '*' : '')).join('، ')}</td>
            <td>{x.mandatoryForDispatch ? 'نعم' : 'لا'}</td><td>{x.isActive ? 'مفعّلة' : 'موقوفة'}</td><td><button className="btn" onClick={() => setEditing(x)}>تعديل</button></td></tr>)}</tbody></table></div>
      </Panel>
    </>
  )
}

interface DocDetail {
  document: DocRow; type: DocType; currentVersionId: string; canSeeCopies: boolean; canEdit: boolean
  versions: { id: string; version: number; kind: string; number?: string; category?: string; issuer?: string; issueDate?: string; expiry?: string; note?: string; at: string; by?: string; current: boolean
    files: { id: string; fileName: string; contentType: string; size: number; uploadedAt: string }[] | null }[]
  history: History[]
}

function CopyLink({ id, name, type }: { id: string; name: string; type: string }) {
  const [url, setUrl] = useState<string | null>(null)
  useEffect(() => {
    if (!type.startsWith('image/')) return
    let u: string | null = null
    blobUrl(`/api/attachments/${id}`).then(x => { u = x; setUrl(x) }).catch(() => setUrl(null))
    return () => { if (u) URL.revokeObjectURL(u) }
  }, [id, type])
  const open = async () => window.open(url ?? await blobUrl(`/api/attachments/${id}`), '_blank', 'noopener')
  return <button className="btn" style={{ padding: 4 }} onClick={open} title={name}>{url ? <img src={url} alt={name} style={{ width: 96, height: 72, objectFit: 'cover', display: 'block', borderRadius: 6 }} /> : `📄 ${name || 'نسخة'}`}</button>
}

export function DocumentView() {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const act = useAction()
  const [mode, setMode] = useState<'' | 'renew' | 'correct' | 'copy'>('')
  const q = useQuery({ queryKey: ['documents', 'one', id], queryFn: () => api<DocDetail>(`/api/documents/${id}`) })
  if (q.isError) return <div className="err">{(q.error as Error).message}</div>
  const d = q.data
  if (!d) return <div className="sub">جارٍ التحميل…</div>
  const r = d.document
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const { body, file } = values(form)
    if (mode === 'renew') act.run(async () => { const x = await post<{ versionId: string }>(`/api/documents/${id}/renew`, body); await attach(x.versionId, file); setMode('') }, 'جُدّدت الوثيقة؛ الإصدار السابق محفوظ في السجل.')
    else if (mode === 'correct') act.run(async () => { await api(`/api/documents/${id}`, { method: 'PUT', body: JSON.stringify(body) }); setMode('') }, 'حُفظ التصحيح.')
    else if (mode === 'copy') act.run(async () => { await attach(d!.currentVersionId, file); setMode('') }, 'أُرفقت النسخة.')
  }
  return (
    <>
      <div className="spread noprint">
        <div className="row"><button className="btn" onClick={() => nav(-1)}>← رجوع</button><Link className="btn" to="/documents">كل الوثائق</Link>
          <h1>{r.typeName}</h1><span className={`chip ${docChip[r.status]}`}>{r.statusLabel}</span></div>
        {d.canEdit && <div className="row">
          <button className="btn pri" onClick={() => setMode(mode === 'renew' ? '' : 'renew')}>تجديد الوثيقة</button>
          <button className="btn" onClick={() => setMode(mode === 'correct' ? '' : 'correct')}>تصحيح البيانات</button>
          {d.canSeeCopies && <button className="btn" onClick={() => setMode(mode === 'copy' ? '' : 'copy')}>إرفاق نسخة</button>}
        </div>}
      </div>
      {act.banner}
      {mode && (
        <form className="filter" onSubmit={submit} key={mode}>
          <div className="sub" style={{ marginBottom: 8 }}>{mode === 'renew' ? 'أدخل بيانات الوثيقة الجديدة وأرفق النسخة المجددة. يبقى الإصدار الحالي في السجل.' : mode === 'correct' ? 'لتصحيح خطأ إدخال في الإصدار الحالي فقط (ليس تجديدًا).' : 'نسخة للإصدار الحالي.'}</div>
          <div className="fg">
            {mode === 'copy' ? <Field label="الملف (صورة أو PDF)"><input name="file" type="file" required accept="image/*,application/pdf" /></Field>
              : <TypeFields type={mode === 'correct' ? { ...d.type, fields: d.type.fields.filter(f => f.key !== 'image') } : d.type} values={mode === 'correct' ? r : undefined} />}
            {mode === 'renew' && <Field label="ملاحظة"><input name="note" maxLength={500} /></Field>}
            {mode === 'correct' && <Field label="سبب التصحيح *"><input name="reason" required minLength={3} maxLength={500} /></Field>}
          </div>
          <div className="actions"><button className="btn pri" disabled={act.busy}>{mode === 'renew' ? 'حفظ التجديد' : mode === 'correct' ? 'حفظ التصحيح' : 'رفع'}</button></div>
        </form>)}
      <Panel title={`${r.ownerKind === 'driver' ? 'السائق' : 'المركبة'}: ${r.ownerName}`} actions={r.ownerKind === 'vehicle' ? <Link className="btn" to={`/custody/truck/${r.ownerId}`}>ملف الشاحنة</Link> : undefined}>
        <div className="facts">
          {d.type.fields.filter(f => f.key !== 'image').map(f => {
            const v = f.key === 'number' ? r.number : f.key === 'category' ? r.category : f.key === 'issuer' ? r.issuer : f.key === 'issueDate' ? r.issueDate : r.expiry
            return <div key={f.key}><span className="sub">{f.label}</span><div className="num">{v ?? '—'}</div></div>
          })}
          <div><span className="sub">الأيام المتبقية</span><div className={`num ${r.status === 'expired' ? 'diff' : ''}`}>{r.daysLeft ?? '—'}</div></div>
          <div><span className="sub">إلزامية للتشغيل</span><div>{r.mandatory ? 'نعم — تمنع إسناد الرحلات عند انتهائها' : 'لا'}</div></div>
          <div><span className="sub">الإصدار الحالي</span><div className="num">{r.version}</div></div>
        </div>
        {!d.canSeeCopies && <div className="sub" style={{ padding: '0 16px 12px' }}>نسخ الوثيقة لا تظهر لصلاحيتك.</div>}
      </Panel>
      <Panel title="الإصدارات (لا يُحذف أي إصدار)">
        <div className="tblw"><table><thead><tr><th>الإصدار</th><th>النوع</th><th>الرقم</th><th>الإصدار / البداية</th><th>الانتهاء</th><th>سُجّل</th>{d.canSeeCopies && <th>النسخ</th>}</tr></thead>
          <tbody>{d.versions.map(v => <tr key={v.id}><td className="num"><b>{v.version}</b>{v.current && <div className="sub">الحالي</div>}</td><td>{v.kind}{v.note && <div className="sub">{v.note}</div>}</td>
            <td className="num">{v.number ?? '—'}<div className="sub">{v.category ?? v.issuer ?? ''}</div></td><td className="num">{v.issueDate ?? '—'}</td><td className="num">{v.expiry ?? '—'}</td>
            <td className="num">{ksa(v.at)}<div className="sub">{v.by}</div></td>
            {d.canSeeCopies && <td><div className="row">{v.files?.map(f => <CopyLink key={f.id} id={f.id} name={f.fileName} type={f.contentType} />)}{v.files?.length === 0 && <span className="sub">لا توجد</span>}</div></td>}</tr>)}</tbody></table></div>
      </Panel>
      <Panel title="سجل الوثيقة">
        <div className="tblw"><table><thead><tr><th>الوقت</th><th>الإجراء</th><th>بواسطة</th><th>التفاصيل</th><th>السابق</th><th>الجديد</th></tr></thead>
          <tbody>{d.history.slice().reverse().map((h, i) => <tr key={i}><td className="num">{ksa(h.at)}</td><td><b>{h.action}</b></td><td>{h.by ?? '—'}</td><td style={{ whiteSpace: 'normal' }}>{h.detail}</td>
            <td className="num" style={{ whiteSpace: 'normal', fontSize: 12 }}>{h.oldValue ?? ''}</td><td className="num" style={{ whiteSpace: 'normal', fontSize: 12 }}>{h.newValue ?? ''}</td></tr>)}</tbody></table></div>
      </Panel>
      <div className="sub">آخر تحديث {dateOnly(r.updatedAt)} · {fmt(d.versions.length)} إصدار</div>
    </>
  )
}
