// Documents of a part or a work order: part photo, invoice, receipt voucher, work order, signed hand-over.
import { useState, type FormEvent } from 'react'
import { blobUrl } from '../api'
import { Field, Panel, useAction } from '../ui'
import { attachmentKinds, uploadAttachment } from '../custody'

export default function Attachments({ ownerType, ownerId, files, canEdit }: { ownerType: 'part' | 'workorder'; ownerId: string; files: { id: string; kind: string; fileName: string }[]; canEdit: boolean }) {
  const act = useAction()
  const [file, setFile] = useState<File | null>(null)
  const label = (k: string) => attachmentKinds.find(x => x[0] === k)?.[1] ?? k
  async function open(id: string) {
    const url = await blobUrl(`/api/attachments/${id}`)
    window.open(url, '_blank', 'noopener')
    setTimeout(() => URL.revokeObjectURL(url), 60_000)
  }
  function send(e: FormEvent<HTMLFormElement>) {
    e.preventDefault()
    const kind = String(new FormData(e.currentTarget).get('kind'))
    if (file) act.run(() => uploadAttachment(ownerType, ownerId, kind, file), 'رُفع الملف.')
  }
  return (
    <Panel title={`المرفقات (${files.length})`}>
      {act.banner}
      <div className="row" style={{ padding: 12 }}>
        {files.map(f => <button key={f.id} className="btn" onClick={() => open(f.id).catch(() => undefined)}>📎 {label(f.kind)}{f.fileName ? ` · ${f.fileName}` : ''}</button>)}
        {files.length === 0 && <span className="sub">لا توجد مرفقات.</span>}
      </div>
      {canEdit && <form className="filter noprint" style={{ boxShadow: 'none', margin: 0 }} onSubmit={send}>
        <div className="fg">
          <Field label="نوع المستند"><select name="kind">{attachmentKinds.map(([k, l]) => <option key={k} value={k}>{l}</option>)}</select></Field>
          <Field label="الملف (صورة أو PDF حتى 10 ميجابايت)"><input type="file" accept="image/jpeg,image/png,image/webp,image/heic,application/pdf" onChange={e => setFile(e.target.files?.[0] ?? null)} required /></Field>
        </div>
        <div className="actions"><button className="btn pri" disabled={act.busy || !file}>رفع</button></div>
      </form>}
    </Panel>
  )
}
