// Custody & truck spare parts: types, data hooks and small helpers shared by the custody pages.
import { useQuery } from '@tanstack/react-query'
import qrcode from 'qrcode-generator'
import { api, upload } from './api'

export type PartStatus = 'Stock' | 'Issued' | 'Installed' | 'Repair' | 'ReturnedToSupplier' | 'Damaged' | 'Lost' | 'Scrapped'
export type HolderKind = 'driver' | 'employee'

export interface Meta {
  statuses: { value: PartStatus; label: string }[]; moves: Record<string, string>; fates: Record<string, string>
  issueReasons: string[]; categories: string[]; tyrePositions: string[]; workOrderTypes: string[]
  canEdit: boolean; canSeeCost: boolean; canExport: boolean
}
export interface Warehouse { id: string; name: string; location?: string; keeper?: string; isActive: boolean }
export interface Employee { id: string; name: string; role?: string; empNo?: string; phone?: string; isActive: boolean }
export interface Holder { kind: HolderKind; id: string; name: string; role: string; empNo?: string; parts: number; items: number }
export interface CatalogItem {
  id: string; code: string; name: string; tracking: 'Individual' | 'Quantity'; category: string; brand?: string; model?: string; mfrPartNo?: string; unit?: string
  minQty?: number; reorderQty?: number; inspectEveryDays?: number; unitCost?: number; specs?: string; isActive: boolean; inStock: number; belowMin: boolean
}
export interface Part {
  id: string; internalNo: string; serial?: string; catalogId: string; code?: string; name?: string; category?: string; tracking?: string; brand?: string; model?: string; mfrPartNo?: string
  status: PartStatus; statusLabel: string; statusReason?: string; location: string; warehouseId?: string; warehouse?: string; bin?: string; vehicleId?: string; plate?: string; position?: string
  installedAt?: string; installKm?: number; installHours?: number; holderKind?: HolderKind; holderId?: string; holder?: string; holderType?: string; holderSince?: string
  supplier?: string; invoiceNo?: string; purchaseDate?: string; cost?: number; warrantyStart?: string; warrantyEnd?: string; warrantyTerms?: string; warranty: string
  batchNo?: string; specs?: string; expiryDate?: string; lastInspection?: string; nextInspection?: string; condition?: string; notes?: string; meterId?: string
  repairVendor?: string; repairSentAt?: string; repairExpectedAt?: string; repairCost?: number; claimRef?: string; claimOpenedAt?: string; claimResult?: string
  lastLocation?: string; reviewOutcome?: string; createdAt: string; pendingTransfer?: string
}
export interface Movement {
  id: string; type: string; label: string; partId?: string; partNo?: string; catalogId: string; item: string; code: string; qty: number; statusBefore?: string; statusAfter?: string
  from: string; to: string; plate: string; position?: string; workOrderId?: string; workOrder: string; reason?: string; fate?: string; relatedPartId?: string; relatedNo?: string
  km?: number; hours?: number; cost?: number; vendor?: string; docRef?: string; note?: string; at: string; by: string; approvedBy: string
}
export interface Transfer {
  id: string; no: string; kind: string; kindLabel: string; partId?: string; partNo?: string; catalogId: string; item: string; code: string; qty: number
  fromKind: string; fromType: string; fromName: string; toKind: string; toType: string; toName: string; workOrder: string; reason?: string
  status: 'Pending' | 'Accepted' | 'Rejected'; statusLabel: string; condition?: string; handoverBy?: string; handoverNote?: string; requestedAt: string; requestedBy: string
  decidedAt?: string; decidedBy?: string; decisionNote?: string
}
export interface WorkOrder {
  id: string; no: string; vehicleId: string; plate: string; type: string; description: string; workshop?: string; km?: number; closed: boolean; openedAt: string; openedBy: string
  closedAt?: string; closedBy?: string; closeNote?: string; driver?: string; operations: number; laborCost?: number; partsCost?: number; total?: number
}
export interface Report { key: string; title: string; subtitle: string; tables: { title: string; columns: string[]; rows: (string | number | null)[][] }[]; generatedAt: string }

export const statusChip: Record<PartStatus, string> = {
  Stock: 'st-WaitingLoad', Issued: 'st-EnRoute', Installed: 'st-Approved', Repair: 'st-AwaitingApproval', ReturnedToSupplier: 'st-Cancelled', Damaged: 'st-UnderReview', Lost: 'st-UnderReview', Scrapped: 'st-Cancelled',
}
export const holderLabel = (k?: string) => (k === 'driver' ? 'سائق' : k === 'employee' ? 'موظف' : k === 'warehouse' ? 'مستودع' : k === 'vehicle' ? 'شاحنة' : '')

const opt = { staleTime: 30_000 }
export const useMeta = () => useQuery({ queryKey: ['custody', 'meta'], queryFn: () => api<Meta>('/api/custody/meta'), staleTime: 300_000 })
export const useWarehouses = () => useQuery({ queryKey: ['custody', 'warehouses'], queryFn: () => api<Warehouse[]>('/api/custody/warehouses'), ...opt })
export const useEmployees = () => useQuery({ queryKey: ['custody', 'employees'], queryFn: () => api<Employee[]>('/api/custody/employees'), ...opt })
export const useHolders = () => useQuery({ queryKey: ['custody', 'holders'], queryFn: () => api<Holder[]>('/api/custody/holders'), ...opt })
export const useCatalog = () => useQuery({ queryKey: ['custody', 'catalog'], queryFn: () => api<CatalogItem[]>('/api/custody/catalog'), ...opt })
export const useWorkOrders = (open?: boolean) => useQuery({ queryKey: ['custody', 'workorders', open], queryFn: () => api<WorkOrder[]>(`/api/custody/workorders${open == null ? '' : `?open=${open}`}`), ...opt })
export const useParts = (qs: string) => useQuery({ queryKey: ['custody', 'parts', qs], queryFn: () => api<Part[]>(`/api/custody/parts?${qs}`), ...opt })

/** QR image (data: URL) for a part's internal number; scanners type the number back into the search box. */
export function qrDataUrl(text: string, cell = 4) {
  const q = qrcode(0, 'M'); q.addData(text); q.make()
  return q.createDataURL(cell, 1)
}

/** Uploads a photo or PDF for a part / work order / hand-over (same checks as every other attachment on the server). */
export function uploadAttachment(ownerType: 'part' | 'workorder' | 'transfer', ownerId: string, kind: string, file: File) {
  const fd = new FormData()
  fd.append('file', file); fd.append('ownerType', ownerType); fd.append('ownerId', ownerId); fd.append('kind', kind)
  return upload<{ id: string }>('/api/attachments', fd)
}

export const attachmentKinds: [string, string][] = [['part-photo', 'صورة القطعة'], ['invoice', 'فاتورة'], ['receipt-voucher', 'سند استلام'], ['work-order', 'أمر صيانة'], ['handover', 'محضر تسليم موقّع'], ['inspection', 'تقرير فحص'], ['other', 'أخرى']]

export const num = (v: FormDataEntryValue | null) => (v == null || String(v).trim() === '' ? undefined : Number(v))
export const str = (v: FormDataEntryValue | null) => (v == null || String(v).trim() === '' ? undefined : String(v).trim())
export const day = (iso?: string) => (iso ? iso.slice(0, 10) : '—')
export const money = (n?: number | null) => (n == null ? '—' : n.toLocaleString('en-US', { maximumFractionDigits: 2 }))

export interface CVehicle { id: string; plate: string; internalNo?: string; make?: string; model?: string; year?: number; meterId?: string; driverId?: string; driver?: string; installed: number; openWorkOrders: number }
export interface CMeter { id: string; serial: string; kind?: string; plate?: string; lastCalibration?: string; nextCalibration?: string }
export const useCVehicles = () => useQuery({ queryKey: ['custody', 'vehicles'], queryFn: () => api<CVehicle[]>('/api/custody/vehicles'), ...opt })
export const useCMeters = () => useQuery({ queryKey: ['custody', 'meters'], queryFn: () => api<CMeter[]>('/api/custody/meters'), ...opt })
export const useTransfers = (status?: string) => useQuery({ queryKey: ['custody', 'transfers', status], queryFn: () => api<Transfer[]>(`/api/custody/transfers${status ? `?status=${status}` : ''}`), ...opt })

/** "driver:<id>" ↔ { holderKind, holderId } for <select> values. */
export const holderValue = (h: { kind: string; id: string }) => `${h.kind}:${h.id}`
export function parseHolder(v: FormDataEntryValue | null) {
  const s = String(v ?? '')
  const i = s.indexOf(':')
  return i < 0 ? { holderKind: undefined, holderId: undefined } : { holderKind: s.slice(0, i), holderId: s.slice(i + 1) }
}

/** Reads the fate group of a form into the API's FateInput. */
export function fateOf(f: FormData) {
  const s = (k: string) => { const v = f.get(k); return v == null || String(v).trim() === '' ? undefined : String(v).trim() }
  return {
    fate: s('fate'), warehouseId: s('fateWarehouseId'), bin: s('fateBin'), vendor: s('vendor'), expectedReturn: s('expectedReturn'),
    estimatedCost: s('estimatedCost') ? Number(s('estimatedCost')) : undefined, claimRef: s('claimRef'), scrapApproval: s('scrapApproval'),
  }
}
