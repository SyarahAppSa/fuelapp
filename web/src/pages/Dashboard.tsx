import { useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Bar, BarChart, CartesianGrid, Cell, Pie, PieChart, ResponsiveContainer, Tooltip, XAxis, YAxis, Area, AreaChart } from 'recharts'
import { useNavigate } from 'react-router-dom'
import { api, fmt, signed } from '../api'

type Point = { label: string; value: number }
interface Summary {
  from: string; to: string
  loaded: number; delivered: number; netDifference: number; operations: number; trucks: number; drivers: number
  open: number; awaitingApproval: number; underReview: number
  byStation: Point[]; byCustomer: Point[]; byDriver: Point[]; monthly: { month: string; liters: number }[]
  counts: { customers: number; stations: number; vehicles: number; drivers: number }
  byStatus: { status: string; label: string; value: number }[]
}

// status colours follow the status chips used everywhere else
const STATUS_COLOR: Record<string, string> = {
  Approved: 'var(--ok)', AwaitingApproval: 'var(--warn)', UnderReview: 'var(--crit)', Cancelled: 'var(--ink-3)',
  WaitingLoad: 'var(--s5)', Loaded: 'var(--s2)', EnRoute: 'var(--s1)', Arrived: 'var(--s4)', Unloading: 'var(--s3)',
}

const I = (d: string) => <svg viewBox="0 0 24 24" width="30" height="30" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={d} /></svg>
const icons = {
  customers: I('M3 21h18M5 21V7l7-4 7 4v14M9 9h1m4 0h1M9 13h1m4 0h1M9 17h1m4 0h1'),
  stations: I('M4 21V5a2 2 0 0 1 2-2h6a2 2 0 0 1 2 2v16M3 21h12M7 8h4M14 9h2a2 2 0 0 1 2 2v5a1.5 1.5 0 0 0 3 0V8l-3-3'),
  trucks: I('M2 6h11v10H2zM13 9h4l4 4v3h-8M6.5 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4zM17.5 19a2 2 0 1 0 0-4 2 2 0 0 0 0 4z'),
  drivers: I('M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8zM4 21a8 8 0 0 1 16 0'),
  ops: I('M12 3v4M12 17v4M3 12h4M17 12h4M12 16a4 4 0 1 0 0-8 4 4 0 0 0 0 8z'),
  liters: I('M12 3s6 6.5 6 11a6 6 0 0 1-12 0c0-4.5 6-11 6-11z'),
}

function Stat({ icon, label, value, unit, onClick }: { icon: ReactNode; label: string; value: string; unit?: string; onClick?: () => void }) {
  return (
    <button className="stat" onClick={onClick} disabled={!onClick}>
      <span className="stat-ic">{icon}</span>
      <span className="stat-tx"><span className="stat-l">{label}</span><span className="stat-v">{value}{unit && <small> {unit}</small>}</span></span>
    </button>
  )
}

/** «الأعلى» list: the bar length is the share of the largest value, the number sits inside the bar. */
function TopList({ title, points, unit }: { title: string; points: Point[]; unit: string }) {
  const max = Math.max(1, ...points.map(p => p.value))
  return (
    <section className="card toplist"><h2>{title}</h2>
      {points.length === 0 && <div className="sub">لا توجد بيانات في الفترة.</div>}
      {points.slice(0, 6).map(p => (
        <div className="tl-row" key={p.label}>
          <span className="tl-name" title={p.label}>{p.label || '—'}</span>
          <span className="tl-track"><span className="tl-bar" style={{ width: `${Math.max(18, (p.value / max) * 100)}%` }}><b className="num">{fmt(p.value)} {unit}</b></span></span>
        </div>))}
    </section>
  )
}

const month = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
const lastDay = (m: string) => { const [y, mo] = m.split('-').map(Number); return `${m}-${String(new Date(y, mo, 0).getDate()).padStart(2, '0')}` }

export default function Dashboard() {
  const nav = useNavigate()
  const now = month(new Date())
  const [range, setRange] = useState({ from: now, to: now })
  const qs = `from=${range.from}-01&to=${lastDay(range.to)}`
  const { data: s, isLoading, error } = useQuery({ queryKey: ['dashboard', qs], queryFn: () => api<Summary>(`/api/dashboard/summary?${qs}`), refetchInterval: 60_000 })
  const axis = { fill: 'var(--ink-3)', fontSize: 11 }
  const totalStatus = s?.byStatus.reduce((a, b) => a + b.value, 0) ?? 0

  return (
    <>
      <div className="dash-head">
        <h1>لوحة التحكم</h1>
        <div className="range" aria-label="الفترة">
          <label><span className="sr">من</span><input type="month" value={range.from} max={range.to} onChange={e => e.target.value && setRange({ ...range, from: e.target.value })} /></label>
          <span className="sub">إلى</span>
          <label><span className="sr">إلى</span><input type="month" value={range.to} min={range.from} max={now} onChange={e => e.target.value && setRange({ ...range, to: e.target.value })} /></label>
        </div>
      </div>
      {isLoading && <div className="sub">جارٍ التحميل…</div>}
      {error && <div className="err">{(error as Error).message}</div>}
      {s && <>
        <div className="stats">
          <Stat icon={icons.customers} label="عدد العملاء والمواقع" value={fmt(s.counts.customers)} onClick={() => nav('/master')} />
          <Stat icon={icons.stations} label="مواقع التحميل" value={fmt(s.counts.stations)} onClick={() => nav('/master')} />
          <Stat icon={icons.trucks} label="عدد الشاحنات" value={fmt(s.counts.vehicles)} unit={`(${s.trucks} عملت في الفترة)`} onClick={() => nav('/assign')} />
          <Stat icon={icons.drivers} label="عدد السائقين" value={fmt(s.counts.drivers)} unit={`(${s.drivers} نفّذوا عمليات)`} onClick={() => nav('/assign')} />
          <Stat icon={icons.ops} label="عمليات التعبئة في الفترة" value={fmt(s.operations)} onClick={() => nav('/trips')} />
          <Stat icon={icons.liters} label="اللترات المحمّلة في الفترة" value={fmt(s.loaded)} unit={`لتر · المسلّم ${fmt(s.delivered)}`} onClick={() => nav('/fill-report')} />
        </div>

        <div className="alerts">
          <button className="alert" onClick={() => nav('/trips')}><span>رحلات مفتوحة الآن</span><b className="num">{s.open}</b></button>
          <button className={`alert ${s.awaitingApproval ? 'warn' : ''}`} onClick={() => nav('/trips')}><span>بانتظار الاعتماد</span><b className="num">{s.awaitingApproval}</b></button>
          <button className={`alert ${s.underReview ? 'crit' : ''}`} onClick={() => nav('/trips')}><span>تحت المراجعة (فروقات)</span><b className="num">{s.underReview}</b></button>
          <div className="alert"><span>صافي الفروقات في الفترة</span><b className="num">{signed(s.netDifference)} لتر</b></div>
        </div>

        <div className="charts2">
          <section className="card chart"><h2>اللترات المحمّلة حسب موقع التحميل</h2>
            <ResponsiveContainer width="100%" height={280}>
              <BarChart data={s.byStation} margin={{ top: 8, bottom: 8 }}>
                <defs><linearGradient id="barg" x1="0" y1="0" x2="0" y2="1"><stop offset="0%" stopColor="var(--hero-b)" /><stop offset="100%" stopColor="var(--hero-a)" /></linearGradient></defs>
                <CartesianGrid stroke="var(--line)" vertical={false} />
                <XAxis dataKey="label" tick={axis} reversed interval={0} angle={-20} textAnchor="end" height={60} />
                <YAxis orientation="right" tick={axis} tickFormatter={v => fmt(v)} width={70} />
                <Tooltip formatter={(v: number) => [fmt(v) + ' لتر', 'المحمّل']} cursor={{ fill: 'var(--surface-2)' }} />
                <Bar dataKey="value" fill="url(#barg)" radius={[8, 8, 0, 0]} maxBarSize={42} />
              </BarChart>
            </ResponsiveContainer>
          </section>
          <section className="card chart"><h2>حالات الرحلات في الفترة</h2>
            <div className="donut">
              <ul className="legend">{s.byStatus.map(x => (
                <li key={x.status}><i style={{ background: STATUS_COLOR[x.status] ?? 'var(--s1)' }} />{x.label} <span className="sub num">({x.value} رحلة)</span></li>))}</ul>
              <div className="donut-chart">
                <ResponsiveContainer width="100%" height={240}>
                  <PieChart><Pie data={s.byStatus} dataKey="value" nameKey="label" innerRadius={62} outerRadius={100} stroke="var(--surface)" strokeWidth={2} startAngle={90} endAngle={-270}>
                    {s.byStatus.map(x => <Cell key={x.status} fill={STATUS_COLOR[x.status] ?? 'var(--s1)'} />)}</Pie>
                    <Tooltip formatter={(v: number) => `${v} رحلة`} /></PieChart>
                </ResponsiveContainer>
                <div className="donut-c"><b className="num">{totalStatus}</b><span>رحلة</span></div>
              </div>
            </div>
          </section>
        </div>

        <div className="charts2">
          <TopList title="أعلى العملاء استلامًا للوقود" points={s.byCustomer} unit="لتر" />
          <TopList title="أعلى السائقين تعبئة" points={s.byDriver} unit="لتر" />
        </div>

        <section className="card chart" style={{ marginTop: 16 }}><h2>حركة التعبئة خلال 12 شهرًا</h2>
          <ResponsiveContainer width="100%" height={220}><AreaChart data={s.monthly}><CartesianGrid stroke="var(--line)" vertical={false} /><XAxis dataKey="month" tick={axis} reversed /><YAxis orientation="right" tick={axis} tickFormatter={v => fmt(v)} width={70} /><Tooltip formatter={(v: number) => [fmt(v) + ' لتر', 'المحمّل']} /><Area dataKey="liters" stroke="var(--brand)" strokeWidth={2} fill="var(--brand)" fillOpacity={0.12} /></AreaChart></ResponsiveContainer>
        </section>
      </>}
    </>
  )
}
