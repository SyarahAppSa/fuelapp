import { useQuery } from '@tanstack/react-query'
import { api } from './api'

// Shared types and labels for customer orders (طلبات العملاء), the dispatch board and fleet documents.
export type OrderStatus = 'New' | 'Planned' | 'InProgress' | 'Delivered' | 'Closed' | 'Cancelled'
export type OrderType = 'Transport' | 'TransportAndSupply'
export type Priority = 'Scheduled' | 'Urgent'

export interface Totals { requested: number; planned: number; loaded: number; delivered: number; receipt: number; remaining: number; toDispatch: number; difference: number; tolerance: number; differenceFlag: boolean; trips: number; openTrips: number; late: boolean }
export interface OrderRow {
  id: string; no: string; customerId: string; customerName: string; site: string; customerRef?: string; type: OrderType; typeLabel: string; product: string; qtyRequested: number
  priority: Priority; priorityLabel: string; status: OrderStatus; statusLabel: string; statusReason?: string; requestedFrom?: string; requestedTo?: string; createdAt: string; notes?: string
  totals: Totals; money: { transportRatePerLiter?: number; fuelPricePerLiter?: number } | null
}
export interface HubTrip {
  id: string; no: string; status: string; statusLabel: string; driver?: string; plate?: string; trailerPlate?: string; station?: string; sourceRef: string; loadingDocNo?: string
  qtyRequested: number; loaded?: number; delivered?: number; receipt?: number; receiptNo?: string; difference?: number; tolerance: number
  createdAt: string; loadedAt?: string; deliveredAt?: string; approvedAt?: string; fillReport: { id: string; no: string; status: string } | null
}
export interface History { at: string; action: string; detail?: string; oldValue?: string; newValue?: string; by?: string }
export interface Hub {
  order: OrderRow; createdBy?: string; trips: HubTrip[]; history: History[]
  photos: { reportId: string; reportNo: string; tripNo: string; items: { id: string; kind: string; label: string }[] } | null
  money: { transportRatePerLiter?: number; fuelPricePerLiter?: number; ratePerLiter: number; revenueEstimate: number; orderValue: number } | null
}

export const orderChip: Record<OrderStatus, string> = { New: 'st-WaitingLoad', Planned: 'st-Loaded', InProgress: 'st-EnRoute', Delivered: 'st-Approved', Closed: 'st-Closed', Cancelled: 'st-Cancelled' }
export const orderStatuses: [OrderStatus, string][] = [['New', 'جديد'], ['Planned', 'مخطط'], ['InProgress', 'قيد التنفيذ'], ['Delivered', 'تم التسليم'], ['Closed', 'مغلق'], ['Cancelled', 'ملغى']]
export const typeLabels: Record<OrderType, string> = { Transport: 'نقل فقط', TransportAndSupply: 'نقل وتوريد' }
export const priorityLabels: Record<Priority, string> = { Scheduled: 'مجدول', Urgent: 'عاجل' }
export const fillChip: Record<string, string> = { Draft: 'st-WaitingLoad', Submitted: 'st-AwaitingApproval', NeedsReview: 'st-UnderReview', Returned: 'st-Cancelled', Approved: 'st-Approved' }
export const fillLabel: Record<string, string> = { Draft: 'قيد التوثيق', Submitted: 'مُرسل', NeedsReview: 'بحاجة إلى مراجعة', Returned: 'أُعيد للسائق', Approved: 'معتمد' }

export const sar = (n?: number | null) => (n == null ? '—' : n.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }))
export const dateOnly = (iso?: string | null) => (iso ? new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Riyadh' }).format(new Date(iso)) : '—')
/** A <input type="datetime-local"> value (Saudi time) from an ISO time, and back. */
export const toLocalInput = (iso?: string | null) => (iso ? new Date(new Date(iso).getTime() + 3 * 3600_000).toISOString().slice(0, 16) : '')
export const fromLocalInput = (v?: unknown) => (v ? `${String(v)}:00+03:00` : undefined)

// ── fleet documents ──
export type DocStatus = 'valid' | 'soon' | 'expired' | 'nodate'
export interface DocField { key: 'number' | 'category' | 'issuer' | 'issueDate' | 'expiry' | 'image'; label: string; required: boolean }
export interface DocType { id: string; code: string; name: string; appliesTo: 'vehicle' | 'driver'; fields: DocField[]; mandatoryForDispatch: boolean; isActive: boolean; sortOrder: number }
export interface DocRow {
  id: string; typeId: string; typeName: string; mandatory: boolean; ownerKind: 'vehicle' | 'driver'; ownerId: string; ownerName: string; number?: string; category?: string; issuer?: string
  issueDate?: string; expiry?: string; daysLeft?: number; status: DocStatus; statusLabel: string; version: number; hasCopy: boolean; updatedAt: string
}
export interface DocMeta {
  types: DocType[]; offsets: number[]; warnDays: number; fleetManagerUserId?: string; users: { id: string; displayName: string; username: string; role: string }[] | null
  canEdit: boolean; canSeeCopies: boolean; isAdmin: boolean; statuses: { value: DocStatus; label: string }[]
}
export const docChip: Record<DocStatus, string> = { valid: 'st-Approved', soon: 'st-AwaitingApproval', expired: 'st-UnderReview', nodate: 'st-WaitingLoad' }
export const fieldKeys: [DocField['key'], string][] = [['number', 'الرقم'], ['category', 'الفئة'], ['issuer', 'الجهة / الشركة'], ['issueDate', 'تاريخ الإصدار / البداية'], ['expiry', 'تاريخ الانتهاء'], ['image', 'صورة / نسخة']]

export const useDocMeta = () => useQuery({ queryKey: ['documents', 'meta'], queryFn: () => api<DocMeta>('/api/documents/meta') })
