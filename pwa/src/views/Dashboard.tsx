import { ArrowRight, BrainCircuit, Clock3, ContactRound, Megaphone, Sparkles } from "lucide-react";
import { buildBrain } from "../domain";
import { useStore } from "../store";
import type { PageKey } from "../components/Shell";
import { Card, EmptyState, GradeBadge, PageHeader } from "../components/ui";

export function Dashboard({ navigate }: { navigate: (page: PageKey) => void }) {
  const { leads, touches, outreach, loadDemo } = useStore();
  const priority = [...leads].sort((a, b) => b.score - a.score).slice(0, 5);
  const due = outreach.filter(x => x.status === "pending").length;
  return <>
    <PageHeader title="今天应该做什么？" subtitle="把客户资料、商机判断和人工触达压缩成清晰的今日行动。"
      actions={<button className="button primary" onClick={() => navigate("intelligence")}><Sparkles size={17}/>开始分析</button>}/>
    <section className="hero-band">
      <div><h2>{leads.length ? `优先推进 ${priority[0]?.name || "重点客户"}` : "先建立你的客户工作区"}</h2>
        <p>{leads.length ? (priority[0]?.aiNextAction || "检查客户资料，确认今天最值得执行的下一步。") : "导入 Excel / CSV，或加载示例数据体验完整的本地 AI 销售工作流。"}</p></div>
      <button onClick={() => navigate(leads.length ? "analytics" : "customers")}>{leads.length ? "打开 Customer Brain" : "导入客户"}<ArrowRight size={18}/></button>
    </section>
    <section className="metrics">
      <Metric icon={<ContactRound/>} label="全部客户" value={leads.length} note="当前浏览器工作区"/>
      <Metric icon={<BrainCircuit/>} label="优先商机 A / B" value={leads.filter(x => x.grade === "A" || x.grade === "B").length} note="值得优先人工推进" accent/>
      <Metric icon={<Clock3/>} label="24 小时内有互动" value={leads.filter(x => x.lastContactAt && Date.now() - new Date(x.lastContactAt).getTime() < 86400000).length} note="人工记录的触达"/>
      <Metric icon={<Megaphone/>} label="待执行触达" value={due} note="需要人工确认"/>
    </section>
    <section className="dashboard-grid">
      <Card className="priority-list">
        <div className="section-title"><div><h2>今日行动简报</h2><p>优先级来自已保存的 AI 分析和客户资料</p></div><button className="text-button" onClick={() => navigate("customers")}>查看全部</button></div>
        {!leads.length ? <EmptyState title="还没有客户" body="加载示例数据可立即体验，也可以进入客户列表导入自己的文件。" action={<button className="button secondary" onClick={loadDemo}>加载示例数据</button>}/> :
          priority.map((lead, index) => <button className="priority-row" key={lead.id} onClick={() => navigate("analytics")}>
            <span className="rank">{String(index + 1).padStart(2, "0")}</span>
            <div className="priority-person"><strong>{lead.nickname || lead.name}</strong><span>{lead.company || lead.productInterest || "待补充客户资料"}</span></div>
            <div className="priority-action">{lead.aiNextAction || "补充资料并确认下一步"}</div>
            <GradeBadge grade={lead.grade} score={lead.score}/>
          </button>)}
      </Card>
      <Card className="brain-preview">
        <div className="brain-heading"><span><BrainCircuit/></span><div><h2>Customer Brain</h2><p>跨 CRM、触达与知识的客户大脑</p></div></div>
        {priority[0] ? <BrainSummary lead={priority[0]} touches={touches}/> :
          <EmptyState title="等待客户上下文" body="选择或导入客户后，这里会显示资料覆盖、事实、缺口和下一步。"/>}
      </Card>
    </section>
  </>;
}

function Metric({ icon, label, value, note, accent }: { icon: React.ReactNode; label: string; value: number; note: string; accent?: boolean }) {
  return <Card className={`metric ${accent ? "accent" : ""}`}><div className="metric-icon">{icon}</div><span>{label}</span><strong>{value}</strong><small>{note}</small></Card>;
}

function BrainSummary({ lead, touches }: { lead: ReturnType<typeof useStore>["leads"][number]; touches: ReturnType<typeof useStore>["touches"] }) {
  const brain = buildBrain(lead, touches);
  return <div className="brain-summary">
    <div className="coverage"><div><strong>{brain.coverage}%</strong><span>资料覆盖</span></div><progress value={brain.coverage} max="100"/></div>
    <p className="brain-copy">{brain.summary}</p>
    <div className="brain-facts">{brain.facts.slice(0, 3).map(fact => <span key={fact}>{fact}</span>)}</div>
    <div className="next-action"><span>建议下一步</span><strong>{brain.nextAction}</strong></div>
  </div>;
}
