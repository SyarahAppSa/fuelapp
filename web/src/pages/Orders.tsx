// «طلبات العملاء»: list with filters and the order form. The order is the hub: opening one shows its trucks, quantities and documents.
import { useState, type FormEvent } from 'react'
import { useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, can, fmt, post } from '../api'
import { useMe } from '../App'
import { Field, Panel, formData, useAction, useCustomers } from '../ui'
import { dateOnly, fromLocalInput, orderChip, orderStatuses, priorityLabels, sar, toLocalInput, typeLabels, type OrderRow, type OrderType } from '../orders'

export default function Orders() {
  const { data: me } = useMe()
  const nav = useNavigate()
  const [filters, setFilters] = useState<Record<string, string>>({})
  const [page, setPage] = useState(1)
  const [creating, setCreating] = useState(false)
  const qs = new URLSearchParams({ ...Object.fromEntries(Object.entries(filters).filter(([, v]) => v)), page: String(page) }).toString()
  const list = useQuery({ queryKey: ['orders', qs], queryFn: () => api<{ total: number; size: number; items: OrderRow[] }>(`/api/orders?${qs}`) })
  const pages = list.data ? Math.max(1, Math.ceil(list.data.total / list.data.size)) : 1
  const money = can(me, 'Costs')

  function search(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setFilters({ q: String(f.get('q') ?? ''), status: String(f.get('status') ?? ''), type: String(f.get('type') ?? ''), priority: String(f.get('priority') ?? ''), from: String(f.get('from') ?? ''), to: String(f.get('to') ?? ''), late: f.get('late') ? 'true' : '' })
    setPage(1)
  }

  return (
    <>
      <div className="spread"><h1>طلبات العملاء</h1>
        {can(me, 'Ops', 'Edit') && <button className="btn pri" onClick={() => setCreating(!creating)}>{creating ? 'إغلاق' : 'طلب عميل جديد'}</button>}</div>
      {creating && <OrderForm onDone={(id) => { setCreating(false); nav(`/orders/${id}`) }} />}
      <form className="filter" onSubmit={search}>
        <div className="fg">
          <Field label="بحث: رقم الطلب، العميل، رقم أمر الشراء"><input name="q" maxLength={60} /></Field>
          <Field label="الحالة"><select name="status"><option value="">الكل</option>{orderStatuses.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
          <Field label="النوع"><select name="type"><option value="">الكل</option>{Object.entries(typeLabels).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
          <Field label="الأولوية"><select name="priority"><option value="">الكل</option>{Object.entries(priorityLabels).map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
          <Field label="من تاريخ"><input name="from" type="date" /></Field>
          <Field label="إلى تاريخ"><input name="to" type="date" /></Field>
          <label className="fl" style={{ flexDirection: 'row', gap: 8, alignItems: 'center', marginTop: 18 }}><input type="checkbox" name="late" style={{ width: 'auto' }} /> المتأخرة فقط</label>
        </div>
        <div className="actions"><button className="btn pri">بحث</button></div>
      </form>
      <Panel title={`الطلبات (${list.data?.total ?? 0})`}>
        <div className="tblw"><table>
          <thead><tr><th>رقم الطلب</th><th>العميل / الموقع</th><th>النوع</th><th>الأولوية</th><th>المطلوب</th><th>المسلّم</th><th>المتبقي</th><th>الرحلات</th><th>موعد التسليم</th>{money && <th>الأجرة / السعر</th>}<th>الحالة</th></tr></thead>
          <tbody>{list.data?.items.map(o => (
            <tr key={o.id} className={`click ${o.totals.late ? 'flag' : ''}`} onClick={() => nav(`/orders/${o.id}`)}>
              <td className="num"><b>{o.no}</b>{o.customerRef && <div className="sub">{o.customerRef}</div>}</td>
              <td>{o.customerName}<div className="sub">{o.site}</div></td>
              <td>{o.typeLabel}</td>
              <td><span className={`chip ${o.priority === 'Urgent' ? 'st-UnderReview' : 'st-WaitingLoad'}`}>{o.priorityLabel}</span></td>
              <td className="num">{fmt(o.qtyRequested)}</td><td className="num">{fmt(o.totals.delivered)}</td><td className="num">{fmt(o.totals.remaining)}</td>
              <td className="num">{o.totals.trips}</td>
              <td className="num">{dateOnly(o.requestedTo)}{o.totals.late && <div className="diff">متأخر</div>}</td>
              {money && <td className="num">{sar(o.money?.transportRatePerLiter)}{o.money?.fuelPricePerLiter != null && <div className="sub">وقود {sar(o.money.fuelPricePerLiter)}</div>}</td>}
              <td><span className={`chip ${orderChip[o.status]}`}>{o.statusLabel}</span></td>
            </tr>))}</tbody>
        </table></div>
        {list.data?.items.length === 0 && <div className="sub" style={{ padding: 16 }}>لا توجد طلبات مطابقة.</div>}
        {pages > 1 && <div className="pager">{Array.from({ length: pages }, (_, i) => i + 1).map(p => <button key={p} className={p === page ? 'on' : ''} onClick={() => setPage(p)}>{p}</button>)}</div>}
      </Panel>
    </>
  )
}

/** Create, or edit with a reason (when `order` is given). Rate fields follow the order type. */
export function OrderForm({ order, onDone }: { order?: OrderRow; onDone: (id: string) => void }) {
  const act = useAction()
  const customers = useCustomers()
  const [type, setType] = useState<OrderType>(order?.type ?? 'Transport')
  function submit(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const b = formData(e.currentTarget, ['qtyRequested', 'transportRatePerLiter', 'fuelPricePerLiter'])
    const body = { ...b, requestedFrom: fromLocalInput(b.requestedFrom), requestedTo: fromLocalInput(b.requestedTo) }
    act.run(async () => {
      const r = order ? await api<{ order: OrderRow }>(`/api/orders/${order.id}`, { method: 'PUT', body: JSON.stringify(body) }) : await post<{ order: OrderRow }>('/api/orders', body)
      onDone(r.order.id)
    }, order ? 'حُفظ التعديل.' : 'أُنشئ الطلب.')
  }
  return (
    <form className="filter" onSubmit={submit}>
      {act.banner}
      <div className="fg">
        <Field label="العميل (موقع التسليم) *"><select name="customerId" required defaultValue={order?.customerId ?? ''}><option value="">اختر</option>{customers.data?.map(c => <option key={c.id} value={c.id}>{c.name} — {c.site}</option>)}</select></Field>
        <Field label="نوع الطلب *"><select name="type" value={type} onChange={e => setType(e.target.value as OrderType)}><option value="Transport">نقل فقط</option><option value="TransportAndSupply">نقل وتوريد</option></select></Field>
        <Field label="المنتج *"><input name="product" required defaultValue={order?.product ?? 'ديزل'} maxLength={40} /></Field>
        <Field label="الكمية المطلوبة (لتر) *"><input name="qtyRequested" type="number" min={1} max={10000000} required defaultValue={order?.qtyRequested} /></Field>
        <Field label="الأولوية *"><select name="priority" defaultValue={order?.priority ?? 'Scheduled'}><option value="Scheduled">مجدول</option><option value="Urgent">عاجل</option></select></Field>
        <Field label="التسليم من"><input name="requestedFrom" type="datetime-local" defaultValue={toLocalInput(order?.requestedFrom)} /></Field>
        <Field label="التسليم حتى"><input name="requestedTo" type="datetime-local" defaultValue={toLocalInput(order?.requestedTo)} /></Field>
        <Field label="رقم أمر الشراء لدى العميل"><input name="customerRef" maxLength={60} defaultValue={order?.customerRef} /></Field>
        <Field label="أجرة النقل (ريال/لتر)"><input name="transportRatePerLiter" type="number" step="0.001" min={0} max={100} defaultValue={order?.money?.transportRatePerLiter} /></Field>
        {type === 'TransportAndSupply' && <Field label="سعر بيع الوقود (ريال/لتر)"><input name="fuelPricePerLiter" type="number" step="0.001" min={0} max={100} defaultValue={order?.money?.fuelPricePerLiter} /></Field>}
        <Field label="ملاحظات"><input name="notes" maxLength={1000} defaultValue={order?.notes} /></Field>
        {order && <Field label="سبب التعديل *"><input name="reason" required minLength={3} maxLength={500} /></Field>}
      </div>
      <div className="actions"><button className="btn pri" disabled={act.busy}>{order ? 'حفظ التعديل' : 'إنشاء الطلب'}</button></div>
    </form>
  )
}
