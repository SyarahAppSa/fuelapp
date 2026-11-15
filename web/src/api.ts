// Client for the FuelUp API.
// Security model:
//  • The access token (10 min) lives only in memory — never in localStorage/sessionStorage, so an injected script cannot lift a stored token.
//  • The refresh token is an HttpOnly, Secure, SameSite=Strict cookie set by the server; JavaScript cannot read it.
//  • Refresh calls carry the X-FuelUp-Client header, which a cross-site page cannot send (CSRF guard).
//  • Every permission is enforced by the server; `can()` below only hides buttons for convenience.

export type TripStatus = 'WaitingLoad' | 'Loaded' | 'EnRoute' | 'Arrived' | 'Unloading' | 'AwaitingApproval' | 'Approved' | 'UnderReview' | 'Cancelled'

export interface Snapshot { driverName: string; plate: string; customerName: string; project?: string; site: string; stationName: string; meterSerial?: string }
export interface Delivery { id: string; seq: number; site: string; arrivalLocation?: string; meterBefore?: number; meterAfter?: number; received?: number; receiptNo?: string; submittedAt?: string; sealsBroken: boolean }
export interface Trip {
  id: string; no: string; status: TripStatus; createdAt: string; driverId: string; vehicleId?: string; product: string; qtyRequested: number; sourceRef: string
  reviewReason?: string; resolution?: string; approvedAt?: string; snapshot?: Snapshot
  loading?: { qty: number; docNo: string; at: string; meterStart?: number; meterEnd?: number; odometer?: number }
  deliveries: Delivery[]; handovers: { toDriverId: string; ack: string; note: string }[]
}
export interface Reconciliation { loaded?: number; loadMeterQty?: number; delivered?: number; receipt?: number; unsettled?: number; meterVsReceipt?: number; tolerance: number }
export interface TripView { trip: Trip; reconciliation: Reconciliation; statusLabel: string }
export interface Paged<T> { items: T[]; total: number; page: number; size: number }
export interface Me { id: string; username: string; displayName: string; role: string; driverId?: string; permissions: Record<string, 'None' | 'View' | 'Edit'> }

export class ApiError extends Error { constructor(public status: number, message: string) { super(message) } }

let accessToken: string | null = null
let refreshing: Promise<boolean> | null = null
const CLIENT = { 'X-FuelUp-Client': 'web' }

/** One refresh at a time; if another tab rotated the cookie a moment ago, retry once with the new cookie. */
function refresh(): Promise<boolean> {
  refreshing ??= (async () => {
    for (let attempt = 0; attempt < 2; attempt++) {
      const r = await fetch('/api/auth/refresh', { method: 'POST', credentials: 'same-origin', headers: CLIENT })
      if (r.ok) { accessToken = (await r.json()).accessToken; return true }
      await new Promise(res => setTimeout(res, 800))
    }
    accessToken = null
    return false
  })().finally(() => { refreshing = null })
  return refreshing
}

async function raw(path: string, init: RequestInit = {}, retry = true): Promise<Response> {
  if (!accessToken && retry) await refresh()
  const res = await fetch(path, {
    ...init, credentials: 'same-origin',
    headers: { 'content-type': 'application/json', ...(accessToken ? { authorization: `Bearer ${accessToken}` } : {}), ...init.headers },
  })
  if (res.status === 401 && retry && await refresh()) return raw(path, init, false)
  return res
}

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await raw(path, init)
  if (!res.ok) {
    const body = await res.json().catch(() => ({}))
    throw new ApiError(res.status, body.error || 'تعذر تنفيذ الطلب.')
  }
  return res.status === 204 ? (undefined as T) : res.json()
}

export const post = <T>(path: string, body?: unknown) => api<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) })

export type LoginStep =
  | { kind: 'done' }
  | { kind: 'mfa'; mfaToken: string }
  | { kind: 'enroll'; mfaToken: string; secret: string; otpauthUri: string }

async function authPost(path: string, body: unknown) {
  const res = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body) })
  const json = await res.json().catch(() => ({}))
  if (!res.ok) throw new ApiError(res.status, json.error || 'تعذر الدخول.')
  return json
}

export async function login(username: string, password: string): Promise<LoginStep> {
  const b = await authPost('/api/auth/login', { username, password, client: 'web' })
  if (b.accessToken) { accessToken = b.accessToken; return { kind: 'done' } }
  if (b.mfaSetupRequired) return { kind: 'enroll', mfaToken: b.mfaToken, secret: b.secret, otpauthUri: b.otpauthUri }
  return { kind: 'mfa', mfaToken: b.mfaToken }
}

export async function verifyMfa(mfaToken: string, code: string) {
  accessToken = (await authPost('/api/auth/mfa', { mfaToken, code, client: 'web' })).accessToken
}

/** Returns the one-time recovery codes; they are shown once and never again. */
export async function enrollMfa(mfaToken: string, code: string): Promise<string[]> {
  const b = await authPost('/api/auth/mfa/enroll', { mfaToken, code, client: 'web' })
  accessToken = b.tokens.accessToken
  return b.recoveryCodes
}

export async function logout() {
  await raw('/api/auth/logout', { method: 'POST' }, false).catch(() => undefined)
  accessToken = null
}

/** Downloads a server-generated file (Excel) with the current token. */
export async function download(path: string, filename: string) {
  const res = await raw(path)
  if (!res.ok) throw new ApiError(res.status, 'تعذر التنزيل.')
  const url = URL.createObjectURL(await res.blob())
  const a = Object.assign(document.createElement('a'), { href: url, download: filename })
  a.click(); URL.revokeObjectURL(url)
}

/** UI hint only. The server re-checks every permission on every request. */
export const can = (me: Me | undefined, section: string, level: 'View' | 'Edit' = 'View') => {
  const l = me?.permissions?.[section]
  return level === 'View' ? l === 'View' || l === 'Edit' : l === 'Edit'
}

export const fmt = (n?: number | null) => (n == null ? '—' : Math.round(n).toLocaleString('en-US'))
export const signed = (n?: number | null) => (n == null ? '—' : (n > 0 ? '+' : '') + Math.round(n).toLocaleString('en-US'))
export const ksa = (iso?: string) => (iso ? new Intl.DateTimeFormat('en-GB', { timeZone: 'Asia/Riyadh', dateStyle: 'short', timeStyle: 'short' }).format(new Date(iso)) : '—')
