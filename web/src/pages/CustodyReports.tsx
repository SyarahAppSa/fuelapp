// The 14 custody reports: pick a report, filter, view, export to Excel, or print (the browser's "Save as PDF").
import type { FormEvent } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api, download, ksa } from '../api'
import { Field } from '../ui'
import { holderValue, parseHolder, useCVehicles, useHolders, useMeta, useWarehouses, type Report } from '../custody'

interface ReportInfo { no: number; key: string; title: string; hint: string }

/** Which filters each report uses. */
const filters: Record<string, string[]> = {
  custody: ['holder', 'category'], installed: ['vehicle', 'category'], history: ['q'], issues: ['period', 'vehicle', 'holder', 'category'], replacements: ['period', 'vehicle', 'category'],
  transfers: ['period', 'vehicle', 'holder', 'category'], inventory: ['warehouse', 'category'], shortages: ['category'], damaged: ['period', 'category'], warranty: ['days', 'category'],
  life: ['vehicle', 'category'], costs: ['period', 'vehicle'], repair: ['period', 'category'], clearance: ['holder', 'period'],
}

export default function CustodyReports() {
  const [sp, setSp] = useSearchParams()
  const list = useQuery({ queryKey: ['custody', 'reports'], queryFn: () => api<ReportInfo[]>('/api/custody/reports') })
  const meta = useMeta()
  const vehicles = useCVehicles()
  const holders = useHolders()
  const wh = useWarehouses()
  const key = sp.get('r') ?? 'custody'
  const params = new URLSearchParams()
  for (const k of ['from', 'to', 'vehicleId', 'category', 'warehouseId', 'q', 'days']) { const v = sp.get(k); if (v) params.set(k, v) }
  const h = parseHolder(sp.get('holder'))
  if (h.holderId) { params.set('holderKind', h.holderKind!); params.set('holderId', h.holderId) }
  const qs = params.toString()
  const needsPart = key === 'history' && !sp.get('q')
  const rep = useQuery({ queryKey: ['custody', 'report', key, qs], queryFn: () => api<Report>(`/api/custody/reports/${key}?${qs}`), enabled: !needsPart, retry: false })
  const info = list.data?.find(r => r.key === key)
  const use = filters[key] ?? []

  function apply(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const next = new URLSearchParams({ tab: 'reports', r: key })
    for (const k of ['from', 'to', 'vehicleId', 'category', 'warehouseId', 'q', 'days', 'holder']) { const v = String(f.get(k) ?? '').trim(); if (v) next.set(k, v) }
    setSp(next)
  }
  return (
    <>
      <div className="reppick noprint">
        {list.data?.map(r => <button key={r.key} className={r.key === key ? 'on' : ''} onClick={() => setSp({ tab: 'reports', r: r.key })} title={r.hint}><span className="num">{r.no}</span>{r.title}</button>)}
      </div>
      <form className="filter noprint" onSubmit={apply} key={key + qs}>
        <div className="sub" style={{ marginBottom: 8 }}>{info?.hint}</div>
        <div className="fg">
          {use.includes('period') && <><Field label="من تاريخ"><input type="date" name="from" defaultValue={sp.get('from') ?? ''} /></Field><Field label="إلى تاريخ"><input type="date" name="to" defaultValue={sp.get('to') ?? ''} /></Field></>}
          {use.includes('vehicle') && <Field label="الشاحنة"><select name="vehicleId" defaultValue={sp.get('vehicleId') ?? ''}><option value="">الكل</option>{vehicles.data?.map(v => <option key={v.id} value={v.id}>{v.plate}</option>)}</select></Field>}
          {use.includes('holder') && <Field label="الموظف / السائق"><select name="holder" defaultValue={sp.get('holder') ?? ''}><option value="">الكل</option>{holders.data?.map(x => <option key={x.id} value={holderValue(x)}>{x.name} · {x.role}</option>)}</select></Field>}
          {use.includes('category') && <Field label="الفئة"><select name="category" defaultValue={sp.get('category') ?? ''}><option value="">الكل</option>{meta.data?.categories.map(c => <option key={c}>{c}</option>)}</select></Field>}
          {use.includes('warehouse') && <Field label="المستودع"><select name="warehouseId" defaultValue={sp.get('warehouseId') ?? ''}><option value="">الكل</option>{wh.data?.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}</select></Field>}
          {use.includes('q') && <Field label="رقم القطعة أو الرقم التسلسلي *"><input name="q" defaultValue={sp.get('q') ?? ''} required dir="ltr" placeholder="P-000123" /></Field>}
          {use.includes('days') && <Field label="ينتهي خلال (يوم)"><input type="number" name="days" min={1} max={365} defaultValue={sp.get('days') ?? '30'} /></Field>}
        </div>
        <div className="actions">
          {meta.data?.canExport && rep.data && <button type="button" className="btn rx" onClick={() => download(`/api/custody/reports/${key}?${qs}&format=xlsx`, `${rep.data!.title}.xlsx`)}>إكسل</button>}
          {rep.data && <button type="button" className="btn" onClick={() => window.print()}>طباعة / PDF</button>}
          <button className="btn pri">عرض</button>
        </div>
      </form>
      {rep.isError && <div className="err">{(rep.error as Error).message}</div>}
      {needsPart && <div className="sub">أدخل رقم القطعة أو امسح الباركود لعرض سجل حياتها.</div>}
      {rep.data && <ReportView r={rep.data} />}
    </>
  )
}

function ReportView({ r }: { r: Report }) {
  return (
    <div className="report">
      <div className="report-head"><img src="/logo_full.png" alt="" /><div><h2>{r.title}</h2><div className="sub">{r.subtitle}</div><div className="sub num">أُعدّ في {ksa(r.generatedAt)}</div></div></div>
      {r.tables.map(t => (
        <section key={t.title} className="panel" style={{ marginTop: 14 }}>
          <div className="hd"><h3>{t.title} <span className="sub">({t.rows.length})</span></h3></div>
          <div className="tblw"><table>
            <thead><tr>{t.columns.map(c => <th key={c}>{c}</th>)}</tr></thead>
            <tbody>{t.rows.map((row, i) => <tr key={i}>{row.map((v, j) => <td key={j} className={typeof v === 'number' ? 'num' : ''}>{typeof v === 'number' ? v.toLocaleString('en-US', { maximumFractionDigits: 2 }) : v ?? ''}</td>)}</tr>)}</tbody>
          </table></div>
          {t.rows.length === 0 && <div className="sub" style={{ padding: 12 }}>لا بيانات.</div>}
        </section>))}
    </div>
  )
}
