import { useEffect, useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, can, download, ksa, post } from '../api'
import { useMe } from '../App'
import { Field, Panel, useAction } from '../ui'

interface Rec { id: string; userId: string; name?: string; day: string; checkInAt: string; checkOutAt?: string; minutes: number; status: string; forgotten: boolean; corrected: boolean; note?: string }
interface Mine { open: Rec | null; todayMinutes: number; records: Rec[] }
interface Team { from: string; to: string; summary: { userId: string; name: string; username?: string; days: number; minutes: number; open: number; forgotten: number }[]; records: Rec[] }

const hm = (m: number) => `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}`
const monthStart = () => { const d = new Date(); return new Date(Date.UTC(d.getFullYear(), d.getMonth(), 1)).toISOString().slice(0, 10) }
const today = () => new Date().toISOString().slice(0, 10)
const statusChip = (r: Rec) => r.checkOutAt ? 'st-Approved' : r.forgotten ? 'st-UnderReview' : 'st-EnRoute'
// datetime-local input value in Saudi time (UTC+3, no daylight saving)
const toLocal = (iso?: string) => iso ? new Date(new Date(iso).getTime() + 3 * 3600_000).toISOString().slice(0, 16) : ''
const fromLocal = (v: string) => v ? new Date(v + ':00+03:00').toISOString() : null

/** Optional location with the punch: asked once, never required (attendance still works without it). */
function position(): Promise<{ lat?: number; lng?: number }> {
  return new Promise(res => {
    if (!navigator.geolocation) return res({})
    navigator.geolocation.getCurrentPosition(p => res({ lat: +p.coords.latitude.toFixed(5), lng: +p.coords.longitude.toFixed(5) }), () => res({}), { timeout: 5000, maximumAge: 60_000 })
  })
}

export default function Attendance() {
  const { data: me } = useMe()
  const act = useAction()
  const mine = useQuery({ queryKey: ['attendance', 'me'], queryFn: () => api<Mine>('/api/attendance/me'), refetchInterval: 60_000 })
  const manager = can(me, 'Attendance')
  const [range, setRange] = useState({ from: monthStart(), to: today() })
  const team = useQuery({ queryKey: ['attendance', 'team', range], queryFn: () => api<Team>(`/api/attendance?from=${range.from}&to=${range.to}`), enabled: manager })
  const [editing, setEditing] = useState<Rec | null>(null)
  const [, tick] = useState(0)
  useEffect(() => { const t = setInterval(() => tick(x => x + 1), 30_000); return () => clearInterval(t) }, [])

  const open = mine.data?.open
  const sinceOpen = open ? Math.round((Date.now() - new Date(open.checkInAt).getTime()) / 60_000) : 0
  const punch = (kind: 'check-in' | 'check-out') =>
    act.run(async () => { const pos = await position(); await post(`/api/attendance/${kind}`, pos) }, kind === 'check-in' ? 'سُجّل حضورك.' : 'سُجّل انصرافك.')

  function filter(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    setRange({ from: String(f.get('from')), to: String(f.get('to')) })
  }
  function saveCorrection(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    const body = { checkInAt: fromLocal(String(f.get('in'))), checkOutAt: fromLocal(String(f.get('out'))), reason: String(f.get('reason')) }
    const id = editing!.id
    act.run(async () => { await api(`/api/attendance/${id}`, { method: 'PUT', body: JSON.stringify(body) }); setEditing(null) }, 'عُدّل السجل وحُفظ السبب في سجل التدقيق.')
  }

  return (
    <>
      <h1>الحضور والانصراف</h1>
      {act.banner}
      <section className="hero" style={{ marginTop: 14 }} aria-label="حضوري اليوم">
        <div>
          <div>{open ? 'أنت حاضر منذ' : 'لم تسجّل حضورًا مفتوحًا'}</div>
          <div className="big">{open ? <>{hm(sinceOpen)} <small>ساعة</small></> : '—'}</div>
          <div>{open ? `منذ ${ksa(open.checkInAt)}` : `مجموع اليوم ${hm(mine.data?.todayMinutes ?? 0)} ساعة`}</div>
        </div>
        <div className="tiles">
          {open
            ? <button className="btn punch out" disabled={act.busy} onClick={() => punch('check-out')}>تسجيل الانصراف</button>
            : <button className="btn punch in" disabled={act.busy} onClick={() => punch('check-in')}>تسجيل الحضور</button>}
          <div className="tile"><div>مجموع اليوم</div><div className="v">{hm((mine.data?.todayMinutes ?? 0) + sinceOpen)}</div></div>
        </div>
      </section>

      <Panel title="سجلي (آخر 30 يومًا)">
        <div className="tblw"><table><thead><tr><th>التاريخ</th><th>الحضور</th><th>الانصراف</th><th>المدة</th><th>الحالة</th></tr></thead>
          <tbody>{mine.data?.records.map(r => (
            <tr key={r.id}><td className="num">{r.day}</td><td className="num">{ksa(r.checkInAt)}</td><td className="num">{ksa(r.checkOutAt)}</td><td className="num">{hm(r.minutes)}</td>
              <td><span className={`chip ${statusChip(r)}`}>{r.status}</span>{r.corrected && <span className="sub"> · مُعدّل</span>}</td></tr>))}</tbody></table></div>
      </Panel>

      {manager && team.data && (
        <>
          <form className="filter" onSubmit={filter} style={{ marginTop: 14 }}>
            <div className="fg">
              <Field label="من تاريخ"><input name="from" type="date" defaultValue={range.from} /></Field>
              <Field label="إلى تاريخ"><input name="to" type="date" defaultValue={range.to} /></Field>
            </div>
            <div className="actions"><button className="btn pri">عرض</button>
              {can(me, 'Export', 'Edit') && <button type="button" className="btn rx" onClick={() => download(`/api/attendance?from=${range.from}&to=${range.to}&format=xlsx`, `الحضور-${range.from}-${range.to}.xlsx`)}>إكسل</button>}</div>
          </form>

          <Panel title="إجمالي الموظفين في الفترة">
            <div className="tblw"><table><thead><tr><th>الموظف</th><th>أيام الحضور</th><th>إجمالي الساعات</th><th>حاضر الآن</th><th>انصراف منسي</th></tr></thead>
              <tbody>{team.data.summary.map(s => (
                <tr key={s.userId}><td><b>{s.name}</b><div className="sub num">{s.username}</div></td><td className="num">{s.days}</td><td className="num">{hm(s.minutes)}</td>
                  <td className="num">{s.open - s.forgotten}</td><td className={`num ${s.forgotten ? 'diff' : ''}`}>{s.forgotten}</td></tr>))}</tbody></table></div>
          </Panel>

          {editing && (
            <Panel title={`تعديل سجل ${editing.name ?? ''} — ${editing.day}`} actions={<button className="btn" onClick={() => setEditing(null)}>إلغاء</button>}>
              <form className="filter" onSubmit={saveCorrection} style={{ boxShadow: 'none' }}><div className="fg">
                <Field label="الحضور *"><input name="in" type="datetime-local" required defaultValue={toLocal(editing.checkInAt)} /></Field>
                <Field label="الانصراف"><input name="out" type="datetime-local" defaultValue={toLocal(editing.checkOutAt)} /></Field>
                <Field label="سبب التعديل *"><input name="reason" required minLength={3} maxLength={500} /></Field>
              </div><div className="actions"><button className="btn pri" disabled={act.busy}>حفظ التعديل</button></div></form>
            </Panel>)}

          <Panel title="سجلات الفترة">
            <div className="tblw"><table><thead><tr><th>الموظف</th><th>التاريخ</th><th>الحضور</th><th>الانصراف</th><th>المدة</th><th>الحالة</th>{can(me, 'Attendance', 'Edit') && <th></th>}</tr></thead>
              <tbody>{team.data.records.map(r => (
                <tr key={r.id} className={r.forgotten ? 'flag' : ''}><td>{r.name}</td><td className="num">{r.day}</td><td className="num">{ksa(r.checkInAt)}</td><td className="num">{ksa(r.checkOutAt)}</td>
                  <td className="num">{hm(r.minutes)}</td><td><span className={`chip ${statusChip(r)}`}>{r.status}</span>{r.corrected && <span className="sub"> · مُعدّل</span>}</td>
                  {can(me, 'Attendance', 'Edit') && <td><button className="btn" onClick={() => setEditing(r)}>تعديل</button></td>}</tr>))}</tbody></table></div>
          </Panel>
        </>)}
    </>
  )
}
