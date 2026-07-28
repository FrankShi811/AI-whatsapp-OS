import { openDB, type DBSchema } from "idb";
import type { AiSettings, KnowledgeDocument, Lead, OutreachItem, Touch } from "./types";

interface SalesDb extends DBSchema {
  leads: { key: string; value: Lead; indexes: { "by-buyer": string; "by-phone": string; "by-updated": string } };
  touches: { key: string; value: Touch; indexes: { "by-lead": string; "by-time": string } };
  knowledge: { key: string; value: KnowledgeDocument; indexes: { "by-created": string } };
  outreach: { key: string; value: OutreachItem; indexes: { "by-status": string; "by-lead": string } };
  settings: { key: string; value: unknown };
}

export const dbPromise = openDB<SalesDb>("ai-sales-os-pwa", 1, {
  upgrade(db) {
    const leads = db.createObjectStore("leads", { keyPath: "id" });
    leads.createIndex("by-buyer", "buyerId");
    leads.createIndex("by-phone", "phone");
    leads.createIndex("by-updated", "updatedAt");
    const touches = db.createObjectStore("touches", { keyPath: "id" });
    touches.createIndex("by-lead", "leadId");
    touches.createIndex("by-time", "timestamp");
    const knowledge = db.createObjectStore("knowledge", { keyPath: "id" });
    knowledge.createIndex("by-created", "createdAt");
    const outreach = db.createObjectStore("outreach", { keyPath: "id" });
    outreach.createIndex("by-status", "status");
    outreach.createIndex("by-lead", "leadId");
    db.createObjectStore("settings");
  }
});

export const storage = {
  leads: async () => (await dbPromise).getAll("leads"),
  saveLead: async (lead: Lead) => (await dbPromise).put("leads", lead),
  deleteLead: async (id: string) => (await dbPromise).delete("leads", id),
  touches: async () => (await dbPromise).getAll("touches"),
  saveTouch: async (touch: Touch) => (await dbPromise).put("touches", touch),
  knowledge: async () => (await dbPromise).getAll("knowledge"),
  saveKnowledge: async (doc: KnowledgeDocument) => (await dbPromise).put("knowledge", doc),
  deleteKnowledge: async (id: string) => (await dbPromise).delete("knowledge", id),
  outreach: async () => (await dbPromise).getAll("outreach"),
  saveOutreach: async (item: OutreachItem) => (await dbPromise).put("outreach", item),
  settings: async () => (await dbPromise).get("settings", "ai") as Promise<AiSettings | undefined>,
  saveSettings: async (value: AiSettings) => (await dbPromise).put("settings", value, "ai"),
  snapshot: async () => ({
    format: "ai-sales-os-pwa-backup",
    version: 1,
    exportedAt: new Date().toISOString(),
    leads: await (await dbPromise).getAll("leads"),
    touches: await (await dbPromise).getAll("touches"),
    knowledge: await (await dbPromise).getAll("knowledge"),
    outreach: await (await dbPromise).getAll("outreach"),
    settings: await (await dbPromise).get("settings", "ai")
  }),
  restore: async (value: {
    format: string;
    leads?: Lead[];
    touches?: Touch[];
    knowledge?: KnowledgeDocument[];
    outreach?: OutreachItem[];
    settings?: AiSettings;
  }) => {
    if (value.format !== "ai-sales-os-pwa-backup") throw new Error("不是有效的 AI Sales OS PWA 备份文件。");
    const db = await dbPromise;
    const tx = db.transaction(["leads", "touches", "knowledge", "outreach", "settings"], "readwrite");
    await Promise.all([
      ...((value.leads || []).map(item => tx.objectStore("leads").put(item))),
      ...((value.touches || []).map(item => tx.objectStore("touches").put(item))),
      ...((value.knowledge || []).map(item => tx.objectStore("knowledge").put(item))),
      ...((value.outreach || []).map(item => tx.objectStore("outreach").put(item))),
      value.settings ? tx.objectStore("settings").put(value.settings, "ai") : Promise.resolve()
    ]);
    await tx.done;
  },
  clear: async () => {
    const db = await dbPromise;
    await Promise.all([
      db.clear("leads"),
      db.clear("touches"),
      db.clear("knowledge"),
      db.clear("outreach"),
      db.clear("settings")
    ]);
  }
};
