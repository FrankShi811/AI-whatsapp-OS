import { describe, expect, it } from "vitest";
import * as XLSX from "xlsx";
import { readWorkbook, rowsToLeads, selectPreferredSheetName, type ImportFile, type ImportSheet } from "./importers";
import type { Lead } from "./types";

const actualHeaders = [
  "buyer_nickname\n累计GMV≥10w，且近一年GMV≥10000\n（标绿底为之前联系但已退出TOP圈）",
  "客户姓名",
  "现Owner",
  "电话",
  "邮箱",
  "国家/邮箱\n（可参考Y-AA列具体城市）",
  "一级品类偏好\n（可参考T列判断是否DZY买家）",
  "跟进阶段\n（每周三更新/必填）",
  "buyer_id"
];

const fileFromBytes = (name: string, bytes: Uint8Array): ImportFile => {
  const blob = new Blob([bytes as BlobPart]);
  return { name, size: blob.size, arrayBuffer: () => blob.arrayBuffer() };
};

const existingLead = (overrides: Partial<Lead> = {}): Lead => ({
  id: "existing",
  buyerId: "",
  name: "Existing",
  nickname: "",
  phone: "",
  email: "",
  company: "",
  country: "",
  productInterest: "",
  stage: "新客户",
  grade: "D",
  score: 0,
  owner: "",
  tags: [],
  notes: "",
  source: "test",
  updatedAt: "2026-07-28T00:00:00.000Z",
  customFields: {},
  ...overrides
});

describe("workbook parsing", () => {
  it("selects the saved active sheet instead of the first non-empty sheet", () => {
    const sheets: ImportSheet[] = [
      { name: "Q2目标", headers: ["AM"], rows: Array.from({ length: 10 }, (_, index) => ({ AM: `Owner ${index}` })) },
      { name: "客户总表（Sherry3）", headers: actualHeaders, rows: Array.from({ length: 100 }, () => ({})) }
    ];
    expect(selectPreferredSheetName(
      ["Q2目标", "客户总表（Sherry3）"],
      sheets,
      1,
      [{ Hidden: 1 }, { Hidden: 0 }]
    )).toBe("客户总表（Sherry3）");
  });

  it("preserves numeric phone digits and parses long annotated headers", async () => {
    const workbook = XLSX.utils.book_new();
    const sheet = XLSX.utils.aoa_to_sheet([
      actualHeaders,
      ["mitchells", "", "Frank", 447999000000, "mitchells@example.com", "UK", "表", "初步建联", "buyer-001"]
    ]);
    sheet.D2.z = "0.00E+00";
    XLSX.utils.book_append_sheet(workbook, sheet, "客户总表");
    const bytes = XLSX.write(workbook, { type: "array", bookType: "xlsx" });
    const parsed = await readWorkbook(fileFromBytes("SP社群项目表.xlsx", bytes));
    expect(parsed.sheets[0].rows).toHaveLength(1);
    expect(parsed.sheets[0].rows[0]["电话"]).toBe("447999000000");

    const result = rowsToLeads(parsed.sheets[0].rows, [], {
      fileName: parsed.fileName,
      sheetName: parsed.sheets[0].name
    });
    expect(result.changedLeads[0]).toMatchObject({
      buyerId: "buyer-001",
      name: "mitchells",
      phone: "+447999000000",
      email: "mitchells@example.com",
      country: "UK",
      productInterest: "表",
      stage: "初步建联",
      owner: "Frank"
    });
    expect(Object.keys(result.changedLeads[0].customFields)).toHaveLength(actualHeaders.length);
  });
});

describe("customer import identity", () => {
  it("imports every row while keeping repeated-phone rows separate without Buyer ID", () => {
    const rows = [
      { buyer_nickname: "same-one", 电话: "14155550103" },
      { buyer_nickname: "same-two", 电话: "14155550103" }
    ];
    const result = rowsToLeads(rows, [], { fileName: "repeat.xlsx", sheetName: "customers" });
    expect(result).toMatchObject({ total: 2, created: 2, updated: 0 });
    expect(new Set(result.changedLeads.map(lead => lead.id)).size).toBe(2);
  });

  it("reimports the same source row idempotently", () => {
    const context = { fileName: "repeat.xlsx", sheetName: "customers" };
    const first = rowsToLeads([{ buyer_nickname: "same-one", 电话: "14155550103" }], [], context);
    const second = rowsToLeads([{ buyer_nickname: "same-one-updated", 电话: "14155550103" }], first.changedLeads, context);
    expect(second).toMatchObject({ total: 1, created: 0, updated: 1 });
    expect(second.changedLeads[0].id).toBe(first.changedLeads[0].id);
    expect(second.changedLeads[0].name).toBe("same-one-updated");
  });

  it("removes only legacy empty import placeholders during a valid import", () => {
    const placeholder = existingLead({ id: "bad", name: "未命名客户", source: "文件导入" });
    const legitimate = existingLead({ id: "good", name: "Unnamed but edited", source: "PWA 人工创建" });
    const result = rowsToLeads(
      [{ buyer_nickname: "valid", buyer_id: "buyer-100" }],
      [placeholder, legitimate],
      { fileName: "SP社群项目表.xlsx", sheetName: "客户总表（Sherry3）" }
    );
    expect(result.removedPlaceholderIds).toEqual(["bad"]);
    expect(result.changedLeads).toHaveLength(1);
    expect(result.changedLeads[0].name).toBe("valid");
  });
});
