// «العُهد وقطع الغيار»: where every part is, on which truck, who holds it, and what happened to it.
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, ksa, post } from '../api'
import { Field, Panel, useAction } from '../ui'
import {
  day, fateOf, holderLabel, money, num, parseHolder, statusChip, str, useCatalog, useCMeters, useCVehicles, useEmployees, useHolders, useMeta, useParts, useTransfers,
  useWarehouses, useWorkOrders, type CatalogItem, type Movement, type Part, type PartStatus, type Transfer,
} from '../custody'
import { CatalogSelect, FateFields, HolderSelect, PartPicker, VehicleSelect, WarehouseSelect, WoOrReason } from './CustodyForms'
import CustodyReports from './CustodyReports'
import Attachments from './CustodyAttachments'

const tabs: [string, string][] = [['home', 'لوحة وبحث'], ['parts', 'القطع'], ['stock', 'المخزون'], ['moves', 'الحركات'], ['wo', 'أوامر الصيانة'], ['reports', 'التقارير'], ['setup', 'الإعداد']]

/** Submits a form through useAction and clears it on success. */
function useSubmit() {
  const act = useAction()
  const submit = (run: (f: FormData) => Promise<unknown>, ok: string) => (e: FormEvent<HTMLFormElement>) => {
    e.preventDefault()
    const form = e.currentTarget
    const f = new FormData(form)
    act.run(async () => { const r = await run(f); form.reset(); return r }, ok)
  }
  return { ...act, submit }
}

export default function Custody() {
  const [sp, setSp] = useSearchParams()
  const tab = sp.get('tab') ?? 'home'
  const meta = useMeta()
  return (
    <>
      <div className="spread noprint"><h1>العُهد وقطع غيار الشاحنات</h1></div>
      <div className="tabs noprint" role="tablist">
        {tabs.map(([k, l]) => <button key={k} role="tab" aria-selected={tab === k} className={tab === k ? 'on' : ''} onClick={() => setSp({ tab: k })}>{l}</button>)}
      </div>
      {tab === 'home' && <Home />}
      {tab === 'parts' && <PartsTab canEdit={!!meta.data?.canEdit} />}
      {tab === 'stock' && <StockTab canEdit={!!meta.data?.canEdit} canSeeCost={!!meta.data?.canSeeCost} />}
      {tab === 'moves' && <MovesTab canEdit={!!meta.data?.canEdit} />}
      {tab === 'wo' && <WorkOrdersTab canEdit={!!meta.data?.canEdit} canSeeCost={!!meta.data?.canSeeCost} />}
      {tab === 'reports' && <CustodyReports />}
      {tab === 'setup' && <SetupTab canEdit={!!meta.data?.canEdit} canSeeCost={!!meta.data?.canSeeCost} />}
    </>
  )
}

export function StatusChip({ s, label }: { s: PartStatus; label: string }) {
  return <span className={`chip ${statusChip[s]}`}>{label}</span>
}

// ── home: search (keyboard / barcode scanner), KPIs, pending hand-overs, trucks ───────────────────────────────────
interface SearchResult { exactPartId?: string; parts: Part[]; vehicles: { id: string; plate: string; internalNo?: string; make?: string; model?: string }[]; holders: { kind: string; id: string; name: string; role: string }[]; catalog: { id: string; code: string; name: string; category: string }[] }

function Home() {
  const nav = useNavigate()
  const [q, setQ] = useState('')
  const [err, setErr] = useState('')
  const [res, setRes] = useState<SearchResult | null>(null)
  const parts = useParts('take=2000')
  const cat = useCatalog()
  const pending = useTransfers('Pending')
  const wos = useWorkOrders(true)
  const vehicles = useCVehicles()
  const meta = useMeta()

  async function search(e: FormEvent) {
    e.preventDefault(); setErr('')
    try {
      const r = await api<SearchResult>(`/api/custody/search?q=${encodeURIComponent(q.trim())}`)
      if (r.exactPartId) { nav(`/custody/part/${r.exactPartId}`); return }
      setRes(r)
    } catch (x) { setErr((x as Error).message); setRes(null) }
  }
  const count = (s: PartStatus) => parts.data?.filter(p => p.status === s).length ?? 0
  const soon = new Date(Date.now() + 30 * 864e5).toISOString().slice(0, 10)
  const today = new Date().toISOString().slice(0, 10)
  const expiring = parts.data?.filter(p => p.warrantyEnd && p.warrantyEnd >= today && p.warrantyEnd <= soon && p.status !== 'Scrapped').length ?? 0
  const inspect = parts.data?.filter(p => (p.nextInspection && p.nextInspection <= soon) || (p.expiryDate && p.expiryDate <= soon)).filter(p => !['Scrapped', 'Lost'].includes(p.status)).length ?? 0
  const kpis: [string, number, boolean, string][] = [
    ['مركّبة على الشاحنات', count('Installed'), false, 'parts&status=Installed'], ['بالمستودع', count('Stock'), false, 'parts&status=Stock'], ['عهد لدى الموظفين', count('Issued'), false, 'parts&status=Issued'],
    ['تحت الإصلاح', count('Repair'), count('Repair') > 0, 'parts&status=Repair'], ['تالفة / مفقودة', count('Damaged') + count('Lost'), count('Damaged') + count('Lost') > 0, 'reports&r=damaged'],
    ['أصناف عند الحد الأدنى', cat.data?.filter(c => c.belowMin).length ?? 0, (cat.data?.filter(c => c.belowMin).length ?? 0) > 0, 'reports&r=shortages'],
    ['ضمان ينتهي خلال 30 يومًا', expiring, expiring > 0, 'reports&r=warranty'], ['فحص / صلاحية خلال 30 يومًا', inspect, inspect > 0, 'parts&due=1'],
    ['تسليمات بانتظار القبول', pending.data?.length ?? 0, (pending.data?.length ?? 0) > 0, 'moves'], ['أوامر صيانة مفتوحة', wos.data?.length ?? 0, false, 'wo'],
  ]
  return (
    <>
      <form className="filter" onSubmit={search}>
        <div className="row" style={{ alignItems: 'flex-end' }}>
          <div style={{ flex: 1, minWidth: 260 }}><Field label="بحث: لوحة الشاحنة، اسم الموظف أو السائق، الرقم التسلسلي، أو امسح الباركود / QR">
            <input value={q} onChange={e => setQ(e.target.value)} autoFocus maxLength={80} placeholder="مثال: P-000123 أو 4821 أو سالم" /></Field></div>
          <button className="btn pri">بحث</button>
        </div>
        {err && <div className="err" style={{ marginTop: 8 }}>{err}</div>}
      </form>
      {res && (
        <Panel title="نتائج البحث" actions={<button className="btn" onClick={() => setRes(null)}>إغلاق</button>}>
          <div style={{ padding: 12 }} className="row">
            {res.vehicles.map(v => <Link key={v.id} className="chip st-EnRoute" to={`/custody/truck/${v.id}`}>🚚 {v.plate}</Link>)}
            {res.holders.map(h => <Link key={h.id} className="chip st-WaitingLoad" to={`/custody?tab=reports&r=custody&holder=${h.kind}:${h.id}`}>👤 {h.name} · {h.role}</Link>)}
            {res.catalog.map(c => <Link key={c.id} className="chip st-WaitingLoad" to={`/custody?tab=parts&catalogId=${c.id}`}>📦 {c.code} · {c.name}</Link>)}
          </div>
          <PartsTable parts={res.parts} />
          {res.parts.length + res.vehicles.length + res.holders.length + res.catalog.length === 0 && <div className="sub" style={{ padding: 16 }}>لا نتائج.</div>}
        </Panel>
      )}
      <div className="kpis">
        {kpis.map(([l, v, attn, to]) => <Link key={l} to={`/custody?tab=${to}`} className={`kpi ${attn ? 'attn' : ''}`} style={{ textDecoration: 'none', color: 'inherit' }}><div className="l">{l}</div><div className="v">{v}</div></Link>)}
      </div>
      {(pending.data?.length ?? 0) > 0 && <Panel title="تسليمات بانتظار قبول المستلم"><TransfersTable rows={pending.data ?? []} canDecide={!!meta.data?.canEdit} /></Panel>}
      <Panel title="الشاحنات — افتح ملف الشاحنة">
        <div className="tblw"><table>
          <thead><tr><th>اللوحة</th><th>الرقم الداخلي</th><th>المركبة</th><th>السائق الحالي</th><th>قطع مركّبة</th><th>أوامر مفتوحة</th></tr></thead>
          <tbody>{vehicles.data?.map(v => <tr key={v.id} className="click" onClick={() => nav(`/custody/truck/${v.id}`)}><td className="num"><b>{v.plate}</b></td><td>{v.internalNo}</td><td>{v.make} {v.model}</td><td>{v.driver ?? '—'}</td><td className="num">{v.installed}</td><td className="num">{v.openWorkOrders}</td></tr>)}</tbody>
        </table></div>
      </Panel>
    </>
  )
}

export function PartsTable({ parts, cost }: { parts: Part[]; cost?: boolean }) {
  const nav = useNavigate()
  return (
    <div className="tblw"><table>
      <thead><tr><th>رقم القطعة</th><th>الصنف</th><th>الرقم التسلسلي</th><th>الماركة</th><th>الحالة</th><th>الموقع / الحامل</th><th>الضمان</th>{cost && <th>التكلفة</th>}</tr></thead>
      <tbody>{parts.map(p => (
        <tr key={p.id} className="click" onClick={() => nav(`/custody/part/${p.id}`)}>
          <td className="num"><b>{p.internalNo}</b>{p.pendingTransfer && <div className="sub">تسليم معلّق {p.pendingTransfer}</div>}</td><td>{p.name}<div className="sub">{p.code} · {p.category}</div></td>
          <td className="num">{p.serial ?? '—'}</td><td>{p.brand} {p.model}</td><td><StatusChip s={p.status} label={p.statusLabel} /></td><td>{p.location || '—'}</td>
          <td className={p.warranty === 'ينتهي قريبًا' ? 'diff' : ''}>{p.warranty}{p.warrantyEnd && <div className="sub num">{p.warrantyEnd}</div>}</td>{cost && <td className="num">{money(p.cost)}</td>}
        </tr>))}</tbody>
    </table></div>
  )
}

// ── parts ───────────────────────────────────────────────────────────────────────────────────────────────────────
function PartsTab({ canEdit }: { canEdit: boolean }) {
  const [sp, setSp] = useSearchParams()
  const meta = useMeta()
  const wh = useWarehouses()
  const nav = useNavigate()
  const keys = ['q', 'status', 'category', 'warehouseId', 'catalogId']
  const qs = new URLSearchParams(keys.filter(k => sp.get(k)).map(k => [k, sp.get(k)!])).toString()
  const parts = useParts(qs + '&take=2000')
  const [adding, setAdding] = useState(false)
  const due = sp.get('due') === '1'
  const soon = new Date(Date.now() + 30 * 864e5).toISOString().slice(0, 10)
  const rows = (parts.data ?? []).filter(p => !due || ((p.nextInspection && p.nextInspection <= soon) || (p.expiryDate && p.expiryDate <= soon)) && !['Scrapped', 'Lost'].includes(p.status))

  function filter(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const next = new URLSearchParams({ tab: 'parts' })
    keys.forEach(k => { const v = str(f.get(k)); if (v) next.set(k, v) })
    setSp(next)
  }
  return (
    <>
      <form className="filter" onSubmit={filter} key={qs}>
        <div className="fg">
          <Field label="بحث (رقم، تسلسلي، صنف، لوحة، حامل)"><input name="q" defaultValue={sp.get('q') ?? ''} maxLength={60} /></Field>
          <Field label="الحالة"><select name="status" defaultValue={sp.get('status') ?? ''}><option value="">الكل</option>{meta.data?.statuses.map(s => <option key={s.value} value={s.value}>{s.label}</option>)}</select></Field>
          <Field label="الفئة"><select name="category" defaultValue={sp.get('category') ?? ''}><option value="">الكل</option>{meta.data?.categories.map(c => <option key={c}>{c}</option>)}</select></Field>
          <Field label="المستودع"><select name="warehouseId" defaultValue={sp.get('warehouseId') ?? ''}><option value="">الكل</option>{wh.data?.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}</select></Field>
        </div>
        <div className="actions">
          {canEdit && <button type="button" className="btn ghost" onClick={() => setAdding(!adding)}>{adding ? 'إخفاء النموذج' : '+ استلام قطعة جديدة'}</button>}
          <button type="button" className="btn" disabled={!rows.length} onClick={() => nav(`/custody/labels?ids=${rows.slice(0, 80).map(p => p.id).join(',')}`)}>طباعة ملصقات QR ({Math.min(rows.length, 80)})</button>
          <button className="btn pri">تصفية</button>
        </div>
      </form>
      {adding && canEdit && <NewPartForm />}
      <Panel title={`القطع (${rows.length})${due ? ' — فحص أو صلاحية خلال 30 يومًا' : ''}`}>
        <PartsTable parts={rows} cost={meta.data?.canSeeCost} />
        {parts.isSuccess && rows.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد قطع مطابقة.</div>}
      </Panel>
    </>
  )
}

function NewPartForm() {
  const nav = useNavigate()
  const meters = useCMeters()
  const cat = useCatalog()
  const [catId, setCatId] = useState('')
  const c = cat.data?.find(x => x.id === catId)
  const act = useAction()
  function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const body = {
      catalogId: str(f.get('catalogId')), warehouseId: str(f.get('warehouseId')), serial: str(f.get('serial')), brand: str(f.get('brand')), model: str(f.get('model')), supplier: str(f.get('supplier')),
      invoiceNo: str(f.get('invoiceNo')), purchaseDate: str(f.get('purchaseDate')), cost: num(f.get('cost')), warrantyStart: str(f.get('warrantyStart')), warrantyEnd: str(f.get('warrantyEnd')),
      warrantyTerms: str(f.get('warrantyTerms')), batchNo: str(f.get('batchNo')), specs: str(f.get('specs')), expiryDate: str(f.get('expiryDate')), nextInspection: str(f.get('nextInspection')),
      condition: str(f.get('condition')), notes: str(f.get('notes')), meterId: str(f.get('meterId')), bin: str(f.get('bin')),
    }
    act.run(async () => { const p = await post<Part>('/api/custody/parts', body); nav(`/custody/part/${p.id}`) }, 'استُلمت القطعة.')
  }
  return (
    <form className="filter" onSubmit={save}>
      <h3 style={{ marginBottom: 10 }}>استلام قطعة فردية (يصدر النظام رقمًا داخليًا ورمز QR)</h3>
      {act.banner}
      <div className="fg">
        <CatalogSelect tracking="Individual" label="الصنف *" onChange={setCatId} />
        <WarehouseSelect label="المستودع *" />
        <Field label="موقع التخزين (رف/درج)"><input name="bin" maxLength={40} /></Field>
        <Field label="الرقم التسلسلي للمصنع (إن وجد)"><input name="serial" maxLength={60} dir="ltr" /></Field>
        <Field label="الماركة"><input name="brand" maxLength={60} placeholder={c?.brand} /></Field>
        <Field label="الموديل"><input name="model" maxLength={60} placeholder={c?.model} /></Field>
        <Field label="المورد"><input name="supplier" maxLength={100} /></Field>
        <Field label="رقم الفاتورة"><input name="invoiceNo" maxLength={60} /></Field>
        <Field label="تاريخ الشراء"><input name="purchaseDate" type="date" /></Field>
        <Field label="التكلفة"><input name="cost" type="number" min={0} step="0.01" placeholder={c?.unitCost?.toString()} /></Field>
        <Field label="بداية الضمان"><input name="warrantyStart" type="date" /></Field>
        <Field label="نهاية الضمان"><input name="warrantyEnd" type="date" /></Field>
        <Field label="شروط الضمان"><input name="warrantyTerms" maxLength={300} /></Field>
        <Field label="رقم الدفعة"><input name="batchNo" maxLength={60} /></Field>
        <Field label="المواصفات"><input name="specs" maxLength={300} placeholder={c?.specs} /></Field>
        <Field label="تاريخ الانتهاء (خراطيم/سلامة)"><input name="expiryDate" type="date" /></Field>
        <Field label="الفحص الدوري القادم"><input name="nextInspection" type="date" /></Field>
        <Field label="الحالة"><select name="condition" defaultValue="جديد"><option>جديد</option><option>مستعمل</option><option>مُجدّد</option></select></Field>
        {c?.category === 'عداد وقود' && <Field label="ربط بعداد الوقود المسجل"><select name="meterId" defaultValue=""><option value="">—</option>{meters.data?.map(m => <option key={m.id} value={m.id}>{m.serial}{m.plate ? ` · ${m.plate}` : ''}</option>)}</select></Field>}
        <Field label="ملاحظات"><input name="notes" maxLength={500} /></Field>
      </div>
      <div className="actions"><button className="btn pri" disabled={act.busy}>استلام وإصدار الرقم</button></div>
    </form>
  )
}

// ── quantity stock ──────────────────────────────────────────────────────────────────────────────────────────────
interface StockView { balances: { catalogId: string; code: string; name: string; unit?: string; category: string; warehouseId: string; warehouse: string; qty: number; bin?: string; min?: number; total: number; belowMin: boolean; value?: number }[]; custody: { holderKind: string; holderId: string; holder: string; catalogId: string; code: string; name: string; unit?: string; qty: number; since: string }[] }

function StockTab({ canEdit, canSeeCost }: { canEdit: boolean; canSeeCost: boolean }) {
  const [whId, setWhId] = useState('')
  const stock = useQuery({ queryKey: ['custody', 'stock', whId], queryFn: () => api<StockView>(`/api/custody/stock${whId ? `?warehouseId=${whId}` : ''}`) })
  const wh = useWarehouses()
  const [form, setForm] = useState('')
  const forms: [string, string][] = [['receive', 'استلام كمية'], ['issue', 'صرف كمية'], ['return', 'إرجاع من عهدة'], ['consume', 'استهلاك من عهدة على شاحنة'], ['transfer', 'نقل عهدة كمية'], ['move', 'نقل بين المستودعات'], ['adjust', 'تسوية'], ['count', 'جرد مستودع']]
  return (
    <>
      <div className="filter row">
        <Field label="المستودع"><select value={whId} onChange={e => setWhId(e.target.value)}><option value="">كل المستودعات</option>{wh.data?.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}</select></Field>
        {canEdit && <div className="row" style={{ marginInlineStart: 'auto' }}>{forms.map(([k, l]) => <button key={k} className={`btn ${form === k ? 'pri' : ''}`} onClick={() => setForm(form === k ? '' : k)}>{l}</button>)}</div>}
      </div>
      {canEdit && form && <StockForm kind={form} />}
      <Panel title="أرصدة الأصناف بالكمية">
        <div className="tblw"><table>
          <thead><tr><th>رمز الصنف</th><th>الصنف</th><th>المستودع</th><th>موقع التخزين</th><th>الرصيد</th><th>الإجمالي</th><th>الحد الأدنى</th>{canSeeCost && <th>القيمة</th>}</tr></thead>
          <tbody>{stock.data?.balances.map(b => (
            <tr key={b.catalogId + b.warehouseId} className={b.belowMin ? 'flag' : ''}><td className="num">{b.code}</td><td>{b.name}<div className="sub">{b.category}</div></td><td>{b.warehouse}</td><td>{b.bin ?? '—'}</td>
              <td className="num"><b>{money(b.qty)}</b> {b.unit}</td><td className="num">{money(b.total)}</td><td className="num">{money(b.min)}</td>{canSeeCost && <td className="num">{money(b.value)}</td>}</tr>))}</tbody>
        </table></div>
      </Panel>
      <Panel title="مواد بالكمية في عهدة الأشخاص">
        <div className="tblw"><table>
          <thead><tr><th>الحامل</th><th>النوع</th><th>الصنف</th><th>الكمية</th><th>منذ</th></tr></thead>
          <tbody>{stock.data?.custody.map(c => <tr key={c.holderId + c.catalogId}><td>{c.holder}</td><td>{holderLabel(c.holderKind)}</td><td>{c.code} · {c.name}</td><td className="num">{money(c.qty)} {c.unit}</td><td className="num">{ksa(c.since)}</td></tr>)}</tbody>
        </table></div>
      </Panel>
    </>
  )
}

function StockForm({ kind }: { kind: string }) {
  const act = useSubmit()
  const [vehicleId, setVehicleId] = useState('')
  const [to, setTo] = useState('holder')
  const body = (f: FormData) => ({ catalogId: str(f.get('catalogId')), warehouseId: str(f.get('warehouseId')), qty: num(f.get('qty')), note: str(f.get('note')) })
  let content: ReactNode = null
  let onSubmit = act.submit(async () => undefined, '')
  const qtyF = <Field label="الكمية *"><input name="qty" type="number" min={0.01} step="0.01" required /></Field>
  const note = <Field label="ملاحظة"><input name="note" maxLength={500} /></Field>
  if (kind === 'receive') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><WarehouseSelect label="المستودع *" />{qtyF}<Field label="سعر الوحدة"><input name="unitCost" type="number" min={0} step="0.01" /></Field>
      <Field label="المورد"><input name="supplier" maxLength={100} /></Field><Field label="رقم الفاتورة"><input name="invoiceNo" maxLength={60} /></Field><Field label="رقم الدفعة"><input name="batchNo" maxLength={60} /></Field>
      <Field label="موقع التخزين"><input name="bin" maxLength={40} /></Field>{note}</>
    onSubmit = act.submit(f => post('/api/custody/stock/receive', { ...body(f), unitCost: num(f.get('unitCost')), supplier: str(f.get('supplier')), invoiceNo: str(f.get('invoiceNo')), batchNo: str(f.get('batchNo')), bin: str(f.get('bin')) }), 'استُلمت الكمية.')
  } else if (kind === 'issue') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><WarehouseSelect label="من المستودع *" />{qtyF}
      <Field label="الصرف إلى"><select value={to} onChange={e => setTo(e.target.value)}><option value="holder">شخص (عهدة، بانتظار قبوله)</option><option value="vehicle">شاحنة (استهلاك مباشر)</option></select></Field>
      {to === 'vehicle' ? <><VehicleSelect onChange={setVehicleId} /><Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field></> : <><HolderSelect /><Field label="سلّمها"><input name="handoverBy" maxLength={100} /></Field>
        <label className="fl" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}><input type="checkbox" name="receiverSigned" style={{ width: 'auto' }} /> وقّع المستلم عند الاستلام (يُعتمد فورًا)</label></>}
      <WoOrReason vehicleId={to === 'vehicle' ? vehicleId : undefined} />{note}</>
    onSubmit = act.submit(f => post('/api/custody/stock/issue', { ...body(f), ...(to === 'holder' ? parseHolder(f.get('holder')) : {}), vehicleId: to === 'vehicle' ? str(f.get('vehicleId')) : undefined,
      workOrderId: str(f.get('workOrderId')), reason: str(f.get('reason')), km: num(f.get('km')), handoverBy: str(f.get('handoverBy')), receiverSigned: f.get('receiverSigned') === 'on' }), 'سُجّل الصرف.')
  } else if (kind === 'return') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><HolderSelect label="من (حامل العهدة) *" /><WarehouseSelect label="إلى المستودع *" />{qtyF}{note}</>
    onSubmit = act.submit(f => post('/api/custody/stock/return', { ...body(f), ...parseHolder(f.get('holder')) }), 'أُرجعت الكمية للمستودع.')
  } else if (kind === 'consume') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><HolderSelect label="من عهدة *" /><VehicleSelect onChange={setVehicleId} />{qtyF}<Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><WoOrReason vehicleId={vehicleId} /></>
    onSubmit = act.submit(f => post('/api/custody/stock/consume', { catalogId: str(f.get('catalogId')), qty: num(f.get('qty')), ...parseHolder(f.get('holder')), vehicleId: str(f.get('vehicleId')), workOrderId: str(f.get('workOrderId')), reason: str(f.get('reason')), km: num(f.get('km')) }), 'سُجّل الاستهلاك.')
  } else if (kind === 'transfer') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><HolderSelect name="from" label="من (حامل العهدة) *" /><HolderSelect name="to" label="إلى *" />{qtyF}<Field label="سلّمها"><input name="handoverBy" maxLength={100} /></Field>{note}</>
    onSubmit = act.submit(f => { const a = parseHolder(f.get('from')), b = parseHolder(f.get('to')); return post('/api/custody/holders/transfer', { catalogId: str(f.get('catalogId')), qty: num(f.get('qty')), fromKind: a.holderKind, fromId: a.holderId, toKind: b.holderKind, toId: b.holderId, handoverBy: str(f.get('handoverBy')), note: str(f.get('note')) }) }, 'سُجّل التسليم بانتظار قبول المستلم.')
  } else if (kind === 'move') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><WarehouseSelect name="fromWarehouseId" label="من المستودع *" /><WarehouseSelect name="toWarehouseId" label="إلى المستودع *" />{qtyF}{note}</>
    onSubmit = act.submit(f => post('/api/custody/stock/move', { catalogId: str(f.get('catalogId')), fromWarehouseId: str(f.get('fromWarehouseId')), toWarehouseId: str(f.get('toWarehouseId')), qty: num(f.get('qty')), note: str(f.get('note')) }), 'نُقلت الكمية.')
  } else if (kind === 'adjust') {
    content = <><CatalogSelect tracking="Quantity" label="الصنف *" /><WarehouseSelect label="المستودع *" /><Field label="الرصيد الصحيح *"><input name="newQty" type="number" min={0} step="0.01" required /></Field><Field label="سبب التسوية *"><input name="reason" required minLength={3} maxLength={300} /></Field></>
    onSubmit = act.submit(f => post('/api/custody/stock/adjust', { catalogId: str(f.get('catalogId')), warehouseId: str(f.get('warehouseId')), newQty: num(f.get('newQty')), reason: str(f.get('reason')) }), 'سُجّلت التسوية.')
  } else if (kind === 'count') return <CountForm />
  return (
    <form className="filter" onSubmit={onSubmit}>
      {act.banner}
      <div className="fg">{content}</div>
      <div className="actions"><button className="btn pri" disabled={act.busy}>حفظ</button></div>
    </form>
  )
}

function CountForm() {
  const act = useAction()
  const cat = useCatalog()
  const [whId, setWhId] = useState('')
  const stock = useQuery({ queryKey: ['custody', 'stock', whId], queryFn: () => api<StockView>(`/api/custody/stock?warehouseId=${whId}`), enabled: !!whId })
  const items = (cat.data ?? []).filter(c => c.tracking === 'Quantity' && c.isActive)
  const [result, setResult] = useState<{ no: string; lines: { name: string; recorded: number; actual: number }[]; missingParts: string[]; unexpectedParts: string[] } | null>(null)
  function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const lines = items.filter(c => str(f.get('c-' + c.id)) != null).map(c => ({ catalogId: c.id, actual: num(f.get('c-' + c.id)), bin: str(f.get('b-' + c.id)) }))
    const found = String(f.get('found') ?? '').split(/[\s,،]+/).map(s => s.trim()).filter(Boolean)
    act.run(async () => setResult(await post('/api/custody/counts', { warehouseId: whId, lines, foundParts: found, note: str(f.get('note')) })), 'سُجّل الجرد وطُبّقت التسويات.')
  }
  return (
    <form className="filter" onSubmit={save}>
      <h3>جرد مستودع: أدخل الكمية الفعلية لكل صنف، وامسح أرقام القطع الموجودة</h3>
      {act.banner}
      <div className="fg"><Field label="المستودع *"><WhPlain value={whId} onChange={setWhId} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></div>
      {whId && <div className="tblw" style={{ marginTop: 10 }}><table>
        <thead><tr><th>الصنف</th><th>المسجل</th><th>الفعلي (اتركه فارغًا إن لم يُعد)</th><th>موقع التخزين</th></tr></thead>
        <tbody>{items.map(c => { const b = stock.data?.balances.find(x => x.catalogId === c.id); return (
          <tr key={c.id}><td>{c.code} · {c.name}</td><td className="num">{money(b?.qty ?? 0)} {c.unit}</td><td><input name={'c-' + c.id} type="number" min={0} step="0.01" style={{ maxWidth: 140 }} /></td><td><input name={'b-' + c.id} defaultValue={b?.bin} maxLength={40} style={{ maxWidth: 160 }} /></td></tr>) })}</tbody>
      </table></div>}
      {whId && <Field label="أرقام القطع الفردية الموجودة فعليًا (امسحها واحدة تلو الأخرى، كل رقم في سطر)"><textarea name="found" rows={4} dir="ltr" /></Field>}
      <div className="actions"><button className="btn pri" disabled={act.busy || !whId}>تسجيل الجرد</button></div>
      {result && <div className="card" style={{ padding: 12 }}><b>{result.no}</b>: {result.lines.filter(l => l.actual !== l.recorded).map(l => `${l.name}: ${l.recorded} → ${l.actual}`).join('، ') || 'لا فروقات في الكميات'}
        {result.missingParts.length > 0 && <div className="diff">قطع غير موجودة: {result.missingParts.join('، ')}</div>}
        {result.unexpectedParts.length > 0 && <div>أرقام غير متوقعة في هذا المستودع: {result.unexpectedParts.join('، ')}</div>}</div>}
    </form>
  )
}

function WhPlain({ value, onChange }: { value: string; onChange: (v: string) => void }) {
  const wh = useWarehouses()
  return <select value={value} onChange={e => onChange(e.target.value)} required><option value="">اختر</option>{wh.data?.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}</select>
}

// ── movements: issue, install, replace, transfer, remove; hand-overs ────────────────────────────────────────────
function MovesTab({ canEdit }: { canEdit: boolean }) {
  const [form, setForm] = useState('')
  const [status, setStatus] = useState('Pending')
  const transfers = useTransfers(status || undefined)
  const forms: [string, string][] = [['issue', 'صرف عهدة'], ['install', 'تركيب على شاحنة'], ['replace', 'استبدال'], ['transfer', 'نقل عهدة / بين الشاحنات'], ['remove', 'فك من شاحنة'], ['return', 'إرجاع للمستودع'], ['clearance', 'إخلاء عهدة']]
  return (
    <>
      {canEdit && <div className="filter row">{forms.map(([k, l]) => <button key={k} className={`btn ${form === k ? 'pri' : ''}`} onClick={() => setForm(k)}>{l}</button>)}</div>}
      {canEdit && form && <MoveForm key={form} kind={form} />}
      <Panel title="التسليمات (تسليم ← قبول المستلم)" actions={<select value={status} onChange={e => setStatus(e.target.value)} style={{ width: 'auto' }}><option value="Pending">بانتظار القبول</option><option value="Accepted">مقبولة</option><option value="Rejected">مرفوضة</option><option value="">الكل</option></select>}>
        <TransfersTable rows={transfers.data ?? []} canDecide={canEdit} />
      </Panel>
    </>
  )
}

export function TransfersTable({ rows, canDecide }: { rows: Transfer[]; canDecide: boolean }) {
  const act = useAction()
  return (
    <>
      {act.banner}
      <div className="tblw"><table>
        <thead><tr><th>الرقم</th><th>الحالة</th><th>النوع</th><th>الصنف</th><th>من</th><th>إلى</th><th>سلّمها</th><th>التاريخ</th></tr></thead>
        <tbody>{rows.map(t => (
          <tr key={t.id}><td className="num"><b>{t.no}</b></td>
            <td><span className={`chip ${t.status === 'Pending' ? 'st-AwaitingApproval' : t.status === 'Accepted' ? 'st-Approved' : 'st-UnderReview'}`}>{t.statusLabel}</span>{t.decidedBy && <div className="sub">{t.decidedBy} · {ksa(t.decidedAt)}</div>}{t.decisionNote && <div className="sub">{t.decisionNote}</div>}
              {canDecide && t.status === 'Pending' && <div className="row noprint" style={{ marginTop: 6 }}>
                <button className="btn ok" disabled={act.busy} onClick={() => act.run(() => post(`/api/custody/transfers/${t.id}/accept`, { note: 'قبول نيابة عن المستلم بعد توقيعه' }), `قُبل التسليم ${t.no}.`)}>قبول</button>
                <button className="btn" disabled={act.busy} onClick={() => { const r = prompt('سبب الرفض'); if (r) act.run(() => post(`/api/custody/transfers/${t.id}/reject`, { reason: r }), `رُفض التسليم ${t.no}.`) }}>رفض</button></div>}</td>
            <td>{t.kindLabel}</td><td>{t.item}{t.partNo ? <div className="sub num">{t.partNo}</div> : <div className="sub num">× {t.qty}</div>}</td>
            <td style={{ whiteSpace: 'normal' }}>{t.fromType}: {t.fromName}</td><td style={{ whiteSpace: 'normal' }}>{t.toType}: {t.toName}</td><td>{t.handoverBy ?? t.requestedBy}</td><td className="num">{ksa(t.requestedAt)}</td>
          </tr>))}</tbody>
      </table></div>
      {rows.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد تسليمات.</div>}
    </>
  )
}

function MoveForm({ kind }: { kind: string }) {
  const act = useSubmit()
  const all = useParts('take=2000')
  const meta = useMeta()
  const [vehicleId, setVehicleId] = useState('')
  const [oldPart, setOldPart] = useState<Part | undefined>()
  const [picked, setPicked] = useState<Part | undefined>()
  const [toKind, setToKind] = useState('driver')
  const parts = all.data ?? []
  const free = parts.filter(p => (p.status === 'Stock' || p.status === 'Issued') && !p.pendingTransfer)
  const s = (f: FormData, k: string) => str(f.get(k))
  let content: ReactNode
  let onSubmit = act.submit(async () => undefined, '')
  if (kind === 'issue') {
    content = <><PartPicker parts={parts.filter(p => p.status === 'Stock' && !p.pendingTransfer)} name="partId" label="القطعة (من المستودع) *" /><HolderSelect />
      <WoOrReason /><Field label="حالة القطعة"><input name="condition" maxLength={40} placeholder="جديد" /></Field><Field label="سلّمها"><input name="handoverBy" maxLength={100} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field>
      <label className="fl" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}><input type="checkbox" name="receiverSigned" style={{ width: 'auto' }} /> وقّع المستلم عند الاستلام (يُعتمد فورًا)</label></>
    onSubmit = act.submit(f => post(`/api/custody/parts/${s(f, 'partId')}/issue`, { ...parseHolder(f.get('holder')), workOrderId: s(f, 'workOrderId'), reason: s(f, 'reason'), condition: s(f, 'condition'), handoverBy: s(f, 'handoverBy'), note: s(f, 'note'), receiverSigned: f.get('receiverSigned') === 'on' }), 'سُجّل الصرف (بانتظار قبول المستلم ما لم يوقّع).')
  } else if (kind === 'install') {
    content = <><PartPicker parts={free} name="partId" label="القطعة *" onPick={setPicked} /><VehicleSelect onChange={setVehicleId} />
      <Field label={picked?.category === 'إطار' ? 'الموضع *' : 'الموضع'}><input name="position" list="tyre-pos" maxLength={60} required={picked?.category === 'إطار'} /><datalist id="tyre-pos">{meta.data?.tyrePositions.map(p => <option key={p} value={p} />)}</datalist></Field>
      <Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="ساعات التشغيل"><input name="hours" type="number" min={0} /></Field><WoOrReason vehicleId={vehicleId} /><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>
    onSubmit = act.submit(f => post(`/api/custody/parts/${s(f, 'partId')}/install`, { vehicleId: s(f, 'vehicleId'), position: s(f, 'position'), km: num(f.get('km')), hours: num(f.get('hours')), workOrderId: s(f, 'workOrderId'), reason: s(f, 'reason'), note: s(f, 'note') }), 'رُكّبت القطعة.')
  } else if (kind === 'replace') {
    const installed = parts.filter(p => p.status === 'Installed' && (!vehicleId || p.vehicleId === vehicleId))
    content = <><VehicleSelect onChange={v => { setVehicleId(v); setOldPart(undefined) }} />
      <Field label="القطعة القديمة (المركّبة) *"><select name="oldPartId" required defaultValue="" onChange={e => setOldPart(parts.find(p => p.id === e.target.value))}><option value="">اختر</option>{installed.map(p => <option key={p.id} value={p.id}>{p.position ? `${p.position} · ` : ''}{p.name} · {p.internalNo}{p.serial ? ` · ${p.serial}` : ''}</option>)}</select></Field>
      <PartPicker parts={free.filter(p => !oldPart || p.category === oldPart.category)} name="newPartId" label="القطعة الجديدة *" />
      <Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="ساعات التشغيل"><input name="hours" type="number" min={0} /></Field>
      <Field label="سبب الاستبدال *"><input name="reasonText" required minLength={3} maxLength={300} /></Field>
      <FateFields /><WoOrReason vehicleId={vehicleId} reasonName="issueReason" /><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>
    onSubmit = act.submit(f => post('/api/custody/replace', { oldPartId: s(f, 'oldPartId'), newPartId: s(f, 'newPartId'), km: num(f.get('km')), hours: num(f.get('hours')), reason: s(f, 'reasonText'), oldPartFate: fateOf(f), workOrderId: s(f, 'workOrderId'), issueReason: s(f, 'issueReason'), note: s(f, 'note') }), 'تم الاستبدال وسُجّل مصير القطعة القديمة.')
  } else if (kind === 'transfer') {
    content = <><PartPicker parts={parts.filter(p => ['Stock', 'Issued', 'Installed'].includes(p.status) && !p.pendingTransfer)} name="partId" label="القطعة *" />
      <Field label="إلى"><select value={toKind} onChange={e => setToKind(e.target.value)}><option value="driver">شخص (سائق/موظف)</option><option value="warehouse">مستودع</option><option value="vehicle">شاحنة أخرى (لقطعة مركّبة)</option></select></Field>
      {toKind === 'driver' && <HolderSelect label="المستلم *" />}{toKind === 'warehouse' && <WarehouseSelect name="toId" label="المستودع *" />}
      {toKind === 'vehicle' && <><VehicleSelect name="toId" label="الشاحنة المستلمة *" /><Field label="الموضع"><input name="position" maxLength={60} /></Field><Field label="عداد الشاحنة المسلِّمة (كم)"><input name="km" type="number" min={0} /></Field></>}
      <Field label="حالة القطعة"><input name="condition" maxLength={40} /></Field><Field label="المسلِّم"><input name="handoverBy" maxLength={100} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>
    onSubmit = act.submit(f => { const h = parseHolder(f.get('holder')); return post(`/api/custody/parts/${s(f, 'partId')}/transfer`, { toKind: toKind === 'driver' ? h.holderKind : toKind, toId: toKind === 'driver' ? h.holderId : s(f, 'toId'), position: s(f, 'position'), km: num(f.get('km')), condition: s(f, 'condition'), handoverBy: s(f, 'handoverBy'), note: s(f, 'note') }) }, 'سُجّل التسليم بانتظار قبول المستلم.')
  } else if (kind === 'remove') {
    content = <><PartPicker parts={parts.filter(p => p.status === 'Installed')} name="partId" label="القطعة المركّبة *" /><Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="ساعات التشغيل"><input name="hours" type="number" min={0} /></Field>
      <Field label="سبب الفك *"><input name="reasonText" required minLength={3} maxLength={300} /></Field><FateFields /><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>
    onSubmit = act.submit(f => post(`/api/custody/parts/${s(f, 'partId')}/remove`, { km: num(f.get('km')), hours: num(f.get('hours')), reason: s(f, 'reasonText'), fate: fateOf(f), note: s(f, 'note') }), 'فُكّت القطعة وسُجّل مصيرها.')
  } else if (kind === 'return') {
    content = <><PartPicker parts={parts.filter(p => p.status === 'Issued' && !p.pendingTransfer)} name="partId" label="القطعة (من عهدة شخص) *" /><WarehouseSelect label="إلى المستودع *" /><Field label="موقع التخزين"><input name="bin" maxLength={40} /></Field><Field label="حالتها عند الإرجاع"><input name="condition" maxLength={40} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>
    onSubmit = act.submit(f => post(`/api/custody/parts/${s(f, 'partId')}/return`, { warehouseId: s(f, 'warehouseId'), bin: s(f, 'bin'), condition: s(f, 'condition'), note: s(f, 'note') }), 'أُرجعت القطعة للمستودع.')
  } else {
    content = <><HolderSelect label="الموظف أو السائق *" /><WarehouseSelect label="إرجاع الكل إلى المستودع *" /><Field label="سبب الإخلاء"><input name="note" maxLength={500} placeholder="نقل / انتهاء خدمة" /></Field></>
    onSubmit = act.submit(f => post('/api/custody/clearance', { ...parseHolder(f.get('holder')), warehouseId: s(f, 'warehouseId'), note: s(f, 'note') }), 'تم إخلاء العهدة. راجع تقرير «إخلاء العهدة» لطباعة البيان.')
  }
  return (
    <form className="filter" onSubmit={onSubmit}>
      {act.banner}
      <div className="fg">{content}</div>
      <div className="actions"><button className="btn pri" disabled={act.busy}>حفظ</button></div>
    </form>
  )
}

// ── work orders ─────────────────────────────────────────────────────────────────────────────────────────────────
function WorkOrdersTab({ canEdit, canSeeCost }: { canEdit: boolean; canSeeCost: boolean }) {
  const [open, setOpen] = useState<string>('true')
  const wos = useWorkOrders(open === '' ? undefined : open === 'true')
  const meta = useMeta()
  const act = useSubmit()
  const [id, setId] = useState<string | null>(null)
  if (id) return <WorkOrderView id={id} canEdit={canEdit} onBack={() => setId(null)} />
  return (
    <>
      {canEdit && <form className="filter" onSubmit={act.submit(f => post('/api/custody/workorders', { vehicleId: str(f.get('vehicleId')), type: str(f.get('type')), description: str(f.get('description')), workshop: str(f.get('workshop')), km: num(f.get('km')), laborCost: num(f.get('laborCost')) }), 'فُتح أمر الصيانة.')}>
        <h3>فتح أمر صيانة</h3>{act.banner}
        <div className="fg"><VehicleSelect /><Field label="النوع *"><select name="type" required>{meta.data?.workOrderTypes.map(t => <option key={t}>{t}</option>)}</select></Field>
          <Field label="الوصف *"><input name="description" required minLength={3} maxLength={1000} /></Field><Field label="الورشة"><input name="workshop" maxLength={100} /></Field>
          <Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="الأجور"><input name="laborCost" type="number" min={0} step="0.01" /></Field></div>
        <div className="actions"><button className="btn pri" disabled={act.busy}>فتح الأمر</button></div>
      </form>}
      <Panel title="أوامر الصيانة" actions={<select value={open} onChange={e => setOpen(e.target.value)} style={{ width: 'auto' }}><option value="true">المفتوحة</option><option value="false">المغلقة</option><option value="">الكل</option></select>}>
        <div className="tblw"><table>
          <thead><tr><th>الرقم</th><th>الشاحنة</th><th>النوع</th><th>الوصف</th><th>السائق</th><th>فُتح</th><th>عمليات</th>{canSeeCost && <><th>القطع</th><th>الأجور</th><th>الإجمالي</th></>}<th>الحالة</th></tr></thead>
          <tbody>{wos.data?.map(w => (
            <tr key={w.id} className="click" onClick={() => setId(w.id)}><td className="num"><b>{w.no}</b></td><td className="num">{w.plate}</td><td>{w.type}</td><td style={{ whiteSpace: 'normal', minWidth: 200 }}>{w.description}</td><td>{w.driver ?? '—'}</td>
              <td className="num">{ksa(w.openedAt)}</td><td className="num">{w.operations}</td>{canSeeCost && <><td className="num">{money(w.partsCost)}</td><td className="num">{money(w.laborCost)}</td><td className="num"><b>{money(w.total)}</b></td></>}
              <td><span className={`chip ${w.closed ? 'st-Approved' : 'st-AwaitingApproval'}`}>{w.closed ? 'مغلق' : 'مفتوح'}</span></td></tr>))}</tbody>
        </table></div>
      </Panel>
    </>
  )
}

interface WoDetail { id: string; no: string; plate: string; vehicleId: string; type: string; description: string; workshop?: string; km?: number; closed: boolean; openedAt: string; openedBy: string; closedAt?: string; closedBy?: string; closeNote?: string; laborCost?: number; partsCost?: number; total?: number; movements: Movement[]; attachments: { id: string; kind: string; fileName: string }[] }

function WorkOrderView({ id, canEdit, onBack }: { id: string; canEdit: boolean; onBack: () => void }) {
  const q = useQuery({ queryKey: ['custody', 'workorder', id], queryFn: () => api<WoDetail>(`/api/custody/workorders/${id}`) })
  const act = useSubmit()
  const w = q.data
  if (!w) return <div className="sub">جارٍ التحميل…</div>
  return (
    <>
      <div className="row noprint"><button className="btn" onClick={onBack}>← أوامر الصيانة</button><button className="btn" onClick={() => window.print()}>طباعة</button></div>
      <section className="hero" style={{ marginTop: 14 }}>
        <div><div>أمر صيانة</div><div className="big" style={{ fontSize: 26 }}>{w.no}</div><div><Link to={`/custody/truck/${w.vehicleId}`} style={{ color: '#fff' }}>{w.plate}</Link> · {w.type}</div><div>{w.description}</div></div>
        <div className="tiles">
          <div className="tile"><div>الحالة</div><div className="v" style={{ fontSize: 18 }}>{w.closed ? 'مغلق' : 'مفتوح'}</div><div className="num">{ksa(w.openedAt)} · {w.openedBy}</div></div>
          <div className="tile"><div>التكلفة (قطع + أجور)</div><div className="v">{money(w.total)}</div><div className="num">قطع {money(w.partsCost)} · أجور {money(w.laborCost)}</div></div>
        </div>
      </section>
      <Panel title="القطع والمواد المصروفة على الأمر">
        <MovesTable rows={w.movements} />
      </Panel>
      {canEdit && !w.closed && (
        <form className="filter noprint" onSubmit={act.submit(f => post(`/api/custody/workorders/${id}/close`, { laborCost: num(f.get('laborCost')), note: str(f.get('note')) }), 'أُغلق أمر الصيانة.')}>
          {act.banner}
          <div className="fg"><Field label="الأجور النهائية"><input name="laborCost" type="number" min={0} step="0.01" defaultValue={w.laborCost} /></Field><Field label="ملاحظة الإغلاق"><input name="note" maxLength={500} /></Field></div>
          <div className="actions"><button className="btn ok" disabled={act.busy}>إغلاق الأمر</button></div>
        </form>)}
      <Attachments ownerType="workorder" ownerId={id} files={w.attachments} canEdit={canEdit} />
    </>
  )
}

export function MovesTable({ rows, showPart = true }: { rows: Movement[]; showPart?: boolean }) {
  return (
    <div className="tblw"><table>
      <thead><tr><th>التاريخ</th><th>الحركة</th>{showPart && <th>القطعة / الصنف</th>}<th>من</th><th>إلى</th><th>أمر الصيانة</th><th>السبب / المصير</th><th>كم</th><th>ساعات</th><th>التكلفة</th><th>بواسطة</th></tr></thead>
      <tbody>{rows.map(m => (
        <tr key={m.id}><td className="num">{ksa(m.at)}</td><td><b>{m.label}</b>{m.relatedNo && <div className="sub num">↔ {m.relatedNo}</div>}</td>
          {showPart && <td>{m.partId ? <Link to={`/custody/part/${m.partId}`} className="num">{m.partNo ?? 'القطعة'}</Link> : null} {m.item}{!m.partId && <span className="num"> × {m.qty}</span>}</td>}
          <td>{m.from || '—'}</td><td>{m.to || '—'}</td><td className="num">{m.workOrder || '—'}</td>
          <td style={{ whiteSpace: 'normal', minWidth: 160 }}>{[m.reason, m.fate && `المصير: ${m.fate}`, m.docRef, m.note].filter(Boolean).join(' · ')}</td>
          <td className="num">{m.km ?? ''}</td><td className="num">{m.hours ?? ''}</td><td className="num">{m.cost != null ? money(m.cost) : ''}</td><td>{m.by}{m.approvedBy && m.approvedBy !== m.by && <div className="sub">اعتماد: {m.approvedBy}</div>}</td></tr>))}</tbody>
    </table></div>
  )
}

// ── setup: warehouses, employees, catalog ───────────────────────────────────────────────────────────────────────
function SetupTab({ canEdit, canSeeCost }: { canEdit: boolean; canSeeCost: boolean }) {
  const wh = useWarehouses()
  const emps = useEmployees()
  const cat = useCatalog()
  const holders = useHolders()
  const meta = useMeta()
  const act = useSubmit()
  const [edit, setEdit] = useState<CatalogItem | null>(null)
  function saveCat(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const body = { code: str(f.get('code')), name: str(f.get('name')), tracking: str(f.get('tracking')), category: str(f.get('category')), brand: str(f.get('brand')), model: str(f.get('model')), mfrPartNo: str(f.get('mfrPartNo')),
      unit: str(f.get('unit')), minQty: num(f.get('minQty')), reorderQty: num(f.get('reorderQty')), inspectEveryDays: num(f.get('inspectEveryDays')), unitCost: num(f.get('unitCost')), specs: str(f.get('specs')), isActive: f.get('isActive') !== 'off' }
    act.run(() => edit ? api(`/api/custody/catalog/${edit.id}`, { method: 'PUT', body: JSON.stringify(body) }) : post('/api/custody/catalog', body), edit ? 'حُفظ الصنف.' : 'أُضيف الصنف.')
  }
  return (
    <>
      {act.banner}
      <Panel title="الأصناف (دليل قطع الغيار والمواد)">
        {canEdit && <form className="filter" style={{ boxShadow: 'none' }} onSubmit={saveCat} key={edit?.id ?? 'new'}>
          <div className="fg">
            <Field label="رمز الصنف *"><input name="code" required maxLength={40} defaultValue={edit?.code} dir="ltr" /></Field>
            <Field label="الاسم *"><input name="name" required minLength={2} maxLength={150} defaultValue={edit?.name} /></Field>
            <Field label="طريقة التتبع *"><select name="tracking" defaultValue={edit?.tracking ?? 'Individual'}><option value="Individual">فردي (لكل قطعة رقم)</option><option value="Quantity">بالكمية</option></select></Field>
            <Field label="الفئة *"><select name="category" defaultValue={edit?.category}>{meta.data?.categories.map(c => <option key={c}>{c}</option>)}</select></Field>
            <Field label="الماركة"><input name="brand" maxLength={60} defaultValue={edit?.brand} /></Field>
            <Field label="الموديل"><input name="model" maxLength={60} defaultValue={edit?.model} /></Field>
            <Field label="رقم القطعة لدى المصنع"><input name="mfrPartNo" maxLength={60} defaultValue={edit?.mfrPartNo} dir="ltr" /></Field>
            <Field label="الوحدة"><input name="unit" maxLength={20} defaultValue={edit?.unit} /></Field>
            <Field label="الحد الأدنى"><input name="minQty" type="number" min={0} defaultValue={edit?.minQty} /></Field>
            <Field label="كمية إعادة الطلب"><input name="reorderQty" type="number" min={0} defaultValue={edit?.reorderQty} /></Field>
            <Field label="الفحص الدوري كل (يوم)"><input name="inspectEveryDays" type="number" min={1} defaultValue={edit?.inspectEveryDays} /></Field>
            {canSeeCost && <Field label="سعر الوحدة"><input name="unitCost" type="number" min={0} step="0.01" defaultValue={edit?.unitCost} /></Field>}
            <Field label="المواصفات"><input name="specs" maxLength={300} defaultValue={edit?.specs} /></Field>
          </div>
          <div className="actions">{edit && <button type="button" className="btn" onClick={() => setEdit(null)}>إلغاء التعديل</button>}<button className="btn pri" disabled={act.busy}>{edit ? 'حفظ الصنف' : 'إضافة صنف'}</button></div>
        </form>}
        <div className="tblw"><table>
          <thead><tr><th>الرمز</th><th>الاسم</th><th>الفئة</th><th>التتبع</th><th>الماركة</th><th>المتوفر</th><th>الحد الأدنى</th><th>إعادة الطلب</th><th>فحص كل</th>{canSeeCost && <th>سعر الوحدة</th>}</tr></thead>
          <tbody>{cat.data?.map(c => (
            <tr key={c.id} className={`${canEdit ? 'click' : ''} ${c.belowMin ? 'flag' : ''}`} onClick={() => canEdit && setEdit(c)}><td className="num">{c.code}</td><td>{c.name}</td><td>{c.category}</td><td>{c.tracking === 'Individual' ? 'فردي' : 'كمية'}</td>
              <td>{c.brand} {c.model}</td><td className="num">{money(c.inStock)} {c.unit}</td><td className="num">{money(c.minQty)}</td><td className="num">{money(c.reorderQty)}</td><td className="num">{c.inspectEveryDays ? `${c.inspectEveryDays} يوم` : ''}</td>{canSeeCost && <td className="num">{money(c.unitCost)}</td>}</tr>))}</tbody>
        </table></div>
      </Panel>
      <div className="charts">
        <Panel title="المستودعات">
          {canEdit && <form className="filter" style={{ boxShadow: 'none' }} onSubmit={act.submit(f => post('/api/custody/warehouses', { name: str(f.get('name')), location: str(f.get('location')), keeper: str(f.get('keeper')) }), 'أُضيف المستودع.')}>
            <div className="fg"><Field label="الاسم *"><input name="name" required minLength={2} maxLength={100} /></Field><Field label="الموقع"><input name="location" maxLength={150} /></Field><Field label="أمين المستودع"><input name="keeper" maxLength={100} /></Field></div>
            <div className="actions"><button className="btn pri">إضافة</button></div></form>}
          <div className="tblw"><table><thead><tr><th>الاسم</th><th>الموقع</th><th>الأمين</th></tr></thead><tbody>{wh.data?.map(w => <tr key={w.id}><td>{w.name}</td><td>{w.location}</td><td>{w.keeper}</td></tr>)}</tbody></table></div>
        </Panel>
        <Panel title="الموظفون والفنيون (حاملو العهد غير السائقين)">
          {canEdit && <form className="filter" style={{ boxShadow: 'none' }} onSubmit={act.submit(f => post('/api/custody/employees', { name: str(f.get('name')), role: str(f.get('role')), empNo: str(f.get('empNo')), phone: str(f.get('phone')) }), 'أُضيف الموظف.')}>
            <div className="fg"><Field label="الاسم *"><input name="name" required minLength={2} maxLength={100} /></Field><Field label="الوظيفة"><input name="role" maxLength={60} placeholder="فني إطارات" /></Field><Field label="الرقم الوظيفي"><input name="empNo" maxLength={30} /></Field><Field label="الجوال"><input name="phone" maxLength={20} /></Field></div>
            <div className="actions"><button className="btn pri">إضافة</button></div></form>}
          <div className="tblw"><table><thead><tr><th>الاسم</th><th>الوظيفة</th><th>الرقم</th><th>قطع بعهدته</th></tr></thead>
            <tbody>{emps.data?.map(e => <tr key={e.id}><td>{e.name}</td><td>{e.role}</td><td className="num">{e.empNo}</td><td className="num">{holders.data?.find(h => h.id === e.id)?.parts ?? 0}</td></tr>)}</tbody></table></div>
        </Panel>
      </div>
      <div className="sub">الأيام بتوقيت السعودية. آخر تحديث: {day(new Date().toISOString())}</div>
    </>
  )
}
