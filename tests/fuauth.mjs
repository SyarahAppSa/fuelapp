// Test helper: signs in demo accounts, completing two-step verification (RFC 6238 TOTP) like an authenticator app would.
// Secrets of enrolled test accounts are kept in a local JSON file so later runs can sign in again.
import { createHmac } from 'node:crypto';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';

export const B = process.env.API || 'http://127.0.0.1:5004';
export const PASS = process.env.DEMO_PASSWORD || 'FuelUp@2026';
const STORE = process.env.MFA_STORE || new URL('./.mfa-secrets.json', import.meta.url).pathname;
const state = existsSync(STORE) ? JSON.parse(readFileSync(STORE, 'utf8')) : {};
const save = () => writeFileSync(STORE, JSON.stringify(state, null, 1));

// The API limits sign-in calls per IP per minute. Tests stay under the limit (AUTH_BUDGET) by pausing when needed.
const authTimes = [];
const AUTH_BUDGET = Number(process.env.AUTH_BUDGET || 15);
export async function authPace() {
  const cut = () => { while (authTimes.length && Date.now() - authTimes[0] > 61000) authTimes.shift() };
  cut();
  if (authTimes.length >= AUTH_BUDGET) { await new Promise(r => setTimeout(r, 61000 - (Date.now() - authTimes[0]))); cut(); }
  authTimes.push(Date.now());
}
const limited = (p) => p.startsWith('/api/auth/') && !p.startsWith('/api/auth/logout') || p === '/api/me/password' || p.startsWith('/api/me/mfa/c') || p.startsWith('/api/me/mfa/r') || p.startsWith('/api/me/mfa/d');

export async function call(tok, method, path, body, headers = {}) {
  if (limited(path)) await authPace();
  const r = await fetch(B + path, { method, headers: { 'content-type': 'application/json', ...(tok ? { authorization: 'Bearer ' + tok } : {}), ...headers }, body: body ? JSON.stringify(body) : undefined });
  const t = await r.text(); let j; try { j = JSON.parse(t) } catch { j = t }
  return { s: r.status, j, h: r.headers };
}

function base32(s) {
  const a = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'; let bits = '';
  for (const c of s.replace(/=+$/, '').toUpperCase()) bits += a.indexOf(c).toString(2).padStart(5, '0');
  return Buffer.from(bits.match(/.{8}/g).map(b => parseInt(b, 2)));
}
export function totp(secret, step) {
  const msg = Buffer.alloc(8); msg.writeBigUInt64BE(BigInt(step));
  const h = createHmac('sha1', base32(secret)).update(msg).digest();
  const o = h[h.length - 1] & 15;
  return String(((h.readUInt32BE(o) & 0x7fffffff) % 1e6)).padStart(6, '0');
}
const now = () => Math.floor(Date.now() / 30000);

// A code is accepted once per 30 s step (replay protection) within ±1 step: pick the next unused step, waiting if needed.
export async function nextCode(user) {
  const s = state[user]; let target = Math.max(now(), (s.last ?? 0) + 1);
  while (target > now() + 1) await new Promise(r => setTimeout(r, 1000));
  s.last = target; save();
  return totp(s.secret, target);
}

export async function login(username, password = PASS, client = 'mobile') {
  const r = await call(null, 'POST', '/api/auth/login', { username, password, client });
  if (r.s !== 200) return { ...r, token: null };
  if (r.j.accessToken) return { ...r, token: r.j.accessToken, refresh: r.j.refreshToken };
  if (r.j.mfaSetupRequired) {
    state[username] = { secret: r.j.secret, last: 0 };
    const e = await call(null, 'POST', '/api/auth/mfa/enroll', { mfaToken: r.j.mfaToken, code: await nextCode(username), client });
    state[username].recovery = e.j.recoveryCodes; save();
    return { ...e, token: e.j.tokens?.accessToken, refresh: e.j.tokens?.refreshToken, enrolled: true };
  }
  const v = await call(null, 'POST', '/api/auth/mfa', { mfaToken: r.j.mfaToken, code: await nextCode(username), client });
  return { ...v, token: v.j.accessToken, refresh: v.j.refreshToken };
}

export const token = async (u) => (await login(u)).token;
export const secretOf = (u) => state[u];
export function resetStore() { for (const k of Object.keys(state)) delete state[k]; save(); }
