import { useState, type FormEvent } from 'react'
import { NavLink, Navigate, Route, Routes, useNavigate } from 'react-router-dom'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import qrcode from 'qrcode-generator'
import { api, can, enrollMfa, ksa, login, logout, post, verifyMfa, type LoginStep, type Me } from './api'
import Dashboard from './pages/Dashboard'
import Trips from './pages/Trips'
import FillReport from './pages/FillReport'
import Security from './pages/Security'
import MasterData from './pages/MasterData'
import Assignments from './pages/Assignments'
import Users from './pages/Users'
import Meters from './pages/Meters'
import Attendance from './pages/Attendance'
import FillReports from './pages/FillReports'
import Custody from './pages/Custody'
import PartView from './pages/PartView'
import TruckFile from './pages/TruckFile'
import PartLabels from './pages/PartLabels'
import MyCustody from './pages/MyCustody'
import Orders from './pages/Orders'
import OrderHub from './pages/OrderHub'
import Dispatch from './pages/Dispatch'
import Documents, { DocumentView } from './pages/Documents'

export function useMe() {
  return useQuery({ queryKey: ['me'], queryFn: () => api<Me>('/api/me'), staleTime: 60_000, retry: false })
}

/** Renders the otpauth:// URI as a QR image (data: URL, no HTML injection, CSP-friendly). */
function Qr({ text }: { text: string }) {
  const q = qrcode(0, 'M'); q.addData(text); q.make()
  return <img className="qr" src={q.createDataURL(5, 2)} alt="رمز QR لإضافة الحساب إلى تطبيق المصادقة" width={q.getModuleCount() * 5 + 20} />
}

function Login() {
  const nav = useNavigate()
  const qc = useQueryClient()
  const [err, setErr] = useState('')
  const [busy, setBusy] = useState(false)
  const [step, setStep] = useState<LoginStep | null>(null)
  const [codes, setCodes] = useState<string[] | null>(null)

  async function run(fn: () => Promise<void>) {
    setBusy(true); setErr('')
    try { await fn() } catch (x) { setErr((x as Error).message) } finally { setBusy(false) }
  }
  const done = async () => { await qc.invalidateQueries(); nav('/') }

  function submitPassword(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const f = new FormData(e.currentTarget)
    run(async () => { const s = await login(String(f.get('u')), String(f.get('p'))); if (s.kind === 'done') await done(); else setStep(s) })
  }
  function submitCode(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const code = String(new FormData(e.currentTarget).get('code'))
    run(async () => {
      if (step?.kind === 'enroll') setCodes(await enrollMfa(step.mfaToken, code))
      else if (step?.kind === 'mfa') { await verifyMfa(step.mfaToken, code); await done() }
    })
  }

  return (
    <div className="login">
      <section className="hero-side"><h2>سجّل الدخول إلى منظومة فيول آب</h2><p>تحكّم في نقل الوقود من التعبئة حتى التسليم: مطابقة الكميات، تتبع الأسطول، وملف كل مركبة في مكان واحد.</p></section>
      <section className="form-side">
        {codes ? (
          <div className="form">
            <img src="/logo_full.png" alt="فيول آب" />
            <h1>احفظ رموز الاسترداد</h1>
            <div className="sub">استخدم أحدها للدخول إذا فقدت جوالك. كل رمز يعمل مرة واحدة، ولن تظهر هذه الرموز مرة أخرى.</div>
            <ol className="codes num">{codes.map(c => <li key={c}>{c}</li>)}</ol>
            <button className="btn pri" onClick={done}>حفظتها، تابع</button>
          </div>
        ) : !step ? (
          <form key="password" onSubmit={submitPassword}>
            <img src="/logo_full.png" alt="فيول آب" />
            <h1>مرحبًا بك مجددًا</h1>
            <div className="sub">إدارة عمليات الوقود مع إمكانية الوصول في الوقت الفعلي</div>
            <label className="fl" htmlFor="u">اسم المستخدم<input id="u" name="u" autoComplete="username" required maxLength={64} /></label>
            <label className="fl" htmlFor="p">كلمة المرور<input id="p" name="p" type="password" autoComplete="current-password" required maxLength={128} /></label>
            {err && <div className="err" role="alert">{err}</div>}
            <button className="btn pri" disabled={busy}>{busy ? 'جارٍ الدخول…' : 'تسجيل الدخول'}</button>
          </form>
        ) : (
          <form key="code" onSubmit={submitCode}>
            <img src="/logo_full.png" alt="فيول آب" />
            <h1>{step.kind === 'enroll' ? 'فعّل التحقق بخطوتين' : 'التحقق بخطوتين'}</h1>
            {step.kind === 'enroll' ? (
              <>
                <div className="sub">حسابات الإدارة تتطلب تطبيق مصادقة (Google أو Microsoft Authenticator). امسح الرمز ثم أدخل الرقم الظاهر في التطبيق.</div>
                <Qr text={step.otpauthUri} />
                <div className="sub">أو أدخل المفتاح يدويًا: <span className="num secret">{step.secret}</span></div>
              </>
            ) : <div className="sub">أدخل الرمز المكوّن من 6 أرقام من تطبيق المصادقة، أو أحد رموز الاسترداد.</div>}
            <label className="fl" htmlFor="code">رمز التحقق<input id="code" name="code" inputMode="numeric" autoComplete="one-time-code" required maxLength={20} autoFocus /></label>
            {err && <div className="err" role="alert">{err}</div>}
            <button className="btn pri" disabled={busy}>{busy ? 'جارٍ التحقق…' : 'تحقق'}</button>
            <button type="button" className="btn ghost" onClick={() => { setStep(null); setErr('') }}>رجوع</button>
          </form>
        )}
      </section>
    </div>
  )
}

interface Note { id: string; text: string; ref?: string; at: string; readAt?: string }

/** Notifications of the signed-in user (document expiry alerts, fill reports to review…). A ref starting with "/" opens that record. */
function Bell() {
  const [open, setOpen] = useState(false)
  const nav = useNavigate()
  const qc = useQueryClient()
  const { data } = useQuery({ queryKey: ['notifications'], queryFn: () => api<Note[]>('/api/notifications'), refetchInterval: 120_000 })
  const unread = data?.filter(n => !n.readAt).length ?? 0
  async function markRead() { await post('/api/notifications/read-all'); qc.invalidateQueries({ queryKey: ['notifications'] }) }
  return (
    <div className="bell-wrap">
      <button className="btn bell" aria-label={`الإشعارات (${unread} غير مقروء)`} aria-expanded={open} onClick={() => setOpen(!open)}>🔔{unread > 0 && <span className="badge num">{unread}</span>}</button>
      {open && (
        <div className="bell-list" role="dialog" aria-label="الإشعارات">
          <div className="spread" style={{ padding: '8px 12px', borderBottom: '1px solid var(--line)' }}><b>الإشعارات</b>{unread > 0 && <button className="btn" style={{ padding: '2px 10px' }} onClick={markRead}>تعليم الكل كمقروء</button>}</div>
          {(data ?? []).length === 0 && <div className="sub" style={{ padding: 12 }}>لا توجد إشعارات.</div>}
          {data?.slice(0, 30).map(n => {
            const link = n.ref?.startsWith('/') ? n.ref : null
            return (
              <button key={n.id} className={`bell-item ${n.readAt ? '' : 'unread'}`} disabled={!link} onClick={() => { if (link) { setOpen(false); nav(link) } }}>
                <span>{n.text}</span><span className="sub num">{ksa(n.at)}</span>
              </button>)
          })}
        </div>)}
    </div>
  )
}

function Layout() {
  const { data: me, isError, isLoading } = useMe()
  const nav = useNavigate()
  const qc = useQueryClient()
  if (isError) return <Navigate to="/login" replace />
  if (isLoading) return <div className="sub" style={{ padding: 24 }}>جارٍ التحميل…</div>
  const links = [
    { to: '/', label: 'لوحة التحكم', show: can(me, 'Ops') },
    { to: '/orders', label: 'طلبات العملاء', show: can(me, 'Ops') },
    { to: '/dispatch', label: 'لوحة التوزيع', show: can(me, 'Ops') },
    { to: '/trips', label: 'الرحلات', show: can(me, 'Ops') },
    { to: '/fill-reports', label: 'تقارير التعبئة', show: can(me, 'FillReports') },
    { to: '/fill-report', label: 'تقرير الكميات والعمليات', show: can(me, 'Moves') },
    { to: '/attendance', label: 'الحضور والانصراف', show: true },
    { to: '/custody', label: 'العُهد وقطع الغيار', show: can(me, 'Custody') },
    { to: '/my-custody', label: 'عهدتي', show: !!me?.driverId },
    { to: '/documents', label: 'وثائق المركبات والسائقين', show: can(me, 'Documents') || can(me, 'Ops') || can(me, 'Master') },
    { to: '/assign', label: 'الإسناد', show: can(me, 'Assign') },
    { to: '/meters', label: 'العدادات', show: can(me, 'Meters') },
    { to: '/master', label: 'البيانات الأساسية', show: can(me, 'Master') },
    { to: '/users', label: 'المستخدمون والصلاحيات', show: can(me, 'Users') },
    { to: '/security', label: 'الأمان والجلسات', show: true },
  ]
  return (
    <div className="app">
      <aside className="side" aria-label="القائمة الرئيسية">
        <div className="side-brand"><img src="/logo_mark.png" alt="" /><div><b>فيول آب</b><small>FULL APP</small></div></div>
        <nav>{links.filter(l => l.show).map(l => <NavLink key={l.to} to={l.to} end={!['/custody', '/orders', '/documents', '/fill-reports'].includes(l.to)}>{l.label}</NavLink>)}</nav>
      </aside>
      <div className="shell">
        <header className="topbar">
          <div className="end">
            <Bell />
            <span className="chip-user">{me?.displayName ?? '…'}</span>
            <button className="btn" onClick={async () => { await logout(); qc.clear(); nav('/login') }}>خروج</button>
          </div>
        </header>
        <Routes>
          <Route path="/" element={!can(me, 'Ops') && can(me, 'Custody') ? <Navigate to="/custody" replace /> : <Dashboard />} />
          <Route path="/trips" element={<Trips />} />
          <Route path="/fill-report" element={<FillReport />} />
          <Route path="/security" element={<Security />} />
          <Route path="/attendance" element={<Attendance />} />
          <Route path="/fill-reports" element={<FillReports />} />
          <Route path="/fill-reports/:id" element={<FillReports />} />
          <Route path="/orders" element={<Orders />} />
          <Route path="/orders/:id" element={<OrderHub />} />
          <Route path="/dispatch" element={<Dispatch />} />
          <Route path="/documents" element={<Documents />} />
          <Route path="/documents/:id" element={<DocumentView />} />
          <Route path="/assign" element={<Assignments />} />
          <Route path="/meters" element={<Meters />} />
          <Route path="/master" element={<MasterData />} />
          <Route path="/users" element={<Users />} />
          <Route path="/custody" element={<Custody />} />
          <Route path="/custody/part/:id" element={<PartView />} />
          <Route path="/custody/truck/:id" element={<TruckFile />} />
          <Route path="/custody/labels" element={<PartLabels />} />
          <Route path="/my-custody" element={<MyCustody />} />
        </Routes>
      </div>
    </div>
  )
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<Login />} />
      <Route path="/*" element={<Layout />} />
    </Routes>
  )
}
