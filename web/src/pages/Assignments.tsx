import { useQuery } from '@tanstack/react-query'
import { api, can, ksa, post } from '../api'
import { useMe } from '../App'
import { Panel, useAction, useCustomers, useDrivers, useVehicles } from '../ui'

interface VA { id: string; vehicleId: string; driverId: string; from: string; to?: string }
interface CA { id: string; customerId: string; driverId: string; from: string; to?: string }

// The selected value is read at the moment of the change: React resets a controlled <select> before the request runs.
/** Who drives which vehicle, and the default driver of each customer (orders go to him automatically). */
export default function Assignments() {
  const { data: me } = useMe()
  const edit = can(me, 'Assign', 'Edit')
  const act = useAction()
  const vehicles = useVehicles(), drivers = useDrivers(), customers = useCustomers()
  const va = useQuery({ queryKey: ['assign', 'vehicles'], queryFn: () => api<VA[]>('/api/assignments/vehicles') })
  const ca = useQuery({ queryKey: ['assign', 'customers'], queryFn: () => api<CA[]>('/api/assignments/customers') })
  const name = (id?: string) => drivers.data?.find(d => d.id === id)?.name ?? '—'
  const openV = (vid: string) => va.data?.find(a => a.vehicleId === vid && !a.to)
  const openC = (cid: string) => ca.data?.find(a => a.customerId === cid && !a.to)

  return (
    <>
      <h1>الإسناد</h1>
      {act.banner}
      <Panel title="المركبات والسائقون">
        <div className="tblw"><table><thead><tr><th>المركبة</th><th>السائق الحالي</th><th>منذ</th>{edit && <th>تغيير</th>}</tr></thead>
          <tbody>{vehicles.data?.map(v => {
            const cur = openV(v.id)
            return (
              <tr key={v.id}><td className="num"><b>{v.plate}</b><div className="sub">{v.internalNo}</div></td><td>{name(cur?.driverId)}</td><td className="num">{ksa(cur?.from)}</td>
                {edit && <td><select aria-label={`سائق المركبة ${v.plate}`} value={cur?.driverId ?? ''} disabled={act.busy}
                  onChange={e => { const driverId = e.target.value || null; act.run(() => post(`/api/assignments/vehicles/${v.id}`, { driverId }), `تم تحديث إسناد ${v.plate}.`) }}>
                  <option value="">— بدون سائق —</option>{drivers.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></td>}
              </tr>)
          })}</tbody></table></div>
      </Panel>
      <Panel title="السائق الافتراضي لكل عميل">
        <div className="tblw"><table><thead><tr><th>العميل</th><th>الموقع</th><th>السائق الافتراضي</th>{edit && <th>تغيير</th>}</tr></thead>
          <tbody>{customers.data?.map(c => {
            const cur = openC(c.id)
            return (
              <tr key={c.id}><td>{c.name}</td><td>{c.site}</td><td>{name(cur?.driverId)}</td>
                {edit && <td><select aria-label={`السائق الافتراضي لـ ${c.name}`} value={cur?.driverId ?? ''} disabled={act.busy}
                  onChange={e => { const driverId = e.target.value || null; act.run(() => post(`/api/assignments/customers/${c.id}`, { driverId }), `تم تحديث السائق الافتراضي لـ ${c.name}.`) }}>
                  <option value="">— بدون —</option>{drivers.data?.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select></td>}
              </tr>)
          })}</tbody></table></div>
      </Panel>
    </>
  )
}
