import type { Lead } from "./types";
import { findIdentity, gradeFor, normalizePhone, uid } from "./domain";

const aliases: Record<string, string[]> = {
  buyerId: ["buyer id", "buyerid", "买家id", "买家编号", "客户id"],
  name: ["name", "姓名", "客户名", "客户名称", "buyer name"],
  nickname: ["nickname", "昵称", "buyer nickname"],
  phone: ["phone", "mobile", "tel", "电话", "手机号", "whatsapp"],
  email: ["email", "邮箱", "邮件"],
  company: ["company", "公司", "企业"],
  country: ["country", "国家", "地区"],
  productInterest: ["product", "interest", "产品", "采购产品", "产品兴趣"],
  stage: ["stage", "阶段"],
  owner: ["owner", "负责人"],
  notes: ["notes", "备注", "说明"]
};

const keyFor = (header: string) => {
  const normalized = header.trim().toLowerCase().replace(/[_-]+/g, " ");
  return Object.entries(aliases).find(([, values]) => values.includes(normalized))?.[0];
};

export async function readRows(file: File): Promise<Record<string, unknown>[]> {
  const XLSX = await import("xlsx");
  const data = await file.arrayBuffer();
  const workbook = XLSX.read(data, { type: "array" });
  const sheet = workbook.Sheets[workbook.SheetNames[0]];
  return XLSX.utils.sheet_to_json<Record<string, unknown>>(sheet, { defval: "" });
}

export function rowsToLeads(rows: Record<string, unknown>[], existing: Lead[]) {
  const next = [...existing];
  for (const row of rows) {
    const mapped: Record<string, string> = {};
    const customFields: Record<string, string> = {};
    for (const [header, raw] of Object.entries(row)) {
      const value = String(raw ?? "").trim();
      const key = keyFor(header);
      if (key) mapped[key] = value;
      else customFields[header] = value;
    }
    const found = findIdentity(next, mapped.buyerId || "", mapped.phone || "");
    const lead: Lead = {
      id: found?.id || uid(),
      buyerId: mapped.buyerId || found?.buyerId || "",
      name: mapped.name || found?.name || mapped.nickname || "未命名客户",
      nickname: mapped.nickname || found?.nickname || "",
      phone: normalizePhone(mapped.phone || found?.phone || ""),
      email: mapped.email || found?.email || "",
      company: mapped.company || found?.company || "",
      country: mapped.country || found?.country || "",
      productInterest: mapped.productInterest || found?.productInterest || "",
      stage: mapped.stage || found?.stage || "新客户",
      grade: found?.grade || gradeFor(0),
      score: found?.score || 0,
      owner: mapped.owner || found?.owner || "",
      tags: found?.tags || [],
      notes: mapped.notes || found?.notes || "",
      source: fileSource(row),
      updatedAt: new Date().toISOString(),
      customFields: { ...(found?.customFields || {}), ...customFields },
      aiSummary: found?.aiSummary,
      aiNextAction: found?.aiNextAction,
      aiRisks: found?.aiRisks,
      lastContactAt: found?.lastContactAt
    };
    const index = next.findIndex(x => x.id === lead.id);
    if (index >= 0) next[index] = lead;
    else next.push(lead);
  }
  return next;
}

const fileSource = (row: Record<string, unknown>) => String(row.Source || row.source || row.来源 || "文件导入");
