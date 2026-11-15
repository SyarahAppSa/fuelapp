import { useQuery } from '@tanstack/react-query'
import { Bar, BarChart, CartesianGrid, Cell, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis, Area, AreaChart, Legend } from 'recharts'
import { api, fmt, signed } from '../api'

type Point = { label: string; value: number }
interface Summary {
  loaded: number; delivered: number; netDifference: number; operations: number; trucks: number; drivers: number
  open: number; awaitingApproval: number; underReview: number
  byStation: Point[]; byCustomer: Point[]; byDriver: Point[]; monthly: { month: string; liters: number }[]
}

// Validated categorical order (light mode) — see the FuelUp identity tokens.
const SERIES = ['var(--s1)', 'var(--s2)', 'var(--s3)', 'var(--s4)', 'var(--s5)']

function foldOther(points: Point[]): Point[] {
  if (points.length <= 5) return points
  const rest = points.slice(4).reduce((s, p) => s + p.value, 0)
  return [...points.slice(0, 4), { label: 'أخرى', value: rest }]
}

export default function Dashboard() {
  const { data: s, isLoading, error } = useQuery({ queryKey: ['dashboard'], queryFn: () => api<Summary>('/api/dashboard/summary'), refetchInterval: 60_000 })
  if (isLoading) return <div className="sub">جارٍ التحميل…</div>
  if (error || !s) return <div className="err">{(error as Error)?.message ?? 'تعذر التحميل'}</div>
  const axis = { fill: 'var(--ink-3)', fontSize: 11 }
  return (
    <>
      <h1>لوحة التحكم</h1>
      <section className="hero" aria-label="ملخص الفترة" style={{ marginTop: 14 }}>
        <div><div>إجمالي المعبأ في الفترة</div><div className="big">{fmt(s.loaded)} <small>لتر</small></div><div>{s.operations} عملية تعبئة</div></div>
        <div className="tiles">
          <div className="tile"><div>المسلّم حسب العداد</div><div className="v">{fmt(s.delivered)}</div></div>
          <div className="tile"><div>صافي الفروقات (لتر)</div><div className="v">{signed(s.netDifference)}</div></div>
          <div className="tile"><div>شاحنات عاملة</div><div className="v">{s.trucks}</div></div>
          <div className="tile"><div>سائقون نفّذوا عمليات</div><div className="v">{s.drivers}</div></div>
        </div>
      </section>
      <div className="kpis">
        <div className="kpi"><div className="l">رحلات مفتوحة</div><div className="v">{s.open}</div></div>
        <div className={`kpi ${s.awaitingApproval ? 'attn' : ''}`}><div className="l">بانتظار الاعتماد</div><div className="v">{s.awaitingApproval}</div></div>
        <div className={`kpi ${s.underReview ? 'attn' : ''}`}><div className="l">تحت المراجعة</div><div className="v">{s.underReview}</div></div>
      </div>
      <div className="charts">
        <section className="card chart"><h2>التعبئة حسب الموقع</h2>
          <ResponsiveContainer width="100%" height={240}><BarChart data={s.byStation}><CartesianGrid stroke="var(--line)" vertical={false} /><XAxis dataKey="label" tick={axis} reversed /><YAxis orientation="right" tick={axis} /><Tooltip formatter={(v: number) => [fmt(v) + ' لتر', 'المعبأ']} /><Bar dataKey="value" fill="var(--s1)" radius={[4, 4, 0, 0]} maxBarSize={36} /></BarChart></ResponsiveContainer>
        </section>
        <section className="card chart"><h2>توزيع الكميات المسلّمة على العملاء</h2>
          <ResponsiveContainer width="100%" height={240}><PieChart><Pie data={foldOther(s.byCustomer)} dataKey="value" nameKey="label" innerRadius={58} outerRadius={90} stroke="var(--surface)" strokeWidth={2}>{foldOther(s.byCustomer).map((p, i) => <Cell key={p.label} fill={p.label === 'أخرى' ? 'var(--ink-3)' : SERIES[i]} />)}</Pie><Tooltip formatter={(v: number) => fmt(v) + ' لتر'} /><Legend layout="vertical" align="left" verticalAlign="middle" /></PieChart></ResponsiveContainer>
        </section>
        <section className="card chart"><h2>حركة التعبئة الشهرية</h2>
          <ResponsiveContainer width="100%" height={240}><AreaChart data={s.monthly}><CartesianGrid stroke="var(--line)" vertical={false} /><XAxis dataKey="month" tick={axis} reversed /><YAxis orientation="right" tick={axis} /><Tooltip formatter={(v: number) => [fmt(v) + ' لتر', 'المعبأ']} /><Area dataKey="liters" stroke="var(--s1)" strokeWidth={2} fill="var(--s1)" fillOpacity={0.15} /></AreaChart></ResponsiveContainer>
        </section>
        <section className="card chart"><h2>أكثر السائقين تعبئة</h2>
          <ResponsiveContainer width="100%" height={240}><BarChart data={s.byDriver} layout="vertical"><XAxis type="number" tick={axis} reversed /><YAxis type="category" dataKey="label" orientation="right" width={110} tick={axis} /><Tooltip formatter={(v: number) => [fmt(v) + ' لتر', 'المعبأ']} /><Bar dataKey="value" fill="var(--s1)" radius={[4, 4, 4, 4]} maxBarSize={18} /></BarChart></ResponsiveContainer>
        </section>
      </div>
    </>
  )
}
