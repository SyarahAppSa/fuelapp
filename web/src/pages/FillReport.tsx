import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, can, download, fmt, signed } from '../api'
import { useMe } from '../App'

interface Row { id: string; no: string; at: string; status: string; driver: string; plate: string; place: string; loadRef: string; customer: string; project?: string; meter?: string; meterBefore?: number; meterAfter?: number; computed?: number; receipt?: number; difference?: number; differenceFlag: boolean; approvedBy?: string; approvedAt: string; notes: string }
interface Report { summary: { count: number; computed: number; receipt: number; difference: number; pending: number; flagged: number }; rows: Row[] }

const monthStart = () => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1).toISOString().slice(0, 10) }

export default function FillReport() {
  const { data: me } = useMe()
  const [filters, setFilters] = useState({ from: monthStart(), to: new Date().toISOString().slice(0, 10) })
  const qs = new URLSearchParams(filters).toString()
  const { data, isLoading } = useQuery({ queryKey: ['fill', qs], queryFn: () => api<Report>(`/api/reports/fill-operations?${qs}`) })
  const [page, setPage] = useState(1)
  const size = 20
  function apply(e: FormEvent<HTMLFormElement>) { e.preventDefault(); const f = new FormData(e.currentTarget); setFilters({ from: String(f.get('from')), to: String(f.get('to')) }); setPage(1) }
  const rows = data?.rows ?? []
  const pages = Math.max(1, Math.ceil(rows.length / size))
  const s = data?.summary
  return (
    <>
      <div className="spread"><h1>تقرير عمليات التعبئة</h1>
        {s && <div className="sumstrip"><span>عدد العمليات <b>{s.count}</b></span><span>المحتسبة <b>{fmt(s.computed)}</b> لتر</span><span>المعتمدة <b>{fmt(s.receipt)}</b> لتر</span><span>فرق العداد عن السند <b>{signed(s.difference)}</b></span><span>المعلقة <b>{s.pending}</b></span><span>تحتاج مراجعة <b>{s.flagged}</b></span></div>}
      </div>
      <form className="filter" onSubmit={apply}>
        <div className="fg"><label className="fl" htmlFor="from">من تاريخ<input id="from" name="from" type="date" defaultValue={filters.from} /></label><label className="fl" htmlFor="to">إلى تاريخ<input id="to" name="to" type="date" defaultValue={filters.to} /></label></div>
        <div className="actions"><button className="btn pri">تطبيق</button><button type="button" className="btn ghost" onClick={() => setFilters({ from: monthStart(), to: new Date().toISOString().slice(0, 10) })}>إلغاء</button></div>
      </form>
      {can(me, 'Export', 'Edit') && <div className="row" style={{ marginBottom: 10 }}><button className="btn rx" onClick={() => download(`/api/reports/fill-operations?${qs}&format=xlsx`, 'تقرير-عمليات-التعبئة.xlsx')}>إكسل</button></div>}
      <section className="panel">
        {isLoading ? <div className="sub" style={{ padding: 16 }}>جارٍ التحميل…</div> : rows.length === 0 ? <div className="sub" style={{ padding: 16 }}>لا توجد عمليات في هذه الفترة.</div> : (
          <div className="tblw"><table>
            <thead><tr><th>رقم العملية</th><th>التاريخ والوقت</th><th>الحالة</th><th>السائق</th><th>اللوحة</th><th>مكان التعبئة</th><th>رقم طلب التحميل</th><th>العميل / المشروع</th><th>العداد</th><th>قبل</th><th>بعد</th><th>المحتسبة</th><th>المعتمدة</th><th>الفرق</th><th>الاعتماد</th><th>الملاحظات</th></tr></thead>
            <tbody>{rows.slice((page - 1) * size, page * size).map(r => (
              <tr key={r.id} className={r.differenceFlag ? 'flag' : ''}>
                <td className="num"><b>{r.no}</b></td><td className="num">{r.at}</td><td>{r.status}</td><td>{r.driver}</td><td className="num">{r.plate}</td><td>{r.place}</td><td className="num">{r.loadRef}</td>
                <td>{r.customer}<div className="sub">{r.project}</div></td><td className="num">{r.meter ?? '—'}</td><td className="num">{fmt(r.meterBefore)}</td><td className="num">{fmt(r.meterAfter)}</td>
                <td className="num">{fmt(r.computed)}</td><td className="num">{fmt(r.receipt)}</td><td className={`num ${r.differenceFlag ? 'diff' : ''}`}>{signed(r.difference)}</td>
                <td>{r.approvedBy ?? '—'}<div className="sub num">{r.approvedAt}</div></td><td style={{ whiteSpace: 'normal', minWidth: 200 }}>{r.notes}</td>
              </tr>))}</tbody>
          </table></div>)}
        {pages > 1 && <div className="pager">{Array.from({ length: pages }, (_, i) => i + 1).map(p => <button key={p} className={p === page ? 'on' : ''} onClick={() => setPage(p)}>{p}</button>)}</div>}
      </section>
    </>
  )
}
