import { useState, type FormEvent } from 'react'
import { can, fmt, post } from '../api'
import { useMe } from '../App'
import { Field, Panel, driverTypes, formData, stationTypes, useAction, useCustomers, useDrivers, useMeters, useStations, useVehicles } from '../ui'

type Tab = 'stations' | 'vehicles' | 'drivers' | 'customers' | 'meters'
const tabs: [Tab, string][] = [['stations', 'مواقع التعبئة'], ['vehicles', 'المركبات'], ['drivers', 'السائقون'], ['customers', 'العملاء والوجهات'], ['meters', 'العدادات']]

export default function MasterData() {
  const { data: me } = useMe()
  const [tab, setTab] = useState<Tab>('stations')
  const edit = can(me, 'Master', 'Edit')
  const act = useAction()
  const stations = useStations(), vehicles = useVehicles(), drivers = useDrivers(), customers = useCustomers(), meters = useMeters()

  function add(e: FormEvent<HTMLFormElement>, path: string, numeric: string[] = []) {
    e.preventDefault()
    const form = e.currentTarget
    const body = formData(form, numeric)
    act.run(async () => { await post(`/api/master/${path}`, body); form.reset() }, 'تمت الإضافة.')
  }

  return (
    <>
      <div className="spread"><h1>البيانات الأساسية</h1>
        <div className="row">{tabs.map(([k, l]) => <button key={k} className={`btn ${tab === k ? 'pri' : ''}`} onClick={() => { setTab(k); act.clear() }}>{l}</button>)}</div></div>
      {act.banner}

      {tab === 'stations' && <>
        {edit && <form className="filter" onSubmit={e => add(e, 'stations')}><div className="fg">
          <Field label="الاسم *"><input name="name" required maxLength={100} /></Field>
          <Field label="النوع *"><select name="type" required>{Object.entries(stationTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></Field>
          <Field label="المدينة"><input name="city" maxLength={60} /></Field>
        </div><div className="actions"><button className="btn pri" disabled={act.busy}>إضافة موقع</button></div></form>}
        <Panel title="مواقع التعبئة"><div className="tblw"><table><thead><tr><th>الاسم</th><th>النوع</th><th>المدينة</th></tr></thead>
          <tbody>{stations.data?.map(s => <tr key={s.id}><td>{s.name}</td><td>{stationTypes[s.type]}</td><td>{s.city ?? '—'}</td></tr>)}</tbody></table></div></Panel>
      </>}

      {tab === 'vehicles' && <>
        {edit && <form className="filter" onSubmit={e => add(e, 'vehicles', ['capacity', 'year'])}><div className="fg">
          <Field label="اللوحة *"><input name="plate" required maxLength={20} /></Field>
          <Field label="الرقم الداخلي"><input name="internalNo" maxLength={20} /></Field>
          <Field label="الشركة المصنعة"><input name="make" maxLength={40} /></Field>
          <Field label="الطراز"><input name="model" maxLength={40} /></Field>
          <Field label="سنة الصنع"><input name="year" type="number" min={1980} max={2100} /></Field>
          <Field label="السعة (لتر) *"><input name="capacity" type="number" min={0} max={100000} required /></Field>
          <Field label="عداد التعبئة"><select name="meterId"><option value="">— بدون —</option>{meters.data?.map(m => <option key={m.id} value={m.id}>{m.serial}</option>)}</select></Field>
        </div><div className="actions"><button className="btn pri" disabled={act.busy}>إضافة مركبة</button></div></form>}
        <Panel title="المركبات"><div className="tblw"><table><thead><tr><th>اللوحة</th><th>الرقم الداخلي</th><th>المركبة</th><th>السعة</th><th>العداد</th></tr></thead>
          <tbody>{vehicles.data?.map(v => <tr key={v.id}><td className="num"><b>{v.plate}</b></td><td>{v.internalNo ?? '—'}</td><td>{[v.make, v.model, v.year].filter(Boolean).join(' ')}</td>
            <td className="num">{fmt(v.capacity)}</td><td className="num">{meters.data?.find(m => m.id === v.meterId)?.serial ?? '—'}</td></tr>)}</tbody></table></div></Panel>
      </>}

      {tab === 'drivers' && <>
        {edit && <form className="filter" onSubmit={e => add(e, 'drivers')}><div className="fg">
          <Field label="الاسم *"><input name="name" required maxLength={100} /></Field>
          <Field label="الرقم الوظيفي"><input name="empNo" maxLength={30} /></Field>
          <Field label="الجوال"><input name="phone" inputMode="tel" pattern="\+?[0-9 ]{7,20}" maxLength={20} /></Field>
          <Field label="النوع *"><select name="type" required>{Object.entries(driverTypes).map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></Field>
        </div><div className="actions"><button className="btn pri" disabled={act.busy}>إضافة سائق</button></div></form>}
        <Panel title="السائقون"><div className="tblw"><table><thead><tr><th>الاسم</th><th>الرقم الوظيفي</th><th>الجوال</th><th>النوع</th></tr></thead>
          <tbody>{drivers.data?.map(d => <tr key={d.id}><td>{d.name}</td><td className="num">{d.empNo ?? '—'}</td><td className="num">{d.phone ?? '—'}</td><td>{driverTypes[d.type]}</td></tr>)}</tbody></table></div></Panel>
      </>}

      {tab === 'customers' && <>
        {edit && <form className="filter" onSubmit={e => add(e, 'customers')}><div className="fg">
          <Field label="العميل *"><input name="name" required maxLength={150} /></Field>
          <Field label="المشروع"><input name="project" maxLength={150} /></Field>
          <Field label="موقع التسليم *"><input name="site" required maxLength={200} /></Field>
          <Field label="المستلم"><input name="recipient" maxLength={100} /></Field>
          <Field label="جوال المستلم"><input name="recipientPhone" inputMode="tel" pattern="\+?[0-9 ]{7,20}" maxLength={20} /></Field>
        </div><div className="actions"><button className="btn pri" disabled={act.busy}>إضافة عميل</button></div></form>}
        <Panel title="العملاء والوجهات"><div className="tblw"><table><thead><tr><th>العميل</th><th>المشروع</th><th>موقع التسليم</th></tr></thead>
          <tbody>{customers.data?.map(c => <tr key={c.id}><td>{c.name}</td><td>{c.project ?? '—'}</td><td>{c.site}</td></tr>)}</tbody></table></div></Panel>
      </>}

      {tab === 'meters' && <>
        {edit && <form className="filter" onSubmit={e => add(e, 'meters')}><div className="fg">
          <Field label="الرقم التسلسلي *"><input name="serial" required maxLength={50} /></Field>
          <Field label="النوع"><input name="kind" maxLength={80} /></Field>
        </div><div className="actions"><button className="btn pri" disabled={act.busy}>إضافة عداد</button></div></form>}
        <Panel title="العدادات"><div className="tblw"><table><thead><tr><th>الرقم التسلسلي</th><th>النوع</th><th>المركبة</th></tr></thead>
          <tbody>{meters.data?.map(m => <tr key={m.id}><td className="num"><b>{m.serial}</b></td><td>{m.kind ?? '—'}</td>
            <td className="num">{vehicles.data?.find(v => v.meterId === m.id)?.plate ?? '—'}</td></tr>)}</tbody></table></div></Panel>
      </>}
    </>
  )
}
