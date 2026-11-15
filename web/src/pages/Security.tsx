import { useState, type FormEvent } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, can, ksa, post } from '../api'
import { useMe } from '../App'

interface Session { id: string; client: string; ip?: string; userAgent?: string; createdAt: string; lastSeenAt: string; current: boolean }
interface MySec { mfaEnabled: boolean; mfaRequired: boolean; recoveryCodesLeft: number; lastLoginAt?: string; lastLoginIp?: string; passwordChangedAt: string }
interface UserRow { id: string; username: string; displayName: string; role: string; isActive: boolean; mfaEnabled: boolean; lockoutUntil?: string; lastLoginAt?: string }
interface SecEvent { at: string; action: string; severity: 'Low' | 'Medium' | 'High'; detail?: string; ip?: string; user?: string }

const sevChip = { Low: 'st-Approved', Medium: 'st-AwaitingApproval', High: 'st-UnderReview' } as const
const sevLabel = { Low: 'منخفض', Medium: 'متوسط', High: 'مرتفع' } as const

export default function Security() {
  const { data: me } = useMe()
  const qc = useQueryClient()
  const sec = useQuery({ queryKey: ['mysec'], queryFn: () => api<MySec>('/api/me/security') })
  const sessions = useQuery({ queryKey: ['sessions'], queryFn: () => api<Session[]>('/api/me/sessions') })
  const isUserAdmin = can(me, 'Users')
  const users = useQuery({ queryKey: ['users'], queryFn: () => api<UserRow[]>('/api/users'), enabled: isUserAdmin })
  const events = useQuery({ queryKey: ['secevents'], queryFn: () => api<SecEvent[]>('/api/security/events?take=100'), enabled: isUserAdmin })
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const act = useMutation({
    mutationFn: ({ path, body }: { path: string; body?: unknown; okText: string }) => post(path, body),
    onSuccess: (_d, v) => { setMsg({ ok: true, text: v.okText }); qc.invalidateQueries() },
    onError: (e) => setMsg({ ok: false, text: (e as Error).message }),
  })

  function changePassword(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    if (f.get('n') !== f.get('n2')) { setMsg({ ok: false, text: 'كلمتا المرور الجديدتان غير متطابقتين.' }); return }
    act.mutate({ path: '/api/me/password', body: { currentPassword: f.get('c'), newPassword: f.get('n') }, okText: 'تم تغيير كلمة المرور وإنهاء جلساتك الأخرى.' })
    e.currentTarget.reset()
  }

  return (
    <>
      <h1>الأمان والجلسات</h1>
      {msg && <div className={msg.ok ? 'okmsg' : 'err'} role="status" style={{ marginTop: 10 }}>{msg.text}</div>}

      <div className="kpis">
        <div className="kpi"><div className="l">التحقق بخطوتين</div><div className="v" style={{ fontSize: 18 }}>{sec.data?.mfaEnabled ? 'مفعّل' : sec.data?.mfaRequired ? 'مطلوب' : 'غير مفعّل'}</div></div>
        <div className="kpi"><div className="l">رموز الاسترداد المتبقية</div><div className="v">{sec.data?.recoveryCodesLeft ?? '—'}</div></div>
        <div className="kpi"><div className="l">آخر دخول</div><div className="v num" style={{ fontSize: 16 }}>{ksa(sec.data?.lastLoginAt)}<div className="sub">{sec.data?.lastLoginIp}</div></div></div>
      </div>

      <section className="panel" style={{ marginTop: 14 }}>
        <div className="hd spread"><h3>أجهزتي المتصلة</h3>
          <button className="btn" onClick={() => act.mutate({ path: '/api/me/sessions/revoke-others', okText: 'أُنهيت كل الجلسات الأخرى.' })}>إنهاء كل الجلسات الأخرى</button></div>
        <div className="tblw"><table>
          <thead><tr><th>الجهاز</th><th>عنوان IP</th><th>بدأت</th><th>آخر نشاط</th><th></th></tr></thead>
          <tbody>{sessions.data?.map(s => (
            <tr key={s.id}><td>{s.client === 'web' ? 'متصفح' : 'تطبيق الجوال'}{s.current && <span className="chip st-Approved" style={{ marginInlineStart: 8 }}>هذا الجهاز</span>}<div className="sub">{s.userAgent?.slice(0, 60)}</div></td>
              <td className="num">{s.ip}</td><td className="num">{ksa(s.createdAt)}</td><td className="num">{ksa(s.lastSeenAt)}</td>
              <td>{!s.current && <button className="btn" onClick={() => act.mutate({ path: `/api/me/sessions/${s.id}/revoke`, okText: 'أُنهيت الجلسة.' })}>إنهاء</button>}</td></tr>
          ))}</tbody>
        </table></div>
      </section>

      <section className="panel" style={{ marginTop: 14 }}>
        <div className="hd"><h3>تغيير كلمة المرور</h3></div>
        <form className="filter" onSubmit={changePassword} style={{ boxShadow: 'none' }}>
          <div className="fg">
            <label className="fl" htmlFor="c">الحالية<input id="c" name="c" type="password" autoComplete="current-password" required maxLength={128} /></label>
            <label className="fl" htmlFor="n">الجديدة (10 أحرف على الأقل)<input id="n" name="n" type="password" autoComplete="new-password" required minLength={10} maxLength={64} /></label>
            <label className="fl" htmlFor="n2">تأكيد الجديدة<input id="n2" name="n2" type="password" autoComplete="new-password" required minLength={10} maxLength={64} /></label>
          </div>
          <div className="actions"><button className="btn pri" disabled={act.isPending}>تغيير</button></div>
        </form>
      </section>

      {isUserAdmin && (
        <>
          <section className="panel" style={{ marginTop: 14 }}>
            <div className="hd"><h3>حسابات المستخدمين</h3></div>
            <div className="tblw"><table>
              <thead><tr><th>المستخدم</th><th>الدور</th><th>الحالة</th><th>التحقق بخطوتين</th><th>آخر دخول</th><th>إجراءات</th></tr></thead>
              <tbody>{users.data?.map(u => {
                const locked = u.lockoutUntil && new Date(u.lockoutUntil) > new Date()
                return (
                  <tr key={u.id}>
                    <td><b>{u.displayName}</b><div className="sub num">{u.username}</div></td><td>{u.role}</td>
                    <td>{!u.isActive ? <span className="chip st-Cancelled">معطّل</span> : locked ? <span className="chip st-UnderReview">موقوف مؤقتًا</span> : <span className="chip st-Approved">نشط</span>}</td>
                    <td>{u.mfaEnabled ? 'مفعّل' : '—'}</td><td className="num">{ksa(u.lastLoginAt)}</td>
                    <td className="row" style={{ gap: 6 }}>
                      {(locked || !u.isActive) && <button className="btn" onClick={() => act.mutate({ path: `/api/users/${u.id}/enable`, okText: 'فُعّل الحساب.' })}>تفعيل</button>}
                      {u.isActive && u.id !== me?.id && <button className="btn" onClick={() => act.mutate({ path: `/api/users/${u.id}/disable`, okText: 'عُطّل الحساب وأُنهيت جلساته.' })}>تعطيل</button>}
                      <button className="btn" onClick={() => act.mutate({ path: `/api/users/${u.id}/revoke-sessions`, okText: 'أُنهيت جلسات المستخدم.' })}>إنهاء الجلسات</button>
                      {me?.role === 'Admin' && u.mfaEnabled && <button className="btn" onClick={() => act.mutate({ path: `/api/users/${u.id}/reset-mfa`, okText: 'أُعيد ضبط التحقق بخطوتين.' })}>إعادة ضبط التحقق</button>}
                    </td>
                  </tr>)
              })}</tbody>
            </table></div>
          </section>

          <section className="panel" style={{ marginTop: 14 }}>
            <div className="hd"><h3>سجل الأحداث الأمنية</h3></div>
            <div className="tblw"><table>
              <thead><tr><th>الوقت</th><th>الخطورة</th><th>الحدث</th><th>المستخدم</th><th>IP</th><th>التفاصيل</th></tr></thead>
              <tbody>{events.data?.map((e, i) => (
                <tr key={i}><td className="num">{ksa(e.at)}</td><td><span className={`chip ${sevChip[e.severity]}`}>{sevLabel[e.severity]}</span></td>
                  <td className="num">{e.action}</td><td>{e.user ?? '—'}</td><td className="num">{e.ip}</td><td style={{ whiteSpace: 'normal', minWidth: 240 }}>{e.detail}</td></tr>
              ))}</tbody>
            </table></div>
          </section>
        </>
      )}
    </>
  )
}
