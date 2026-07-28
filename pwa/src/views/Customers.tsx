import { Download, FileUp, Plus, Search, Trash2 } from "lucide-react";
import { useMemo, useRef, useState } from "react";
import { uid } from "../domain";
import { readRows, rowsToLeads } from "../importers";
import { useStore } from "../store";
import type { Lead } from "../types";
import { Button, EmptyState, Field, GradeBadge, Modal, PageHeader } from "../components/ui";

const emptyLead = (): Lead => ({
  id: uid(), buyerId: "", name: "", nickname: "", phone: "", email: "", company: "", country: "",
  productInterest: "", stage: "新客户", grade: "D", score: 0, owner: "", tags: [], notes: "", source: "PWA 人工创建",
  updatedAt: new Date().toISOString(), customFields: {}
});

export function Customers() {
  const { leads, saveLead, removeLead, loadDemo } = useStore();
  const [query, setQuery] = useState("");
  const [editing, setEditing] = useState<Lead | null>(null);
  const [notice, setNotice] = useState("");
  const fileRef = useRef<HTMLInputElement>(null);
  const filtered = useMemo(() => leads.filter(lead => JSON.stringify(lead).toLowerCase().includes(query.toLowerCase())), [leads, query]);

  const importFile = async (file?: File) => {
    if (!file) return;
    try {
      const rows = await readRows(file);
      const next = rowsToLeads(rows, leads);
      for (const lead of next) await saveLead(lead);
      setNotice(`已读取 ${rows.length} 行，当前共 ${next.length} 位客户。Buyer ID 优先，缺失时按电话号码更新。`);
    } catch (error) { setNotice(error instanceof Error ? error.message : "文件读取失败"); }
  };
  const exportData = () => {
    const blob = new Blob([JSON.stringify(leads, null, 2)], { type: "application/json" });
    const url = URL.createObjectURL(blob); const a = document.createElement("a");
    a.href = url; a.download = `ai-sales-os-customers-${new Date().toISOString().slice(0, 10)}.json`; a.click(); URL.revokeObjectURL(url);
  };
  return <>
    <PageHeader title="客户列表" subtitle="Buyer ID 优先识别同一客户；资料只保存在当前浏览器。"
      actions={<><Button variant="secondary" onClick={exportData}><Download size={16}/>导出</Button><Button variant="secondary" onClick={() => fileRef.current?.click()}><FileUp size={16}/>导入 Excel / CSV</Button><Button onClick={() => setEditing(emptyLead())}><Plus size={16}/>新建客户</Button></>}/>
    <input ref={fileRef} hidden type="file" accept=".xlsx,.xls,.csv" onChange={event => void importFile(event.target.files?.[0])}/>
    {notice && <div className="notice">{notice}<button onClick={() => setNotice("")}>×</button></div>}
    <div className="toolbar"><div className="search"><Search/><input value={query} onChange={e => setQuery(e.target.value)} placeholder="搜索姓名、Buyer ID、电话、邮箱或自定义字段"/></div><span>{filtered.length} 位客户</span></div>
    <section className="table-shell">
      {!leads.length ? <EmptyState title="建立统一客户档案" body="支持 Excel / CSV 动态字段导入；也可以先加载示例数据体验。" action={<Button variant="secondary" onClick={loadDemo}>加载示例数据</Button>}/> :
      <div className="data-table">
        <div className="table-row table-head"><span>客户</span><span>统一身份</span><span>公司 / 产品</span><span>阶段</span><span>AI 等级</span><span/></div>
        {filtered.map(lead => <button className="table-row" key={lead.id} onClick={() => setEditing({ ...lead })}>
          <span className="customer-cell"><strong>{lead.nickname || lead.name}</strong><small>{lead.email || lead.phone || "联系方式待补充"}</small></span>
          <span><strong>{lead.buyerId || "电话号码兜底"}</strong><small>{lead.buyerId ? "Buyer ID" : lead.phone}</small></span>
          <span><strong>{lead.company || "—"}</strong><small>{lead.productInterest || "产品待补充"}</small></span>
          <span>{lead.stage}</span><span><GradeBadge grade={lead.grade} score={lead.score}/></span>
          <span><button className="row-delete" onClick={event => { event.stopPropagation(); if (confirm(`删除 ${lead.name}？`)) void removeLead(lead.id); }}><Trash2 size={15}/></button></span>
        </button>)}
      </div>}
    </section>
    {editing && <LeadEditor lead={editing} onClose={() => setEditing(null)} onSave={async lead => { await saveLead({ ...lead, updatedAt: new Date().toISOString() }); setEditing(null); }}/>}
  </>;
}

function LeadEditor({ lead, onClose, onSave }: { lead: Lead; onClose: () => void; onSave: (lead: Lead) => Promise<void> }) {
  const [value, setValue] = useState(lead);
  const set = (key: keyof Lead, next: string) => setValue(current => ({ ...current, [key]: next }));
  return <Modal title={lead.name ? "编辑客户" : "新建客户"} onClose={onClose} wide>
    <div className="form-grid">
      <Field label="Buyer ID" hint="存在时作为跨板块统一身份"><input value={value.buyerId} onChange={e => set("buyerId", e.target.value)}/></Field>
      <Field label="客户名称"><input value={value.name} onChange={e => set("name", e.target.value)}/></Field>
      <Field label="Nickname"><input value={value.nickname} onChange={e => set("nickname", e.target.value)}/></Field>
      <Field label="WhatsApp / 电话"><input value={value.phone} onChange={e => set("phone", e.target.value)}/></Field>
      <Field label="邮箱"><input type="email" value={value.email} onChange={e => set("email", e.target.value)}/></Field>
      <Field label="公司"><input value={value.company} onChange={e => set("company", e.target.value)}/></Field>
      <Field label="国家 / 地区"><input value={value.country} onChange={e => set("country", e.target.value)}/></Field>
      <Field label="关注产品"><input value={value.productInterest} onChange={e => set("productInterest", e.target.value)}/></Field>
      <Field label="销售阶段"><select value={value.stage} onChange={e => set("stage", e.target.value)}>{["新客户","初步沟通","需求确认","报价中","谈判中","成交","复购","暂停"].map(x => <option key={x}>{x}</option>)}</select></Field>
      <Field label="负责人"><input value={value.owner} onChange={e => set("owner", e.target.value)}/></Field>
      <Field label="备注"><textarea value={value.notes} onChange={e => set("notes", e.target.value)} rows={4}/></Field>
    </div>
    <div className="modal-actions"><Button variant="secondary" onClick={onClose}>取消</Button><Button disabled={!value.name.trim()} onClick={() => void onSave(value)}>保存客户</Button></div>
  </Modal>;
}
