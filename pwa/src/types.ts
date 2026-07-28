export type Grade = "A" | "B" | "C" | "D";
export type Channel = "whatsapp" | "email";
export type Direction = "incoming" | "outgoing";

export interface Lead {
  id: string;
  importKey?: string;
  buyerId: string;
  name: string;
  nickname: string;
  phone: string;
  email: string;
  company: string;
  country: string;
  productInterest: string;
  stage: string;
  grade: Grade;
  score: number;
  owner: string;
  tags: string[];
  notes: string;
  source: string;
  updatedAt: string;
  lastContactAt?: string;
  aiSummary?: string;
  aiNextAction?: string;
  aiRisks?: string[];
  customFields: Record<string, string>;
}

export interface Touch {
  id: string;
  leadId: string;
  channel: Channel;
  direction: Direction;
  subject?: string;
  body: string;
  timestamp: string;
  status: "received" | "opened" | "confirmed-sent";
}

export interface KnowledgeDocument {
  id: string;
  name: string;
  category: string;
  text: string;
  enabled: boolean;
  createdAt: string;
}

export interface OutreachItem {
  id: string;
  leadId: string;
  channel: Channel;
  subject?: string;
  body: string;
  status: "pending" | "opened" | "confirmed-sent" | "skipped";
  createdAt: string;
}

export interface AiSettings {
  baseUrl: string;
  model: string;
  reasoning: string;
}

export interface BrainProfile {
  coverage: number;
  summary: string;
  risks: string[];
  nextAction: string;
  facts: string[];
  gaps: string[];
}
