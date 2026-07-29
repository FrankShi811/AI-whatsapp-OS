# macOS and Windows v5.10.1 parity contract

This document is a release gate for the native Avalonia macOS build. The current
Windows WPF v5.10.1 implementation remains the product and behavior source of
truth. macOS uses the same `WAFlow.Core` services and local data model; it does
not depend on, publish to, or exchange data with the PWA.

| Area | Windows source of truth | Native macOS implementation | Gate |
|---|---|---|---|
| Window and shell | `MainWindow.xaml(.cs)` | 1440×900, maximized, 60→240 hover/focus sidebar, 72px top bar, unread badges | Static + UI smoke |
| Navigation and motion | `MainWindow.xaml.cs` | 8 modules, Settings dialog, 110ms exit/235ms entry, 240ms expand/220ms collapse, delayed labels | Static + UI smoke |
| Keyboard and guide | `MainWindow.xaml.cs`, `GuideCatalog.cs` | ⌘/Ctrl+K, ⌘/Ctrl+1…8, Esc, module guide state | Static + UI smoke |
| Appearance | `App.xaml`, `SettingsWindow` | Shared semantic light/dark tokens and immediate 80/90/100/110/125% scale | Static + UI smoke |
| Dashboard | `DashboardView` | Today Brief, Customer Brain, grade/stage funnels, priority and quality tables | UI construction + Core tests |
| Opportunity intelligence | `LeadIntelligenceView` | Search/grade queue, detail evidence, risk, single/bulk analysis and cancellation | Static + Core tests |
| Customers | `CustomersView` | Import/export, search and filters, dynamic dimensions, full columns, select/bulk delete, 10/30 pagination, editor and Customer Brain | Static + workbook regression |
| WhatsApp Inbox | `WhatsAppInboxView` | Multi-account QR login, sync, search, groups, pin, text/media, reply/revoke, CRM, sourcing, agent modes, handoff and knowledge citations | Static + Bridge/Core + UI smoke |
| Email Inbox | `EmailInboxView`, `EmailAccountWindow` | Provider guides and setup links, Keychain password, editable IMAP/SMTP TLS, test/save, sync, compose/reply and CRM/AI context | Static + Core + UI smoke |
| Automation | `CampaignsView` | WhatsApp/email accounts, saved templates, dynamic fields, Beijing schedule, interval/unit/limit, audience filters, previews, approval, pause/resume/cancel and history | Static + Core tests |
| Knowledge base | `KnowledgeBaseView` | Upload/version, metadata, original, chunks, activation, conflicts, real retrieval and candidate approval/publish | Static + Core tests |
| Customer analytics | `AnalyticsView` | Scrollable/searchable customer list, generation, full report sections, evidence, knowledge citations, history, comparison and Word/PDF export | Static + Core tests |
| Local-only boundary | `PlatformDataPaths`, repositories, secret stores | `~/Library/Application Support/WAFlow`, SQLite, macOS Keychain; no shared customer cloud/PWA store | Static + package smoke |
| Packaging | Windows release + macOS workflow | Apple Silicon self-contained app, native Bridge, ad-hoc signing, DMG mount/runtime/UI smoke and SHA-256 | Runner gate |

Run the parity gate locally with:

```powershell
./scripts/test-macos-parity.ps1
```

The workflow must not build or upload a DMG if this gate, the Core regression
suite, the native runtime smoke, or the eight-module UI smoke fails.
