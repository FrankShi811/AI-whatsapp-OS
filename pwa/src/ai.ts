import type { AiSettings, BrainProfile, KnowledgeDocument, Lead, Touch } from "./types";
import { retrieveKnowledge } from "./domain";

const keyName = "ai-sales-os-pwa-api-key";
export const sessionKey = {
  get: () => sessionStorage.getItem(keyName) || "",
  set: (value: string) => value ? sessionStorage.setItem(keyName, value) : sessionStorage.removeItem(keyName)
};

function endpoint(baseUrl: string) {
  const base = baseUrl.trim().replace(/\/+$/, "");
  return base.endsWith("/chat/completions") ? base : `${base}/chat/completions`;
}

export async function runAi<T>(
  settings: AiSettings,
  apiKey: string,
  system: string,
  payload: unknown
): Promise<T> {
  if (!settings.baseUrl || !settings.model || !apiKey) throw new Error("请先在 API 对接中填写支持浏览器跨域的 Provider、模型和本次会话 API Key。");
  const response = await fetch(endpoint(settings.baseUrl), {
    method: "POST",
    headers: { "Content-Type": "application/json", Authorization: `Bearer ${apiKey}` },
    body: JSON.stringify({
      model: settings.model,
      temperature: 0.2,
      response_format: { type: "json_object" },
      messages: [
        { role: "system", content: system },
        { role: "user", content: JSON.stringify(payload) }
      ]
    })
  });
  if (!response.ok) throw new Error(`AI Provider 返回 ${response.status}。纯 PWA 还要求 Provider 允许浏览器跨域访问。`);
  const json = await response.json();
  const content = json?.choices?.[0]?.message?.content;
  if (!content) throw new Error("AI Provider 未返回可解析内容。");
  return JSON.parse(content.replace(/^```json\s*|\s*```$/g, "")) as T;
}

export async function analyzeLead(settings: AiSettings, apiKey: string, lead: Lead, touches: Touch[], knowledge: KnowledgeDocument[]) {
  const related = touches.filter(x => x.leadId === lead.id).slice(-60);
  const hits = retrieveKnowledge(knowledge, `${lead.productInterest} ${lead.notes} ${related.map(x => x.body).join(" ")}`);
  const result = await runAi<{ score: number; summary: string; risks: string[]; nextAction: string }>(
    settings,
    apiKey,
    "你是谨慎的B2B销售分析助手。只依据输入事实判断，不得虚构。输出严格JSON：score为0到100，summary为中文摘要，risks为字符串数组，nextAction为可执行下一步。",
    { lead, conversation: related, approvedKnowledge: hits.map(x => ({ name: x.doc.name, text: x.doc.text.slice(0, 4000) })) }
  );
  if (!Number.isFinite(result.score) || result.score < 0 || result.score > 100) throw new Error("AI 评分结构无效。");
  return result;
}

export async function draftMessage(
  settings: AiSettings,
  apiKey: string,
  lead: Lead,
  brain: BrainProfile,
  touches: Touch[],
  knowledge: KnowledgeDocument[],
  intent: string,
  channel: "WhatsApp" | "Email"
) {
  const related = touches.filter(x => x.leadId === lead.id).slice(-40);
  const hits = retrieveKnowledge(knowledge, `${intent} ${lead.productInterest} ${related.map(x => x.body).join(" ")}`);
  return runAi<{ subject?: string; body: string; risk: string }>(
    settings,
    apiKey,
    `你是谨慎的B2B销售写作助手。根据销售意图和客户真实上下文生成${channel}草稿。不得虚构价格、库存、交期、付款或政策。只生成草稿，绝不声称已发送。输出严格JSON：subject可选，body为正文，risk为中文风险提示。`,
    { intent, lead, brain, conversation: related, approvedKnowledge: hits.map(x => ({ name: x.doc.name, text: x.doc.text.slice(0, 3000) })) }
  );
}
