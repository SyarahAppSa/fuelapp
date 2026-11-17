// One part: its label (QR), where it is and who holds it, the actions allowed in its current state, and its full history.
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, ksa, post } from '../api'
import { Field, Panel, useAction } from '../ui'
import { day, fateOf, money, num, parseHolder, qrDataUrl, str, useMeta, useParts, type Movement, type Part, type PartStatus, type Transfer } from '../custody'
import { FateFields, HolderSelect, PartPicker, VehicleSelect, WarehouseSelect, WoOrReason } from './CustodyForms'
import { MovesTable, StatusChip, TransfersTable } from './Custody'
import Attachments from './CustodyAttachments'

interface Detail {
  part: Part; movements: Movement[]; transfers: Transfer[]; attachments: { id: string; kind: string; fileName: string }[]
  meter?: { id: string; serial: string; kind?: string; lastCalibration?: string; nextCalibration?: string }; meterResets: { at: string; type: string; reason: string; newBaseline: number }[]
  life: { plate: string; position?: string; from: string; to?: string; days: number; km?: number; hours?: number; endReason?: string }[]; totalKm: number; totalDays: number
}

const actionsFor: Record<PartStatus, [string, string][]> = {
  Stock: [['issue', 'صرف عهدة لشخص'], ['install', 'تركيب على شاحنة'], ['transfer', 'نقل لمستودع آخر'], ['repair', 'إرسال للإصلاح'], ['claim', 'مطالبة ضمان'], ['damaged', 'تسجيل تالف'], ['lost', 'تسجيل مفقود'], ['scrap', 'إتلاف معتمد'], ['inspect', 'فحص دوري']],
  Issued: [['return', 'إرجاع للمستودع'], ['transfer', 'نقل العهدة لشخص آخر'], ['install', 'تركيب على شاحنة'], ['repair', 'إرسال للإصلاح'], ['claim', 'مطالبة ضمان'], ['damaged', 'تسجيل تالف'], ['lost', 'تسجيل مفقود'], ['inspect', 'فحص دوري']],
  Installed: [['replace', 'استبدال بقطعة أخرى'], ['remove', 'فك (مع تحديد المصير)'], ['transfer', 'نقل لشاحنة أخرى'], ['damaged', 'تسجيل تالف'], ['lost', 'تسجيل مفقود'], ['inspect', 'فحص دوري']],
  Repair: [['repair-return', 'عودة من الإصلاح'], ['lost', 'تسجيل مفقود']],
  ReturnedToSupplier: [['claim-result', 'نتيجة مطالبة الضمان'], ['scrap', 'إتلاف معتمد']],
  Damaged: [['review', 'نتيجة المراجعة'], ['repair', 'إرسال للإصلاح'], ['claim', 'مطالبة ضمان'], ['scrap', 'إتلاف معتمد'], ['lost', 'تسجيل مفقود']],
  Lost: [['review', 'نتيجة المراجعة'], ['correct', 'وُجدت: إعادة للمستودع (تصحيح)']],
  Scrapped: [['review', 'نتيجة المراجعة']],
}

export default function PartView() {
  const { id = '' } = useParams()
  const nav = useNavigate()
  const meta = useMeta()
  const q = useQuery({ queryKey: ['custody', 'part', id], queryFn: () => api<Detail>(`/api/custody/parts/${id}`) })
  const d = q.data
  if (q.isError) return <div className="err">{(q.error as Error).message}</div>
  if (!d) return <div className="sub">جارٍ التحميل…</div>
  const p = d.part
  const canEdit = !!meta.data?.canEdit
  const facts: [string, ReactNode][] = [
    ['الصنف', `${p.code} · ${p.name}`], ['الفئة', p.category], ['الرقم التسلسلي', <span className="num">{p.serial ?? '—'}</span>], ['الماركة / الموديل', `${p.brand ?? ''} ${p.model ?? ''}`], ['رقم المصنع', p.mfrPartNo ?? '—'],
    ['المورد / الفاتورة', `${p.supplier ?? '—'} · ${p.invoiceNo ?? '—'}`], ['تاريخ الشراء', day(p.purchaseDate)], ...(meta.data?.canSeeCost ? [['التكلفة', money(p.cost)] as [string, ReactNode]] : []),
    ['الضمان', `${p.warranty}${p.warrantyEnd ? ` · ${day(p.warrantyStart)} ← ${day(p.warrantyEnd)}` : ''}`], ['شروط الضمان', p.warrantyTerms ?? '—'], ['رقم الدفعة', p.batchNo ?? '—'], ['المواصفات', p.specs ?? '—'],
    ['الحالة الفنية', p.condition ?? '—'], ['آخر فحص / القادم', `${day(p.lastInspection)} / ${day(p.nextInspection)}`], ['تاريخ الانتهاء', day(p.expiryDate)],
    ['الشاحنة', p.vehicleId ? <Link to={`/custody/truck/${p.vehicleId}`}>{p.plate}{p.position ? ` · ${p.position}` : ''}</Link> : '—'], ['التركيب', p.installedAt ? `${ksa(p.installedAt)} · ${p.installKm ?? '—'} كم${p.installHours != null ? ` · ${p.installHours} ساعة` : ''}` : '—'],
    ['حامل العهدة', p.holder ? `${p.holder} (${p.holderType}) منذ ${ksa(p.holderSince)}` : '—'], ['المستودع', p.warehouse ? `${p.warehouse}${p.bin ? ` · ${p.bin}` : ''}` : '—'],
    ['الإصلاح', p.repairVendor ? `${p.repairVendor} · أُرسلت ${ksa(p.repairSentAt)} · متوقعة ${day(p.repairExpectedAt)}${p.repairCost != null ? ` · ${money(p.repairCost)}` : ''}` : '—'],
    ['مطالبة الضمان', p.claimRef ? `${p.claimRef} · ${ksa(p.claimOpenedAt)}${p.claimResult ? ` · ${p.claimResult}` : ''}` : '—'], ['آخر موقع (تالف/مفقود)', p.lastLocation ?? '—'], ['نتيجة المراجعة', p.reviewOutcome ?? '—'],
    ['سبب الحالة', p.statusReason ?? '—'], ['ملاحظات', p.notes ?? '—'],
  ]
  return (
    <>
      <div className="row noprint"><button className="btn" onClick={() => nav(-1)}>← رجوع</button><Link className="btn" to="/custody?tab=parts">القطع</Link>
        <button className="btn" onClick={() => nav(`/custody/labels?ids=${p.id}`)}>طباعة الملصق</button><button className="btn" onClick={() => window.print()}>طباعة السجل</button></div>
      <section className="hero" style={{ marginTop: 14 }}>
        <div className="row" style={{ alignItems: 'flex-start' }}>
          <img className="qr" src={qrDataUrl(p.internalNo)} alt={`QR ${p.internalNo}`} style={{ margin: 0 }} />
          <div><div>{p.name}</div><div className="big" style={{ fontSize: 28 }}>{p.internalNo}</div><div className="num">{p.serial ?? 'بدون رقم مصنع'}</div><div style={{ marginTop: 6 }}><StatusChip s={p.status} label={p.statusLabel} /></div></div>
        </div>
        <div className="tiles">
          <div className="tile"><div>أين هي الآن</div><div className="v" style={{ fontSize: 17 }}>{p.location || '—'}</div>{p.pendingTransfer && <div>تسليم معلّق {p.pendingTransfer}</div>}</div>
          <div className="tile"><div>الاستخدام على الشاحنات</div><div className="v">{d.totalKm.toLocaleString('en-US')} كم</div><div>{d.totalDays} يومًا في {d.life.length} فترة تركيب</div></div>
        </div>
      </section>
      {canEdit && <PartActions p={p} />}
      <Panel title="بيانات القطعة"><div className="facts">{facts.map(([k, v]) => <div key={k}><span className="sub">{k}</span><div>{v}</div></div>)}</div></Panel>
      {d.meter && <Panel title="عداد الوقود المرتبط"><div className="facts">
        <div><span className="sub">الرقم التسلسلي</span><div className="num">{d.meter.serial}</div></div><div><span className="sub">النوع</span><div>{d.meter.kind ?? '—'}</div></div>
        <div><span className="sub">آخر معايرة</span><div className="num">{day(d.meter.lastCalibration)}</div></div><div><span className="sub">المعايرة القادمة</span><div className="num">{day(d.meter.nextCalibration)}</div></div>
        <div><span className="sub">التصفير/الاستبدال</span><div>{d.meterResets.map(r => `${ksa(r.at)} ${r.type}: ${r.reason}`).join(' · ') || '—'}</div></div></div></Panel>}
      {d.life.length > 0 && <Panel title="فترات التركيب (العمر والكيلومترات)"><div className="tblw"><table>
        <thead><tr><th>الشاحنة</th><th>الموضع</th><th>من</th><th>إلى</th><th>الأيام</th><th>كم</th><th>ساعات</th><th>سبب الفك</th></tr></thead>
        <tbody>{d.life.map((l, i) => <tr key={i}><td className="num">{l.plate}</td><td>{l.position}</td><td className="num">{ksa(l.from)}</td><td className="num">{l.to ? ksa(l.to) : 'ما زالت مركّبة'}</td><td className="num">{l.days}</td><td className="num">{l.km ?? ''}</td><td className="num">{l.hours ?? ''}</td><td>{l.endReason}</td></tr>)}</tbody></table></div></Panel>}
      <Panel title="سجل الحركات (لا يُحذف؛ التصحيح حركة جديدة)"><MovesTable rows={d.movements} showPart={false} /></Panel>
      {d.transfers.length > 0 && <Panel title="التسليمات"><TransfersTable rows={d.transfers} canDecide={canEdit} /></Panel>}
      <Attachments ownerType="part" ownerId={p.id} files={d.attachments} canEdit={canEdit} />
      {canEdit && <EditPart p={p} />}
    </>
  )
}

function PartActions({ p }: { p: Part }) {
  const opts = actionsFor[p.status]
  const [action, setAction] = useState(opts[0]?.[0] ?? '')
  const [vehicleId, setVehicleId] = useState(p.vehicleId ?? '')
  const [toKind, setToKind] = useState(p.status === 'Installed' ? 'vehicle' : p.status === 'Stock' ? 'warehouse' : 'driver')
  const [fixed, setFixed] = useState('yes')
  const [accepted, setAccepted] = useState('yes')
  const meta = useMeta()
  const all = useParts('take=2000')
  const act = useAction()
  const base = `/api/custody/parts/${p.id}`
  const s = (f: FormData, k: string) => str(f.get(k))
  if (p.pendingTransfer) return <div className="card" style={{ padding: 12, marginTop: 12 }}>القطعة في تسليم معلّق ({p.pendingTransfer}). أكمل التسليم أو ارفضه من قائمة التسليمات قبل أي إجراء آخر.</div>

  const forms: Record<string, [ReactNode, (f: FormData) => Promise<unknown>, string]> = {
    issue: [<><HolderSelect /><WoOrReason /><Field label="سلّمها"><input name="handoverBy" maxLength={100} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field>
      <label className="fl" style={{ flexDirection: 'row', alignItems: 'center', gap: 8 }}><input type="checkbox" name="receiverSigned" style={{ width: 'auto' }} /> وقّع المستلم عند الاستلام</label></>,
      f => post(`${base}/issue`, { ...parseHolder(f.get('holder')), workOrderId: s(f, 'workOrderId'), reason: s(f, 'reason'), handoverBy: s(f, 'handoverBy'), note: s(f, 'note'), receiverSigned: f.get('receiverSigned') === 'on' }), 'سُجّل الصرف.'],
    install: [<><VehicleSelect onChange={setVehicleId} /><Field label={p.category === 'إطار' ? 'الموضع *' : 'الموضع'}><input name="position" list="pv-pos" maxLength={60} required={p.category === 'إطار'} /><datalist id="pv-pos">{meta.data?.tyrePositions.map(x => <option key={x} value={x} />)}</datalist></Field>
      <Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="ساعات التشغيل"><input name="hours" type="number" min={0} /></Field><WoOrReason vehicleId={vehicleId} /></>,
      f => post(`${base}/install`, { vehicleId: s(f, 'vehicleId'), position: s(f, 'position'), km: num(f.get('km')), hours: num(f.get('hours')), workOrderId: s(f, 'workOrderId'), reason: s(f, 'reason') }), 'رُكّبت القطعة.'],
    replace: [<><PartPicker parts={(all.data ?? []).filter(x => (x.status === 'Stock' || x.status === 'Issued') && !x.pendingTransfer && x.category === p.category)} name="newPartId" label="القطعة الجديدة *" />
      <Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="ساعات التشغيل"><input name="hours" type="number" min={0} /></Field><Field label="سبب الاستبدال *"><input name="reasonText" required minLength={3} maxLength={300} /></Field>
      <FateFields /><WoOrReason vehicleId={p.vehicleId} reasonName="issueReason" /></>,
      f => post('/api/custody/replace', { oldPartId: p.id, newPartId: s(f, 'newPartId'), km: num(f.get('km')), hours: num(f.get('hours')), reason: s(f, 'reasonText'), oldPartFate: fateOf(f), workOrderId: s(f, 'workOrderId'), issueReason: s(f, 'issueReason') }), 'تم الاستبدال.'],
    remove: [<><Field label="عداد الشاحنة (كم)"><input name="km" type="number" min={0} /></Field><Field label="ساعات التشغيل"><input name="hours" type="number" min={0} /></Field><Field label="سبب الفك *"><input name="reasonText" required minLength={3} maxLength={300} /></Field><FateFields /></>,
      f => post(`${base}/remove`, { km: num(f.get('km')), hours: num(f.get('hours')), reason: s(f, 'reasonText'), fate: fateOf(f) }), 'فُكّت القطعة.'],
    transfer: [<>
      <Field label="إلى"><select value={toKind} onChange={e => setToKind(e.target.value)}>
        {p.status === 'Installed' ? <option value="vehicle">شاحنة أخرى</option> : <><option value="driver">شخص (سائق/موظف)</option><option value="warehouse">مستودع</option></>}</select></Field>
      {toKind === 'driver' && <HolderSelect label="المستلم *" />}{toKind === 'warehouse' && <WarehouseSelect name="toId" label="المستودع *" />}
      {toKind === 'vehicle' && <><VehicleSelect name="toId" label="الشاحنة المستلمة *" /><Field label="الموضع"><input name="position" maxLength={60} /></Field><Field label="عداد الشاحنة الحالية (كم)"><input name="km" type="number" min={0} /></Field></>}
      <Field label="حالة القطعة"><input name="condition" maxLength={40} /></Field><Field label="المسلِّم"><input name="handoverBy" maxLength={100} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>,
      f => { const h = parseHolder(f.get('holder')); return post(`${base}/transfer`, { toKind: toKind === 'driver' ? h.holderKind : toKind, toId: toKind === 'driver' ? h.holderId : s(f, 'toId'), position: s(f, 'position'), km: num(f.get('km')), condition: s(f, 'condition'), handoverBy: s(f, 'handoverBy'), note: s(f, 'note') }) }, 'سُجّل التسليم بانتظار قبول المستلم.'],
    return: [<><WarehouseSelect /><Field label="موقع التخزين"><input name="bin" maxLength={40} /></Field><Field label="الحالة عند الإرجاع"><input name="condition" maxLength={40} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>,
      f => post(`${base}/return`, { warehouseId: s(f, 'warehouseId'), bin: s(f, 'bin'), condition: s(f, 'condition'), note: s(f, 'note') }), 'أُرجعت للمستودع.'],
    repair: [<><Field label="الورشة *"><input name="vendor" required maxLength={100} /></Field><Field label="العودة المتوقعة"><input name="expectedReturn" type="date" /></Field><Field label="التكلفة التقديرية"><input name="estimatedCost" type="number" min={0} step="0.01" /></Field>
      <Field label="السبب / العطل *"><input name="reasonText" required minLength={3} maxLength={300} /></Field><Field label="رقم سند الإرسال"><input name="docRef" maxLength={60} /></Field><WoOrReason vehicleId={p.vehicleId} /></>,
      f => post(`${base}/repair-send`, { vendor: s(f, 'vendor'), expectedReturn: s(f, 'expectedReturn'), estimatedCost: num(f.get('estimatedCost')), reason: s(f, 'reasonText'), workOrderId: s(f, 'workOrderId'), docRef: s(f, 'docRef') }), 'أُرسلت للإصلاح.'],
    'repair-return': [<><Field label="النتيجة"><select value={fixed} onChange={e => setFixed(e.target.value)}><option value="yes">أُصلحت</option><option value="no">تعذّر إصلاحها (تالفة)</option></select></Field>
      {fixed === 'yes' && <><WarehouseSelect label="إلى المستودع *" /><Field label="موقع التخزين"><input name="bin" maxLength={40} /></Field></>}
      <Field label="التكلفة الفعلية"><input name="cost" type="number" min={0} step="0.01" /></Field><Field label="رقم الفاتورة/السند"><input name="docRef" maxLength={60} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>,
      f => post(`${base}/repair-return`, { fixed: fixed === 'yes', warehouseId: s(f, 'warehouseId'), bin: s(f, 'bin'), cost: num(f.get('cost')), docRef: s(f, 'docRef'), note: s(f, 'note') }), 'سُجّلت العودة من الإصلاح.'],
    claim: [<><Field label="رقم المطالبة *"><input name="claimRef" required maxLength={60} /></Field><Field label="السبب *"><input name="reasonText" required minLength={3} maxLength={300} /></Field></>,
      f => post(`${base}/claim`, { claimRef: s(f, 'claimRef'), reason: s(f, 'reasonText') }), 'رُفعت مطالبة الضمان.'],
    'claim-result': [<><Field label="النتيجة"><select value={accepted} onChange={e => setAccepted(e.target.value)}><option value="yes">مقبولة (استبدال/إصلاح)</option><option value="no">مرفوضة</option></select></Field>
      <Field label="التفاصيل *"><input name="result" required minLength={2} maxLength={300} /></Field>{accepted === 'yes' && <WarehouseSelect label="إلى المستودع *" />}</>,
      f => post(`${base}/claim-result`, { accepted: accepted === 'yes', result: s(f, 'result'), warehouseId: s(f, 'warehouseId') }), 'سُجّلت نتيجة المطالبة.'],
    damaged: [<><Field label="السبب *"><input name="reasonText" required minLength={3} maxLength={300} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>,
      f => post(`${base}/damaged`, { reason: s(f, 'reasonText'), note: s(f, 'note') }), 'سُجّلت القطعة تالفة.'],
    lost: [<><Field label="السبب / الظروف *"><input name="reasonText" required minLength={3} maxLength={300} /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>,
      f => post(`${base}/lost`, { reason: s(f, 'reasonText'), note: s(f, 'note') }), 'سُجّلت القطعة مفقودة.'],
    review: [<Field label="نتيجة المراجعة *"><input name="outcome" required minLength={3} maxLength={300} placeholder="مثال: تُحمّل على الموظف / استهلاك طبيعي / حادث" /></Field>,
      f => post(`${base}/review`, { outcome: s(f, 'outcome') }), 'سُجّلت نتيجة المراجعة.'],
    scrap: [<><Field label="سبب الإتلاف *"><input name="reasonText" required minLength={3} maxLength={300} /></Field><Field label="رقم/ملاحظة الاعتماد *"><input name="approval" required minLength={2} maxLength={300} /></Field></>,
      f => post(`${base}/scrap`, { reason: s(f, 'reasonText'), approval: s(f, 'approval') }), 'أُتلفت القطعة باعتماد.'],
    inspect: [<><Field label="النتيجة"><select name="passed" defaultValue="yes"><option value="yes">اجتازت الفحص</option><option value="no">لم تجتز</option></select></Field><Field label="الفحص القادم"><input name="next" type="date" /></Field><Field label="ملاحظة"><input name="note" maxLength={500} /></Field></>,
      f => post(`${base}/inspect`, { passed: f.get('passed') === 'yes', nextInspection: s(f, 'next'), note: s(f, 'note') }), 'سُجّل الفحص.'],
    correct: [<><WarehouseSelect label="إعادة إلى المستودع *" /><Field label="سبب التصحيح *"><input name="reasonText" required minLength={5} maxLength={300} /></Field></>,
      f => post(`${base}/correct`, { warehouseId: s(f, 'warehouseId'), reason: s(f, 'reasonText') }), 'سُجّل التصحيح كحركة جديدة.'],
  }
  const cur = forms[action]
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    act.run(() => cur[1](f), cur[2])
  }
  return (
    <form className="filter noprint" onSubmit={submit} key={action + p.status}>
      <div className="row" style={{ marginBottom: 10 }}><b>إجراء:</b>{opts.map(([k, l]) => <button type="button" key={k} className={`btn ${k === action ? 'pri' : ''}`} onClick={() => setAction(k)}>{l}</button>)}</div>
      {act.banner}
      {cur && <><div className="fg">{cur[0]}</div><div className="actions"><button className="btn pri" disabled={act.busy}>تنفيذ</button></div></>}
    </form>
  )
}

function EditPart({ p }: { p: Part }) {
  const act = useAction()
  const [open, setOpen] = useState(false)
  function save(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const g = (k: string) => str(f.get(k))
    const body = { catalogId: p.catalogId, serial: g('serial'), brand: g('brand'), model: g('model'), supplier: g('supplier'), invoiceNo: g('invoiceNo'), purchaseDate: g('purchaseDate'), cost: num(f.get('cost')) ?? p.cost,
      warrantyStart: g('warrantyStart'), warrantyEnd: g('warrantyEnd'), warrantyTerms: g('warrantyTerms'), batchNo: g('batchNo'), specs: g('specs'), expiryDate: g('expiryDate'), nextInspection: g('nextInspection'),
      condition: g('condition'), notes: g('notes'), meterId: p.meterId, bin: g('bin'), reason: g('reason') }
    act.run(() => api(`/api/custody/parts/${p.id}`, { method: 'PUT', body: JSON.stringify(body) }), 'حُفظ التعديل (القيم السابقة في سجل التعديلات).')
  }
  if (!open) return <div className="noprint" style={{ marginTop: 14 }}><button className="btn" onClick={() => setOpen(true)}>تعديل بيانات القطعة…</button></div>
  return (
    <form className="filter noprint" onSubmit={save}>
      <h3>تعديل بيانات القطعة (يحفظ سبب التعديل والقيم السابقة)</h3>
      {act.banner}
      <div className="fg">
        <Field label="الرقم التسلسلي"><input name="serial" defaultValue={p.serial} dir="ltr" maxLength={60} /></Field><Field label="الماركة"><input name="brand" defaultValue={p.brand} maxLength={60} /></Field>
        <Field label="الموديل"><input name="model" defaultValue={p.model} maxLength={60} /></Field><Field label="المورد"><input name="supplier" defaultValue={p.supplier} maxLength={100} /></Field>
        <Field label="الفاتورة"><input name="invoiceNo" defaultValue={p.invoiceNo} maxLength={60} /></Field><Field label="تاريخ الشراء"><input name="purchaseDate" type="date" defaultValue={p.purchaseDate} /></Field>
        <Field label="التكلفة"><input name="cost" type="number" min={0} step="0.01" defaultValue={p.cost} /></Field><Field label="بداية الضمان"><input name="warrantyStart" type="date" defaultValue={p.warrantyStart} /></Field>
        <Field label="نهاية الضمان"><input name="warrantyEnd" type="date" defaultValue={p.warrantyEnd} /></Field><Field label="شروط الضمان"><input name="warrantyTerms" defaultValue={p.warrantyTerms} maxLength={300} /></Field>
        <Field label="رقم الدفعة"><input name="batchNo" defaultValue={p.batchNo} maxLength={60} /></Field><Field label="المواصفات"><input name="specs" defaultValue={p.specs} maxLength={300} /></Field>
        <Field label="تاريخ الانتهاء"><input name="expiryDate" type="date" defaultValue={p.expiryDate} /></Field><Field label="الفحص القادم"><input name="nextInspection" type="date" defaultValue={p.nextInspection} /></Field>
        <Field label="الحالة الفنية"><input name="condition" defaultValue={p.condition} maxLength={40} /></Field><Field label="موقع التخزين"><input name="bin" defaultValue={p.bin} maxLength={40} /></Field>
        <Field label="ملاحظات"><input name="notes" defaultValue={p.notes} maxLength={500} /></Field><Field label="سبب التعديل *"><input name="reason" required minLength={3} maxLength={500} /></Field>
      </div>
      <div className="actions"><button type="button" className="btn" onClick={() => setOpen(false)}>إلغاء</button><button className="btn pri" disabled={act.busy}>حفظ</button></div>
    </form>
  )
}
