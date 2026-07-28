import type { Lead } from "./types";
import { gradeFor, normalizeBuyer, normalizePhone, uid } from "./domain";

export type ImportFile = Pick<File, "name" | "size" | "arrayBuffer">;

export interface ImportSheet {
  name: string;
  headers: string[];
  rows: Record<string, unknown>[];
}

export interface ParsedImport {
  fileName: string;
  preferredSheetName: string;
  sheets: ImportSheet[];
}

export interface ImportContext {
  fileName: string;
  sheetName: string;
}

export interface ImportResult {
  changedLeads: Lead[];
  removedPlaceholderIds: string[];
  total: number;
  created: number;
  updated: number;
}

type CoreField =
  | "buyerId"
  | "name"
  | "nickname"
  | "phone"
  | "email"
  | "company"
  | "country"
  | "productInterest"
  | "stage"
  | "owner"
  | "tags"
  | "notes"
  | "source";

const MAX_FILE_BYTES = 200 * 1024 * 1024;
const MAX_SHEET_CELLS = 5_000_000;

const aliases: Record<CoreField, string[]> = {
  buyerId: ["buyerid", "buyeridentifier", "buyeraccountid", "dhgatebuyerid", "customerid", "客户id", "买家id", "采购商id", "买家编号", "客户编号"],
  name: ["name", "fullname", "contactname", "buyername", "buyernickname", "姓名", "联系人", "客户姓名", "客户名", "客户名称", "买家姓名", "买家昵称"],
  nickname: ["nickname", "昵称"],
  phone: ["whatsapp", "whatsappnumber", "whatsapp号码", "phone", "mobile", "tel", "电话", "电话号码", "手机号", "手机", "联系电话", "号码"],
  email: ["email", "emailaddress", "mail", "邮箱", "电子邮箱"],
  company: ["company", "companyname", "business", "organization", "公司", "公司名称", "企业", "企业名称"],
  country: ["country", "market", "region", "国家", "国家地区", "市场", "地区"],
  productInterest: ["productinterest", "interestedproduct", "product", "sku", "一级品类偏好", "品类偏好", "产品兴趣", "意向产品", "采购产品", "产品", "询盘产品"],
  stage: ["stage", "leadstage", "status", "阶段", "商机阶段", "跟进阶段", "状态"],
  owner: ["owner", "现owner", "currentowner", "assignee", "salesowner", "负责人", "销售负责人", "跟进人"],
  tags: ["tags", "tag", "labels", "标签", "客户标签"],
  notes: ["notes", "note", "remark", "comments", "备注", "说明"],
  source: ["source", "leadsource", "channel", "来源", "线索来源", "渠道"]
};

const normalizeHeader = (value: string) =>
  value.trim().toLowerCase().replace(/[\s_\-./\\()（）[\]【】]+/g, "");

const aliasLookup = new Map(
  Object.entries(aliases).flatMap(([field, values]) =>
    values.map(value => [normalizeHeader(value), field as CoreField] as const)
  )
);

export const keyForHeader = (header: string): CoreField | undefined => {
  const normalized = normalizeHeader(header);
  const exact = aliasLookup.get(normalized);
  if (exact) return exact;

  for (const segment of header.split(/[/|｜:：]/)) {
    const match = aliasLookup.get(normalizeHeader(segment));
    if (match) return match;
  }

  return [...aliasLookup.entries()]
    .filter(([alias]) => alias.length >= 3 && normalized.startsWith(alias))
    .sort(([left], [right]) => right.length - left.length)[0]?.[1];
};

const uniqueHeaders = (values: unknown[]) => {
  const counts = new Map<string, number>();
  return values.map((value, index) => {
    const base = String(value ?? "").trim() || `Column ${index + 1}`;
    const count = (counts.get(base.toLowerCase()) || 0) + 1;
    counts.set(base.toLowerCase(), count);
    return count === 1 ? base : `${base} (${count})`;
  });
};

const isBlankRow = (values: unknown[]) =>
  values.every(value => value === null || value === undefined || String(value).trim() === "");

const workbookActiveSheet = (workbook: {
  Workbook?: { WBView?: Array<{ activeTab?: number | string }>; Sheets?: Array<{ Hidden?: number }> };
}) => {
  const raw = workbook.Workbook?.WBView?.[0]?.activeTab;
  const activeTab = typeof raw === "string" ? Number.parseInt(raw, 10) : raw;
  return Number.isInteger(activeTab) ? Number(activeTab) : 0;
};

export function selectPreferredSheetName(
  sheetNames: string[],
  parsedSheets: ImportSheet[],
  activeTab: number,
  workbookSheets: Array<{ Hidden?: number }> = []
) {
  const available = new Set(parsedSheets.map(sheet => sheet.name));
  const activeName = sheetNames[activeTab];
  if (activeName && available.has(activeName)) return activeName;
  return sheetNames.find((name, index) => available.has(name) && workbookSheets[index]?.Hidden !== 1)
    || parsedSheets[0]?.name
    || "";
}

export async function readWorkbook(file: ImportFile): Promise<ParsedImport> {
  if (!file.size) throw new Error("导入文件为空。");
  if (file.size > MAX_FILE_BYTES) throw new Error("文件超过 200MB 资源保护上限。");
  if (!/\.(xlsx|xls|csv)$/i.test(file.name)) throw new Error("仅支持 Excel 或 CSV 文件。");

  const XLSX = await import("xlsx");
  const workbook = XLSX.read(await file.arrayBuffer(), {
    type: "array",
    cellDates: false,
    cellNF: true,
    cellText: true
  });
  const sheets: ImportSheet[] = [];

  for (const name of workbook.SheetNames) {
    const worksheet = workbook.Sheets[name];
    const rawMatrix = XLSX.utils.sheet_to_json<unknown[]>(worksheet, {
      header: 1,
      defval: "",
      raw: true,
      blankrows: false
    });
    const displayMatrix = XLSX.utils.sheet_to_json<unknown[]>(worksheet, {
      header: 1,
      defval: "",
      raw: false,
      blankrows: false
    });
    const rowPairs = rawMatrix
      .map((raw, index) => ({ raw, display: displayMatrix[index] || [] }))
      .filter(pair => !isBlankRow(pair.raw) || !isBlankRow(pair.display));
    if (!rowPairs.length) continue;

    const headers = uniqueHeaders(rowPairs[0].display);
    const dataPairs = rowPairs.slice(1);
    if ((longestRowLength(rowPairs) * Math.max(1, dataPairs.length)) > MAX_SHEET_CELLS) {
      throw new Error(`工作表“${name}”超过 ${MAX_SHEET_CELLS.toLocaleString()} 个单元格资源保护上限。`);
    }
    const rows = dataPairs.map(({ raw, display }) =>
      Object.fromEntries(headers.map((header, column) => {
        const rawValue = raw[column];
        const displayValue = display[column];
        const value = keyForHeader(header) === "phone" && typeof rawValue === "number"
          ? rawValue.toFixed(0)
          : String(displayValue ?? rawValue ?? "").trim();
        return [header, value];
      }))
    );
    sheets.push({ name, headers, rows });
  }

  if (!sheets.length) throw new Error("文件中没有非空工作表或数据行。");
  return {
    fileName: file.name,
    preferredSheetName: selectPreferredSheetName(
      workbook.SheetNames,
      sheets,
      workbookActiveSheet(workbook),
      workbook.Workbook?.Sheets || []
    ),
    sheets
  };
}

const longestRowLength = (rows: Array<{ raw: unknown[]; display: unknown[] }>) =>
  Math.max(1, ...rows.map(row => Math.max(row.raw.length, row.display.length)));

const cleanValue = (value: unknown) => {
  const text = String(value ?? "").trim();
  return /^(#n\/a|n\/a|null|undefined)$/i.test(text) ? "" : text;
};

const cleanEmail = (value: unknown) => {
  const email = cleanValue(value);
  return /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email) ? email : "";
};

const importKeyFor = (context: ImportContext, rowIndex: number) =>
  `${context.fileName.trim().toLowerCase()}::${context.sheetName.trim().toLowerCase()}::${rowIndex + 2}`;

const isInvalidPlaceholder = (lead: Lead) =>
  lead.name === "未命名客户"
  && !lead.buyerId
  && !lead.nickname
  && !lead.phone
  && !lead.email
  && !lead.company
  && !lead.productInterest
  && lead.source === "文件导入";

export function rowsToLeads(rows: Record<string, unknown>[], existing: Lead[], context: ImportContext): ImportResult {
  const removedPlaceholderIds = existing.filter(isInvalidPlaceholder).map(lead => lead.id);
  const removedPlaceholderSet = new Set(removedPlaceholderIds);
  const retained = existing.filter(lead => !removedPlaceholderSet.has(lead.id));
  const next = [...retained];
  const original = [...retained];
  const nextIndexById = new Map(next.map((lead, index) => [lead.id, index]));
  const byBuyerId = new Map(
    next.filter(lead => normalizeBuyer(lead.buyerId)).map(lead => [normalizeBuyer(lead.buyerId), lead])
  );
  const byImportKey = new Map(next.filter(lead => lead.importKey).map(lead => [lead.importKey!, lead]));
  const originalByPhone = new Map<string, Lead[]>();
  for (const lead of original) {
    const phone = normalizePhone(lead.phone);
    if (!phone) continue;
    const matches = originalByPhone.get(phone) || [];
    matches.push(lead);
    originalByPhone.set(phone, matches);
  }
  const claimedExistingIds = new Set<string>();
  const changed = new Map<string, Lead>();
  const createdIds = new Set<string>();
  const updatedIds = new Set<string>();
  const timestamp = new Date().toISOString();

  rows.forEach((row, rowIndex) => {
    const mapped: Partial<Record<CoreField, string>> = {};
    const customFields: Record<string, string> = {};
    for (const [header, raw] of Object.entries(row)) {
      const value = cleanValue(raw);
      customFields[header] = value;
      const key = keyForHeader(header);
      if (key && mapped[key] === undefined) mapped[key] = value;
    }

    const buyerId = cleanValue(mapped.buyerId);
    const importKey = importKeyFor(context, rowIndex);
    const phone = normalizePhone(cleanValue(mapped.phone));
    const buyerMatch = buyerId ? byBuyerId.get(normalizeBuyer(buyerId)) : undefined;
    const importMatch = !buyerMatch ? byImportKey.get(importKey) : undefined;
    const phoneCandidates = !buyerMatch && !importMatch && phone
      ? (originalByPhone.get(phone) || []).filter(lead => !claimedExistingIds.has(lead.id))
      : [];
    const found = buyerMatch || importMatch || (phoneCandidates.length === 1 ? phoneCandidates[0] : undefined);
    if (found) claimedExistingIds.add(found.id);

    const fallbackName = Object.values(customFields)
      .find(value => value.length > 0 && value.length <= 160) || `导入行 ${rowIndex + 2}`;
    const name = cleanValue(mapped.name) || cleanValue(mapped.nickname) || found?.name || fallbackName;
    const lead: Lead = {
      id: found?.id || uid(),
      importKey,
      buyerId: buyerId || found?.buyerId || "",
      name,
      nickname: cleanValue(mapped.nickname) || found?.nickname || "",
      phone: phone || found?.phone || "",
      email: cleanEmail(mapped.email) || found?.email || "",
      company: cleanValue(mapped.company) || found?.company || "",
      country: cleanValue(mapped.country) || found?.country || "",
      productInterest: cleanValue(mapped.productInterest) || found?.productInterest || "",
      stage: cleanValue(mapped.stage) || found?.stage || "新客户",
      grade: found?.grade || gradeFor(0),
      score: found?.score || 0,
      owner: cleanValue(mapped.owner) || found?.owner || "",
      tags: cleanValue(mapped.tags)
        ? cleanValue(mapped.tags).split(/[,，;；|]/).map(value => value.trim()).filter(Boolean)
        : found?.tags || [],
      notes: cleanValue(mapped.notes) || found?.notes || "",
      source: cleanValue(mapped.source) || `${context.fileName} · ${context.sheetName}`,
      updatedAt: timestamp,
      customFields: { ...(found?.customFields || {}), ...customFields },
      aiSummary: found?.aiSummary,
      aiNextAction: found?.aiNextAction,
      aiRisks: found?.aiRisks,
      lastContactAt: found?.lastContactAt
    };
    const index = nextIndexById.get(lead.id);
    if (index !== undefined) {
      next[index] = lead;
      if (!createdIds.has(lead.id)) updatedIds.add(lead.id);
    } else {
      next.push(lead);
      nextIndexById.set(lead.id, next.length - 1);
      createdIds.add(lead.id);
    }
    if (lead.buyerId) byBuyerId.set(normalizeBuyer(lead.buyerId), lead);
    byImportKey.set(importKey, lead);
    changed.set(lead.id, lead);
  });

  return {
    changedLeads: [...changed.values()],
    removedPlaceholderIds,
    total: rows.length,
    created: createdIds.size,
    updated: updatedIds.size
  };
}
