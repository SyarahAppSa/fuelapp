// Form pieces shared by the custody pages: pickers that accept a scanned barcode, and the rule-bearing groups
// (work order or approved reason, the old part's fate). The server enforces every rule; these only guide.
import { useId, useState } from 'react'
import { Field } from '../ui'
import { holderValue, useCatalog, useCVehicles, useHolders, useMeta, useWarehouses, useWorkOrders, type Part } from '../custody'

/** Type or scan an internal number / serial; the matching part's id goes into the hidden field `name`. */
export function PartPicker({ parts, name, label, onPick }: { parts: Part[]; name: string; label: string; onPick?: (p: Part | undefined) => void }) {
  const id = useId()
  const [text, setText] = useState('')
  const key = text.trim().toUpperCase().replace(/\s+/g, '')
  const found = key ? parts.find(p => p.internalNo === key || p.serial === key) : undefined
  return (
    <Field label={label}>
      <input list={id} value={text} placeholder="امسح الباركود أو اكتب رقم القطعة" autoComplete="off" required
        onChange={e => { setText(e.target.value); const k = e.target.value.trim().toUpperCase().replace(/\s+/g, ''); onPick?.(parts.find(p => p.internalNo === k || p.serial === k)) }} />
      <datalist id={id}>{parts.slice(0, 400).map(p => <option key={p.id} value={p.internalNo}>{`${p.name} · ${p.serial ?? 'بدون تسلسلي'} · ${p.location}`}</option>)}</datalist>
      <input type="hidden" name={name} value={found?.id ?? ''} />
      <span className={found ? 'sub' : 'sub diff'}>{found ? `${found.name} · ${found.serial ?? '—'} · ${found.statusLabel} · ${found.location}` : key ? 'لا توجد قطعة متاحة بهذا الرقم' : ''}</span>
    </Field>
  )
}

export function HolderSelect({ name = 'holder', label = 'المستلم (سائق أو موظف)', required = true }: { name?: string; label?: string; required?: boolean }) {
  const holders = useHolders()
  return (
    <Field label={label}>
      <select name={name} required={required} defaultValue="">
        <option value="">اختر</option>
        <optgroup label="السائقون">{holders.data?.filter(h => h.kind === 'driver').map(h => <option key={h.id} value={holderValue(h)}>{h.name}{h.empNo ? ` · ${h.empNo}` : ''}</option>)}</optgroup>
        <optgroup label="الموظفون والفنيون">{holders.data?.filter(h => h.kind === 'employee').map(h => <option key={h.id} value={holderValue(h)}>{h.name} · {h.role}</option>)}</optgroup>
      </select>
    </Field>
  )
}

export function WarehouseSelect({ name = 'warehouseId', label = 'المستودع', required = true, blank }: { name?: string; label?: string; required?: boolean; blank?: string }) {
  const wh = useWarehouses()
  return (
    <Field label={label}>
      <select name={name} required={required} defaultValue="">
        <option value="">{blank ?? 'اختر'}</option>
        {wh.data?.filter(w => w.isActive).map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
      </select>
    </Field>
  )
}

export function VehicleSelect({ name = 'vehicleId', label = 'الشاحنة', required = true, blank, onChange }: { name?: string; label?: string; required?: boolean; blank?: string; onChange?: (id: string) => void }) {
  const v = useCVehicles()
  return (
    <Field label={label}>
      <select name={name} required={required} defaultValue="" onChange={e => onChange?.(e.target.value)}>
        <option value="">{blank ?? 'اختر'}</option>
        {v.data?.map(x => <option key={x.id} value={x.id}>{x.plate}{x.internalNo ? ` · ${x.internalNo}` : ''}{x.driver ? ` · ${x.driver}` : ''}</option>)}
      </select>
    </Field>
  )
}

export function CatalogSelect({ name = 'catalogId', label = 'الصنف', tracking, onChange }: { name?: string; label?: string; tracking?: 'Individual' | 'Quantity'; onChange?: (id: string) => void }) {
  const c = useCatalog()
  return (
    <Field label={label}>
      <select name={name} required defaultValue="" onChange={e => onChange?.(e.target.value)}>
        <option value="">اختر</option>
        {c.data?.filter(x => x.isActive && (!tracking || x.tracking === tracking)).map(x => <option key={x.id} value={x.id}>{x.code} · {x.name}{x.tracking === 'Quantity' ? ` (${x.inStock} ${x.unit ?? ''})` : ''}</option>)}
      </select>
    </Field>
  )
}

/** Issuing needs an open work order or an approved reason (one of the two). */
export function WoOrReason({ vehicleId, reasonName = 'reason' }: { vehicleId?: string; reasonName?: string }) {
  const wos = useWorkOrders(true)
  const meta = useMeta()
  return (
    <>
      <Field label="أمر الصيانة (مفتوح)">
        <select name="workOrderId" defaultValue="">
          <option value="">بدون أمر صيانة</option>
          {wos.data?.filter(w => !vehicleId || w.vehicleId === vehicleId).map(w => <option key={w.id} value={w.id}>{w.no} · {w.plate} · {w.description.slice(0, 40)}</option>)}
        </select>
      </Field>
      <Field label="أو سبب صرف معتمد">
        <select name={reasonName} defaultValue=""><option value="">—</option>{meta.data?.issueReasons.map(r => <option key={r}>{r}</option>)}</select>
      </Field>
    </>
  )
}

/** The old / removed part's fate is mandatory: back to stock, repair, warranty claim or approved scrap. */
export function FateFields() {
  const meta = useMeta()
  const [fate, setFate] = useState('')
  return (
    <>
      <Field label="مصير القطعة القديمة *">
        <select name="fate" required value={fate} onChange={e => setFate(e.target.value)}>
          <option value="">اختر المصير</option>
          {Object.entries(meta.data?.fates ?? {}).map(([k, v]) => <option key={k} value={k}>{v}</option>)}
        </select>
      </Field>
      {fate === 'stock' && <><WarehouseSelect name="fateWarehouseId" label="إلى المستودع *" /><Field label="موقع التخزين"><input name="fateBin" maxLength={40} /></Field></>}
      {fate === 'repair' && <>
        <Field label="ورشة الإصلاح *"><input name="vendor" required maxLength={100} /></Field>
        <Field label="العودة المتوقعة"><input name="expectedReturn" type="date" /></Field>
        <Field label="التكلفة التقديرية"><input name="estimatedCost" type="number" min={0} step="0.01" /></Field>
      </>}
      {fate === 'claim' && <Field label="رقم مطالبة الضمان *"><input name="claimRef" required maxLength={60} /></Field>}
      {fate === 'scrap' && <Field label="رقم / ملاحظة اعتماد الإتلاف *"><input name="scrapApproval" required maxLength={300} placeholder="مثال: اعتماد مدير الصيانة رقم 55" /></Field>}
    </>
  )
}
