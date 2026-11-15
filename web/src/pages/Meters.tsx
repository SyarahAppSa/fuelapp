import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, can, fmt, ksa, post } from '../api'
import { useMe } from '../App'
import { Field, Panel, formData, useAction, useMeters, useVehicles } from '../ui'

interface Ledger { last?: number; baselinePending: boolean; readings: { seq: number; reading: number; kind: string; at: string; tripId?: string }[]; resets: { at: string; type: string; reason: string }[] }

/** Cumulative meters: append-only ledger, baseline approval, documented replacement/zeroing (supervisor only, enforced by the server). */
export default function Meters() {
  const { data: me } = useMe()
  const edit = can(me, 'Meters', 'Edit')
  const act = useAction()
  const meters = useMeters(), vehicles = useVehicles()
  const [sel, setSel] = useState<string | null>(null)
  const ledger = useQuery({ queryKey: ['ledger', sel], queryFn: () => api<Ledger>(`/api/meters/${sel}/ledger`), enabled: !!sel })

  function reset(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const body = formData(form, ['newBaseline'])
    act.run(async () => { await post(`/api/meters/${sel}/reset`, body); form.reset() }, 'سُجّل الاستبدال/التصفير مع الاحتفاظ بالسجل السابق.')
  }

  return (
    <>
      <h1>العدادات</h1>
      {act.banner}
      <Panel title="العدادات">
        <div className="tblw"><table><thead><tr><th>الرقم التسلسلي</th><th>المركبة</th><th></th></tr></thead>
          <tbody>{meters.data?.map(m => (
            <tr key={m.id} className={sel === m.id ? 'flag' : ''}><td className="num"><b>{m.serial}</b><div className="sub">{m.kind}</div></td>
              <td className="num">{vehicles.data?.find(v => v.meterId === m.id)?.plate ?? '—'}</td>
              <td><button className="btn" onClick={() => setSel(m.id)}>السجل</button></td></tr>))}</tbody></table></div>
      </Panel>

      {sel && ledger.data && (
        <>
          <div className="kpis">
            <div className="kpi"><div className="l">آخر قراءة معتمدة</div><div className="v">{fmt(ledger.data.last)}</div></div>
            <div className={`kpi ${ledger.data.baselinePending ? 'attn' : ''}`}><div className="l">القراءة التأسيسية</div>
              <div className="v" style={{ fontSize: 18 }}>{ledger.data.baselinePending ? 'بانتظار الاعتماد' : 'معتمدة'}</div>
              {ledger.data.baselinePending && edit && <button className="btn ok" style={{ marginTop: 8 }} disabled={act.busy}
                onClick={() => act.run(() => post(`/api/meters/${sel}/approve-baseline`), 'اعتُمدت القراءة التأسيسية.')}>اعتماد</button>}</div>
          </div>
          {edit && <Panel title="استبدال أو تصفير العداد (للمشرف)">
            <form className="filter" onSubmit={reset} style={{ boxShadow: 'none' }}><div className="fg">
              <Field label="النوع *"><select name="type" required><option value="استبدال">استبدال</option><option value="تصفير">تصفير</option></select></Field>
              <Field label="القراءة التأسيسية الجديدة *"><input name="newBaseline" type="number" min={0} required /></Field>
              <Field label="الرقم التسلسلي الجديد (عند الاستبدال)"><input name="newSerial" maxLength={50} /></Field>
              <Field label="السبب *"><input name="reason" required maxLength={500} /></Field>
            </div><div className="actions"><button className="btn pri" disabled={act.busy}>تسجيل</button></div></form>
          </Panel>}
          <Panel title="سجل القراءات (للإضافة فقط)">
            <div className="tblw"><table><thead><tr><th>#</th><th>القراءة</th><th>النوع</th><th>الوقت</th></tr></thead>
              <tbody>{ledger.data.readings.map(r => <tr key={r.seq}><td className="num">{r.seq}</td><td className="num"><b>{fmt(r.reading)}</b></td><td>{r.kind}</td><td className="num">{ksa(r.at)}</td></tr>)}</tbody></table></div>
          </Panel>
        </>)}
    </>
  )
}
