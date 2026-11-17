// Printable QR labels for parts (A4 sheet). The QR holds the internal number; a USB or phone scanner types it into the search box.
import { useQueries } from '@tanstack/react-query'
import { useNavigate, useSearchParams } from 'react-router-dom'
import { api } from '../api'
import { qrDataUrl, type Part } from '../custody'

export default function PartLabels() {
  const [sp] = useSearchParams()
  const nav = useNavigate()
  const ids = (sp.get('ids') ?? '').split(',').filter(Boolean).slice(0, 80)
  const qs = useQueries({ queries: ids.map(id => ({ queryKey: ['custody', 'part', id], queryFn: () => api<{ part: Part }>(`/api/custody/parts/${id}`) })) })
  const parts = qs.map(q => q.data?.part).filter((p): p is Part => !!p)
  return (
    <>
      <div className="row noprint"><button className="btn" onClick={() => nav(-1)}>← رجوع</button><button className="btn pri" onClick={() => window.print()}>طباعة الملصقات ({parts.length})</button></div>
      <div className="labels">
        {parts.map(p => (
          <div className="label" key={p.id}>
            <img src={qrDataUrl(p.internalNo, 3)} alt={`QR ${p.internalNo}`} />
            <div><b className="num">{p.internalNo}</b><div>{p.name}</div><div className="num sub">{p.serial ?? ''}</div><div className="sub">{p.brand ?? ''} {p.model ?? ''}</div></div>
          </div>))}
      </div>
    </>
  )
}
