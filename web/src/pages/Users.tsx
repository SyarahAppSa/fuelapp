import { useState, type FormEvent } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api, can, ksa, post } from '../api'
import { useMe } from '../App'
import { Field, Panel, formData, useAction, useDrivers } from '../ui'

type Level = 'None' | 'View' | 'Edit'
interface UserRow { id: string; username: string; displayName: string; role: string; driverId?: string; isActive: boolean; mfaEnabled: boolean; lastLoginAt?: string; permissions: { section: string; level: Level }[] }

const sections: [string, string][] = [['Ops', 'التشغيل والرحلات'], ['Moves', 'تقارير الحركة'], ['Approve', 'الاعتماد'], ['Assign', 'الإسناد'], ['Meters', 'العدادات'], ['Tracking', 'التتبع'],
  ['Custody', 'العهد وقطع الغيار'], ['VehicleFile', 'ملف المركبة'], ['Costs', 'التكاليف'], ['Master', 'البيانات الأساسية'], ['Users', 'المستخدمون'], ['Driver', 'تطبيق السائق'], ['Export', 'التصدير'], ['Attendance', 'الحضور والانصراف (للمشرفين)']]
const roles: [string, string][] = [['Admin', 'مدير المنظومة'], ['OpsSupervisor', 'مشرف تشغيل'], ['Storekeeper', 'أمين مستودع'], ['Driver', 'سائق'], ['Viewer', 'اطلاع فقط'], ['Custom', 'مخصص']]
const roleName = (r: string) => roles.find(x => x[0] === r)?.[1] ?? r

/** Every rule here (no granting above your own level, only an Admin manages Admins) is enforced by the server too. */
export default function Users() {
  const { data: me } = useMe()
  const edit = can(me, 'Users', 'Edit')
  const act = useAction()
  const drivers = useDrivers()
  const users = useQuery({ queryKey: ['users'], queryFn: () => api<UserRow[]>('/api/users') })
  const [editing, setEditing] = useState<UserRow | null>(null)
  const [levels, setLevels] = useState<Record<string, Level>>({})

  function create(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const form = e.currentTarget
    const b = formData(form)
    act.run(async () => { await post('/api/users', { ...b, levels: b.role === 'Custom' ? {} : undefined }); form.reset() }, 'أُنشئ المستخدم. عند أول دخول لحسابات الإدارة سيُطلب تفعيل التحقق بخطوتين.')
  }
  function startEdit(u: UserRow) {
    setEditing(u)
    setLevels(Object.fromEntries(sections.map(([s]) => [s, u.permissions.find(p => p.section === s)?.level ?? 'None'])))
  }
  function save() {
    if (!editing) return
    const body = { username: editing.username, displayName: editing.displayName, role: editing.role, driverId: editing.driverId ?? null, levels }
    act.run(async () => { await api(`/api/users/${editing.id}/permissions`, { method: 'PUT', body: JSON.stringify(body) }); setEditing(null) }, 'حُفظت الصلاحيات.')
  }

  return (
    <>
      <h1>المستخدمون والصلاحيات</h1>
      {act.banner}
      {edit && <form className="filter" onSubmit={create}><div className="fg">
        <Field label="اسم المستخدم *"><input name="username" required pattern="[a-zA-Z0-9._\-]{3,32}" maxLength={32} dir="ltr" /></Field>
        <Field label="الاسم الظاهر *"><input name="displayName" required minLength={2} maxLength={80} /></Field>
        <Field label="كلمة المرور الأولى (10 أحرف على الأقل) *"><input name="password" type="password" required minLength={10} maxLength={64} autoComplete="new-password" /></Field>
        <Field label="الدور *"><select name="role" required defaultValue="Driver">{roles.map(([k, v]) => <option key={k} value={k}>{v}</option>)}</select></Field>
        <Field label="ربط بسائق (لحسابات السائقين)"><select name="driverId"><option value="">—</option>{drivers.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></Field>
      </div><div className="actions"><button className="btn pri" disabled={act.busy}>إنشاء مستخدم</button></div></form>}

      {editing && (
        <Panel title={`صلاحيات ${editing.displayName}`} actions={<div className="row"><button className="btn pri" onClick={save} disabled={act.busy}>حفظ</button><button className="btn" onClick={() => setEditing(null)}>إلغاء</button></div>}>
          <div className="tblw"><table><thead><tr><th>القسم</th><th>بدون</th><th>اطلاع</th><th>تعديل</th></tr></thead>
            <tbody>{sections.map(([s, label]) => (
              <tr key={s}><td>{label}</td>{(['None', 'View', 'Edit'] as Level[]).map(l => (
                <td key={l}><input type="radio" aria-label={`${label}: ${l}`} name={`lv-${s}`} checked={levels[s] === l} onChange={() => setLevels({ ...levels, [s]: l })} /></td>))}</tr>))}</tbody></table></div>
        </Panel>)}

      <Panel title="المستخدمون">
        <div className="tblw"><table><thead><tr><th>المستخدم</th><th>الدور</th><th>الحالة</th><th>التحقق بخطوتين</th><th>آخر دخول</th><th>الصلاحيات</th>{edit && <th></th>}</tr></thead>
          <tbody>{users.data?.map(u => (
            <tr key={u.id}><td><b>{u.displayName}</b><div className="sub num">{u.username}</div></td><td>{roleName(u.role)}</td>
              <td>{u.isActive ? <span className="chip st-Approved">نشط</span> : <span className="chip st-Cancelled">معطّل</span>}</td><td>{u.mfaEnabled ? 'مفعّل' : '—'}</td>
              <td className="num">{ksa(u.lastLoginAt)}</td>
              <td style={{ whiteSpace: 'normal', minWidth: 220 }} className="sub">{u.permissions.map(p => `${sections.find(s => s[0] === p.section)?.[1] ?? p.section}: ${p.level === 'Edit' ? 'تعديل' : 'اطلاع'}`).join(' · ')}</td>
              {edit && <td><button className="btn" onClick={() => startEdit(u)}>تعديل الصلاحيات</button></td>}</tr>))}</tbody></table></div>
      </Panel>
      <p className="sub">التعطيل، وإنهاء الجلسات، وإعادة ضبط التحقق بخطوتين: من صفحة «الأمان والجلسات».</p>
    </>
  )
}
