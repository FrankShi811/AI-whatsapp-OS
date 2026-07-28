import { useEffect, useState } from "react";
import { Shell, type PageKey } from "./components/Shell";
import { Modal, Button } from "./components/ui";
import { Analytics } from "./views/Analytics";
import { Campaigns } from "./views/Campaigns";
import { Customers } from "./views/Customers";
import { Dashboard } from "./views/Dashboard";
import { Intelligence } from "./views/Intelligence";
import { Knowledge } from "./views/Knowledge";
import { Outreach } from "./views/Outreach";
import { Settings } from "./views/Settings";

interface InstallPromptEvent extends Event {
  prompt: () => Promise<void>;
  userChoice: Promise<{ outcome: "accepted" | "dismissed" }>;
}

const pages: PageKey[] = ["dashboard","intelligence","customers","whatsapp","email","campaigns","knowledge","analytics","settings"];

export default function App() {
  const initial = location.hash.replace("#/", "") as PageKey;
  const [page, setPageState] = useState<PageKey>(pages.includes(initial) ? initial : "dashboard");
  const [installPrompt, setInstallPrompt] = useState<InstallPromptEvent | null>(null);
  const [guide, setGuide] = useState(() => localStorage.getItem("ai-sales-os-pwa-guide") !== "done");

  useEffect(() => {
    const onPrompt = (event: Event) => { event.preventDefault(); setInstallPrompt(event as InstallPromptEvent); };
    window.addEventListener("beforeinstallprompt", onPrompt);
    return () => window.removeEventListener("beforeinstallprompt", onPrompt);
  }, []);
  useEffect(() => {
    const onHash = () => {
      const next = location.hash.replace("#/", "") as PageKey;
      if (pages.includes(next)) setPageState(next);
    };
    window.addEventListener("hashchange", onHash);
    return () => window.removeEventListener("hashchange", onHash);
  }, []);

  const setPage = (next: PageKey) => {
    setPageState(next);
    history.replaceState(null, "", `#/${next}`);
    window.scrollTo({ top: 0, behavior: "smooth" });
  };
  const install = async () => {
    if (!installPrompt) return;
    await installPrompt.prompt();
    await installPrompt.userChoice;
    setInstallPrompt(null);
  };

  return <Shell page={page} setPage={setPage} canInstall={Boolean(installPrompt)} onInstall={() => void install()}>
    {page === "dashboard" && <Dashboard navigate={setPage}/>}
    {page === "intelligence" && <Intelligence/>}
    {page === "customers" && <Customers/>}
    {page === "whatsapp" && <Outreach channel="whatsapp"/>}
    {page === "email" && <Outreach channel="email"/>}
    {page === "campaigns" && <Campaigns/>}
    {page === "knowledge" && <Knowledge/>}
    {page === "analytics" && <Analytics/>}
    {page === "settings" && <Settings/>}
    {guide && <Modal title="欢迎使用 AI Sales OS PWA" onClose={() => { localStorage.setItem("ai-sales-os-pwa-guide", "done"); setGuide(false); }} wide>
      <div className="guide-grid">
        <div><span>01</span><strong>先建立客户资料</strong><p>导入 Excel / CSV，Buyer ID 优先作为跨板块统一身份，缺失时使用电话号码。</p></div>
        <div><span>02</span><strong>再让 AI 形成判断</strong><p>配置自己的 AI Provider 后，可生成商机分析、风险和可编辑的 WhatsApp / 邮件草稿。</p></div>
        <div><span>03</span><strong>由你确认执行</strong><p>PWA 会打开 WhatsApp 或邮件客户端，但不会伪装后台同步或无人值守自动发送。</p></div>
      </div>
      <div className="guide-boundary"><strong>数据默认留在本机浏览器</strong><span>建议定期在“API 与数据设置”中导出完整备份。</span></div>
      <div className="modal-actions"><Button variant="secondary" onClick={() => setPage("settings")}>查看设置与能力边界</Button><Button onClick={() => { localStorage.setItem("ai-sales-os-pwa-guide", "done"); setGuide(false); }}>开始使用</Button></div>
    </Modal>}
  </Shell>;
}
