// Small shared pieces for the operations pages.
import { useState, type ReactNode } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'

export interface Station { id: string; name: string; type: 'Station' | 'MaysanYard' | 'Aramco'; city?: string }
export interface Vehicle { id: string; plate: string; internalNo?: string; make?: string; model?: string; year?: number; capacity: number; meterId?: string }
export interface Driver { id: string; name: string; empNo?: string; phone?: string; type: 'Aramco' | 'SmallTruck' }
export interface Customer { id: string; name: string; project?: string; site: string; recipient?: string }
export interface Meter { id: string; serial: string; kind?: string; vehicleId?: string }

export const stationTypes = { Station: 'محطة', MaysanYard: 'حوش ميسان', Aramco: 'أرامكو' } as const
export const driverTypes = { Aramco: 'سائق أرامكو', SmallTruck: 'سائق شاحنة صغيرة' } as const

export const useStations = () => useQuery({ queryKey: ['master', 'stations'], queryFn: () => api<Station[]>('/api/master/stations') })
export const useVehicles = () => useQuery({ queryKey: ['master', 'vehicles'], queryFn: () => api<Vehicle[]>('/api/master/vehicles') })
export const useDrivers = () => useQuery({ queryKey: ['master', 'drivers'], queryFn: () => api<Driver[]>('/api/master/drivers') })
export const useCustomers = () => useQuery({ queryKey: ['master', 'customers'], queryFn: () => api<Customer[]>('/api/master/customers') })
export const useMeters = () => useQuery({ queryKey: ['master', 'meters'], queryFn: () => api<Meter[]>('/api/master/meters') })

/** Runs a server call and shows its outcome (success text, or the server's Arabic error) in one place. */
export function useAction() {
  const qc = useQueryClient()
  const [msg, setMsg] = useState<{ ok: boolean; text: string } | null>(null)
  const m = useMutation({
    mutationFn: async ({ run }: { run: () => Promise<unknown>; ok: string }) => run(),
    onSuccess: (_d, v) => { setMsg({ ok: true, text: v.ok }); qc.invalidateQueries() },
    onError: (e) => setMsg({ ok: false, text: (e as Error).message }),
  })
  const banner = msg && <div className={msg.ok ? 'okmsg' : 'err'} role="status" style={{ margin: '10px 0' }}>{msg.text}</div>
  return { run: (run: () => Promise<unknown>, ok: string) => m.mutate({ run, ok }), busy: m.isPending, banner, clear: () => setMsg(null) }
}

export function Field({ label, children }: { label: string; children: ReactNode }) {
  return <label className="fl">{label}{children}</label>
}

export function Panel({ title, actions, children }: { title: string; actions?: ReactNode; children: ReactNode }) {
  return (
    <section className="panel" style={{ marginTop: 14 }}>
      <div className="hd spread"><h3>{title}</h3>{actions}</div>
      {children}
    </section>
  )
}

/** Reads a form into a plain object; empty strings become undefined, numeric fields become numbers. */
export function formData(form: HTMLFormElement, numeric: string[] = []) {
  const out: Record<string, unknown> = {}
  new FormData(form).forEach((v, k) => {
    const s = String(v).trim()
    if (s === '') return
    out[k] = numeric.includes(k) ? Number(s) : s
  })
  return out
}
