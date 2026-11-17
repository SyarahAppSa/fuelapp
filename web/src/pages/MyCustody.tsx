// «عهدتي»: what a driver holds now and the hand-overs waiting for his acceptance.
import { useQuery } from '@tanstack/react-query'
import { api, ksa, post } from '../api'
import { Panel, useAction } from '../ui'
import type { Transfer } from '../custody'

interface Mine { parts: { id: string; internalNo: string; serial?: string; name: string; holderSince?: string; condition?: string }[]; quantities: { catalogId: string; name: string; unit?: string; qty: number; since: string }[]; pending: Transfer[] }

export default function MyCustody() {
  const q = useQuery({ queryKey: ['custody', 'my'], queryFn: () => api<Mine>('/api/custody/my') })
  const act = useAction()
  const m = q.data
  return (
    <>
      <h1>عهدتي</h1>
      {act.banner}
      <Panel title={`بانتظار تأكيد استلامك (${m?.pending.length ?? 0})`}>
        {m?.pending.map(t => (
          <div key={t.id} className="spread" style={{ padding: 12, borderBottom: '1px solid var(--line)' }}>
            <div><b>{t.item}</b> {t.partNo ? <span className="num">{t.partNo}</span> : <span className="num">× {t.qty}</span>}<div className="sub">من {t.fromName} · {ksa(t.requestedAt)}{t.handoverBy ? ` · سلّمها ${t.handoverBy}` : ''}</div></div>
            <div className="row">
              <button className="btn ok" disabled={act.busy} onClick={() => act.run(() => post(`/api/custody/transfers/${t.id}/accept`, { note: 'استلمت' }), 'أكدت الاستلام.')}>استلمت</button>
              <button className="btn" disabled={act.busy} onClick={() => { const r = prompt('سبب رفض الاستلام'); if (r) act.run(() => post(`/api/custody/transfers/${t.id}/reject`, { reason: r }), 'رفضت الاستلام.') }}>رفض</button>
            </div>
          </div>))}
        {m?.pending.length === 0 && <div className="sub" style={{ padding: 16 }}>لا شيء بانتظارك.</div>}
      </Panel>
      <Panel title="القطع في عهدتي">
        <div className="tblw"><table><thead><tr><th>رقم القطعة</th><th>الصنف</th><th>الرقم التسلسلي</th><th>منذ</th><th>الحالة</th></tr></thead>
          <tbody>{m?.parts.map(p => <tr key={p.id}><td className="num">{p.internalNo}</td><td>{p.name}</td><td className="num">{p.serial ?? '—'}</td><td className="num">{ksa(p.holderSince)}</td><td>{p.condition}</td></tr>)}
            {m?.quantities.map(x => <tr key={x.catalogId}><td>—</td><td>{x.name}</td><td className="num">× {x.qty} {x.unit}</td><td className="num">{ksa(x.since)}</td><td>كمية</td></tr>)}</tbody></table></div>
      </Panel>
    </>
  )
}
