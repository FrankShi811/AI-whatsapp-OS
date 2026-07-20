using System.Text;
using System.Net;
using System.Text.Json;
using WAFlow.Core.Domain;
using WAFlow.Core.Imports;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

var failures = new List<string>();
void Check(bool condition, string name) { if (condition) Console.WriteLine($"PASS  {name}"); else { Console.WriteLine($"FAIL  {name}"); failures.Add(name); } }

var root = Path.Combine(Path.GetTempPath(), "WAFlow-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var database = Path.Combine(root, "smoke.db");
var repository = new LocalRepository(database);
await repository.InitializeAsync();
var scorer = new LeadScoringService();
var imports = new ImportService(repository, scorer);

if (args.Length >= 2 && args[0] == "--workbook-only")
{
    var realParsed = imports.Parse(args[1]);
    var sherrySheet = realParsed.Sheets.FirstOrDefault(sheet => sheet.Name == "客户总表（Sherry3）") ?? realParsed.Sheets[0];
    Check(realParsed.PreferredSheetName == "客户总表（Sherry3）", "provided SP workbook active sheet selected by default");
    Check(sherrySheet.Rows.Count == 540 && sherrySheet.Headers.Count == 31, "provided SP workbook shape parsed");
    var realMapping = imports.SuggestMapping(sherrySheet);
    Check(realMapping.Any(row => row.Target == ImportField.Name) && realMapping.Any(row => row.Target == ImportField.WhatsApp), "provided SP workbook core fields inferred");
    var realPreview = await imports.BuildPreviewAsync(sherrySheet, realMapping);
    Check(realPreview.All(row => row.Errors.Count == 0), "provided SP workbook has no mandatory-field failures");
    var realCommit = await imports.CommitAsync(Path.GetFileName(args[1]), realPreview, allowStageChange:true, allowOwnerChange:true);
    var luis = (await repository.GetLeadsAsync("Luis Luis Exposito")).FirstOrDefault();
    Check(realCommit.Failed == 0 && realCommit.Created + realCommit.Updated == sherrySheet.Rows.Count, "provided SP workbook imports every row without mapping failures");
    Check(luis is not null && luis.CustomFields.Count == sherrySheet.Headers.Count, "provided SP workbook keeps all 31 original dimensions");
    Console.WriteLine($"RESULT total={realCommit.Total} created={realCommit.Created} updated={realCommit.Updated} invalid={realCommit.InvalidPhones} failed={realCommit.Failed}");
    try { File.Delete(database); Directory.Delete(root, true); } catch { }
    return failures.Count == 0 ? 0 : 1;
}

Check(LeadScoringService.GradeFromScore(80) == "A", "score boundary A=80");
Check(LeadScoringService.GradeFromScore(79) == "B", "score boundary B=79");
Check(LeadScoringService.GradeFromScore(40) == "C", "score boundary C=40");
Check(LeadScoringService.GradeFromScore(39) == "D", "score boundary D=39");

var phone = PhoneNormalizer.Normalize("07700 900123", "United Kingdom");
Check(phone.Valid && phone.E164 == "+447700900123" && phone.CountryInferred, "E.164 country inference");
var badPhone = PhoneNormalizer.Normalize("12345", "Unknown");
Check(!badPhone.Valid, "invalid phone risk");
var wa = PhoneNormalizer.BuildWaMeUrl("+44 7700 900123", "Hello Elena & team");
Check(wa == "https://wa.me/447700900123?text=Hello%20Elena%20%26%20team", "wa.me encoding");
Check(StageParser.Parse("qualified") == WAFlow.Core.Domain.LeadStage.Interested && StageParser.Parse("won") == WAFlow.Core.Domain.LeadStage.Customer, "legacy stage migration");

var csvPath = Path.Combine(root, "sample.csv");
await File.WriteAllTextAsync(csvPath, "客户姓名,公司名称,国家,WhatsApp号码,意向产品,预计订单额,阶段,备注,门店数量,采购周期\r\nNew Buyer,North Star,United Kingdom,07700900999,Oak chair,12000,new,=HYPERLINK(\"bad\"),12,Quarterly\r\nElena Duplicate,Nordline Living,Italy,+393491234567,DC-18,26000,won,Needs quote,28,Monthly", new UTF8Encoding(true));
var parsed = imports.Parse(csvPath);
Check(parsed.Sheets.Count == 1 && parsed.Sheets[0].Rows.Count == 2, "CSV parser rows");
Check(parsed.Sheets[0].SanitizedFormulaCount >= 1 && parsed.Sheets[0].Rows[0]["备注"].StartsWith("'="), "formula injection sanitization");
var mapping = imports.SuggestMapping(parsed.Sheets[0]);
Check(mapping.Any(m => m.Target == ImportField.WhatsApp) && mapping.Any(m => m.Target == ImportField.Name), "bilingual field mapping");
Check(mapping.Count(m => m.Target == ImportField.Custom) == 2, "unknown headers retained as custom dimensions");
var preview = await imports.BuildPreviewAsync(parsed.Sheets[0], mapping);
Check(preview.Count == 2 && preview.Count(x => x.IsDuplicate) == 1, "duplicate preview by E.164");
var committed = await imports.CommitAsync("sample.csv", preview, allowStageChange:false, allowOwnerChange:false);
Check(committed.Created == 1 && committed.Updated == 1, "preview then update commit");
var elena = await repository.GetLeadAsync("lead_elena");
Check(elena?.Stage == WAFlow.Core.Domain.LeadStage.Negotiation, "duplicate stage protected by default");
var newBuyer = (await repository.GetLeadsAsync("New Buyer")).Single();
Check(newBuyer.CustomFields.GetValueOrDefault("门店数量") == "12" && newBuyer.CustomFields.GetValueOrDefault("采购周期") == "Quarterly", "custom dimensions persisted on lead");
Check(newBuyer.CustomFields.Count == parsed.Sheets[0].Headers.Count && newBuyer.CustomFields.GetValueOrDefault("客户姓名") == "New Buyer", "every original spreadsheet column persisted");
var dashboard = await repository.GetDashboardAsync();
Check(dashboard.TotalLeads == 6, "SQLite persisted seed plus imported lead");

var buyerHeader = "buyer_nickname\n累计GMV≥10w，且近一年GMV≥10000";
var directSheet = new ImportSheet
{
    Name = "direct",
    Headers = [buyerHeader, "现Owner", "电话", "国家/邮箱", "任意业务维度"],
    Rows = [new Dictionary<string, string>
    {
        [buyerHeader] = "direct_buyer_01", ["现Owner"] = "Daisy", ["电话"] = "+14155552671",
        ["国家/邮箱"] = "美国", ["任意业务维度"] = "原样保留"
    }]
};
var directMapping = imports.SuggestMapping(directSheet);
Check(directMapping.Single(x => x.Header == buyerHeader).Target == ImportField.Name &&
      directMapping.Single(x => x.Header == "现Owner").Target == ImportField.Owner &&
      directMapping.Single(x => x.Header == "电话").Target == ImportField.WhatsApp, "long and mixed-language headers inferred without manual mapping");
var directPreview = await imports.BuildPreviewAsync(directSheet, directMapping);
var directCommit = await imports.CommitAsync("direct.xlsx", directPreview, allowStageChange:true, allowOwnerChange:true);
var directLead = (await repository.GetLeadsAsync("direct_buyer_01")).Single();
Check(directCommit.Failed == 0 && directLead.CustomFields.Count == directSheet.Headers.Count && directLead.Owner == "Daisy", "direct import retains every source dimension and links core fields");
directLead.CustomFields["任意业务维度"] = "人工修改后的值";
await repository.UpsertLeadAsync(directLead);
Check((await repository.GetLeadAsync(directLead.Id))?.CustomFields.GetValueOrDefault("任意业务维度") == "人工修改后的值", "manual custom-dimension edits persist");

var riskyPhoneSheet = new ImportSheet
{
    Name="risky-phone", Headers=["buyer_nickname", "电话", "国家"],
    Rows=[new Dictionary<string, string> { ["buyer_nickname"]="risky_buyer", ["电话"]="0", ["国家"]="美国" }]
};
var riskyPhonePreview = await imports.BuildPreviewAsync(riskyPhoneSheet, imports.SuggestMapping(riskyPhoneSheet));
Check(!riskyPhonePreview.Single().PhoneValid && riskyPhonePreview.Single().PhoneE164 == "0", "invalid phone keeps the original cell value instead of a misleading partial E.164 value");

var legacyLead = new Lead { Name="Legacy mapped name", CustomFields=new Dictionary<string, string> { [buyerHeader]="legacy_buyer_01" } };
await repository.UpsertLeadAsync(legacyLead);
var legacySheet = new ImportSheet
{
    Name="legacy-reimport", Headers=[buyerHeader, "电话", "任意业务维度"],
    Rows=[new Dictionary<string, string> { [buyerHeader]="legacy_buyer_01", ["电话"]="+14155550123", ["任意业务维度"]="补全后数据" }]
};
var legacyPreview = await imports.BuildPreviewAsync(legacySheet, imports.SuggestMapping(legacySheet));
var legacyCommit = await imports.CommitAsync("legacy-reimport.xlsx", legacyPreview, allowStageChange:true, allowOwnerChange:true);
var legacyUpdated = await repository.GetLeadAsync(legacyLead.Id);
Check(legacyPreview.Single().IsDuplicate && legacyCommit.Updated == 1 && legacyUpdated?.CustomFields.Count == 3, "fixed importer upgrades earlier partial rows instead of duplicating them");

var arbitrarySheet = new ImportSheet
{
    Name = "arbitrary",
    Headers = ["唯一编号", "完全自由的维度"],
    Rows = [new Dictionary<string, string> { ["唯一编号"] = "SP-ONLY-001", ["完全自由的维度"] = "也可以导入" }]
};
var arbitraryPreview = await imports.BuildPreviewAsync(arbitrarySheet, imports.SuggestMapping(arbitrarySheet));
Check(arbitraryPreview.Single().Errors.Count == 0 && arbitraryPreview.Single().Name == "SP-ONLY-001", "table with no standard CRM columns still imports directly");

const int largeRowCount = 12_000;
var largeCsvPath = Path.Combine(root, "large.csv");
var largeCsv = new StringBuilder("客户姓名,公司名称,国家,WhatsApp号码,行业,年度采购频次\r\n");
for (var index = 0; index < largeRowCount; index++)
    largeCsv.Append("Bulk ").Append(index).Append(",Company ").Append(index).Append(",United Kingdom,0").Append(7_000_000_000L + index).Append(",Retail,").Append(index % 12 + 1).Append("\r\n");
await File.WriteAllTextAsync(largeCsvPath, largeCsv.ToString(), new UTF8Encoding(true));
var largeParsed = imports.Parse(largeCsvPath);
Check(largeParsed.Sheets.Single().Rows.Count == largeRowCount, "no fixed 500-row import limit");
var largeMapping = imports.SuggestMapping(largeParsed.Sheets.Single());
var largePreview = await imports.BuildPreviewAsync(largeParsed.Sheets.Single(), largeMapping);
var largeCommit = await imports.CommitAsync("large.csv", largePreview, allowStageChange:false, allowOwnerChange:false);
Check(largeCommit.Created == largeRowCount && largeCommit.Failed == 0, "12,000-row batched SQLite import");
var lastBulkLead = (await repository.GetLeadsAsync("Bulk 11999")).Single();
Check(lastBulkLead.CustomFields.GetValueOrDefault("行业") == "Retail", "large import custom dimensions persisted");

var workbookArgument = args.SkipWhile(value => value != "--workbook").Skip(1).FirstOrDefault();
if (!string.IsNullOrWhiteSpace(workbookArgument))
{
    var realParsed = imports.Parse(workbookArgument);
    var sherrySheet = realParsed.Sheets.FirstOrDefault(sheet => sheet.Name == "客户总表（Sherry3）") ?? realParsed.Sheets[0];
    Check(sherrySheet.Rows.Count == 540 && sherrySheet.Headers.Count == 31, "provided SP workbook shape parsed");
    var realPreview = await imports.BuildPreviewAsync(sherrySheet, imports.SuggestMapping(sherrySheet));
    var realCommit = await imports.CommitAsync(Path.GetFileName(workbookArgument), realPreview, allowStageChange:true, allowOwnerChange:true);
    var luis = (await repository.GetLeadsAsync("Luis Luis Exposito")).FirstOrDefault();
    Check(realCommit.Failed == 0 && realCommit.Created + realCommit.Updated == sherrySheet.Rows.Count, "provided SP workbook imports every row without mapping failures");
    Check(luis is not null && luis.CustomFields.Count == sherrySheet.Headers.Count, "provided SP workbook keeps all 31 original dimensions");
}

var whatsappLead = (await repository.GetLeadAsync("lead_james"))!;
whatsappLead.WhatsAppOptIn = true; whatsappLead.WhatsAppOptInAt = DateTimeOffset.Now; whatsappLead.WhatsAppOptInSource = "smoke-test";
await repository.UpsertLeadAsync(whatsappLead);
var conversation = new WhatsAppConversation { Id="primary:447700900123", AccountId="primary", Phone="447700900123", LeadId=whatsappLead.Id, DisplayName=whatsappLead.DisplayName, LastMessage="Hello", LastMessageAt=DateTimeOffset.Now, UnreadCount=1 };
await repository.UpsertWhatsAppConversationAsync(conversation);
var whatsappMessage = new WhatsAppMessage { Id="primary:wamid-smoke", ProviderMessageId="wamid-smoke", AccountId="primary", ConversationId=conversation.Id, LeadId=whatsappLead.Id, Phone=conversation.Phone, Direction=WhatsAppMessageDirection.Incoming, Status=WhatsAppMessageStatus.Received, Body="Hello", Timestamp=DateTimeOffset.Now };
var messageInserted = await repository.UpsertWhatsAppMessageAsync(whatsappMessage);
var messageInsertedTwice = await repository.UpsertWhatsAppMessageAsync(whatsappMessage);
Check(messageInserted && !messageInsertedTwice && (await repository.GetWhatsAppMessagesAsync(conversation.Id)).Count == 1, "WhatsApp message idempotency");
await repository.UpdateWhatsAppMessageStatusAsync("primary", "wamid-smoke", WhatsAppMessageStatus.Read);
Check((await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single().Status == WhatsAppMessageStatus.Read, "WhatsApp message status persistence");
await repository.MarkWhatsAppConversationReadAsync(conversation.Id);
Check((await repository.GetWhatsAppConversationsAsync()).Single(x => x.Id == conversation.Id).UnreadCount == 0, "WhatsApp conversation unread persistence");
Check((await repository.GetLeadAsync("lead_james"))?.WhatsAppOptIn == true, "WhatsApp opt-in audit fields persisted");
var whatsappAccounts = await repository.GetWhatsAppAccountsAsync();
whatsappAccounts.Add(new WhatsAppAccount { Id="personal_test", Name="Personal Test" });
await repository.SaveWhatsAppAccountsAsync(whatsappAccounts);
Check((await repository.GetWhatsAppAccountsAsync()).Count == 2, "multiple personal WhatsApp accounts persisted");

var outgoingStatus = new WhatsAppMessage { Id="primary:wamid-out", ProviderMessageId="wamid-out", AccountId="primary", ConversationId=conversation.Id, LeadId=whatsappLead.Id, Phone=conversation.Phone, Direction=WhatsAppMessageDirection.Outgoing, Status=WhatsAppMessageStatus.Sent, Body="Status test", Timestamp=DateTimeOffset.Now };
await repository.UpsertWhatsAppMessageAsync(outgoingStatus);
outgoingStatus.Status = WhatsAppMessageStatus.Pending;
await repository.UpsertWhatsAppMessageAsync(outgoingStatus);
Check((await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single(x => x.Id == outgoingStatus.Id).Status == WhatsAppMessageStatus.Sent, "WhatsApp status cannot regress on duplicate event");

var campaignBridge = new WhatsAppConnectionManager();
await using var campaigns = new CampaignAutomationService(repository, campaignBridge);
var campaign = new WhatsAppCampaign
{
    Name="UK opt-in follow-up", TagFilter="UK", MessageTemplate="Hi {name}, following up about {product} for {company}.",
    StartsAt=DateTimeOffset.Now.AddMinutes(10), IntervalMinutes=3, DailyLimit=25
};
var campaignPreview = await campaigns.PreviewAudienceAsync(campaign);
Check(campaignPreview.Count == 1 && campaignPreview.Single().Eligible && campaignPreview.Single().PreviewMessage.Contains("Reusable water bottles"), "campaign opt-in audience and template preview");
await campaigns.SaveDraftAsync(campaign);
var scheduledCount = await campaigns.ApproveAndScheduleAsync(campaign, "smoke-test");
var campaignRecipients = await repository.GetCampaignRecipientsAsync(campaign.Id);
Check(scheduledCount == 1 && campaignRecipients.Single().Status == CampaignRecipientStatus.Queued && (await repository.GetCampaignAsync(campaign.Id))?.Status == CampaignStatus.Scheduled, "campaign approval creates durable queue");
var uncertainRecipient = campaignRecipients.Single(); uncertainRecipient.Status = CampaignRecipientStatus.Sending;
await repository.SaveCampaignRecipientAsync(uncertainRecipient);
await repository.RecoverInterruptedCampaignRecipientsAsync();
Check((await repository.GetCampaignRecipientsAsync(campaign.Id)).Single().Status == CampaignRecipientStatus.Failed, "campaign uncertain send is never auto-retried");
whatsappLead.OptedOut = true;
await repository.UpsertLeadAsync(whatsappLead);
var optedOutPreview = await campaigns.PreviewAudienceAsync(new WhatsAppCampaign { Name="opt-out-check", TagFilter="UK", MessageTemplate="Hi {name}", StartsAt=DateTimeOffset.Now.AddHours(1) });
Check(optedOutPreview.Single().Eligible == false && optedOutPreview.Single().Reason == "客户已退订", "campaign opt-out exclusion rechecked");
whatsappLead.OptedOut = false;
await repository.UpsertLeadAsync(whatsappLead);
await campaigns.PauseAsync(campaign, "smoke-test pause");
Check((await repository.GetCampaignAsync(campaign.Id))?.Status == CampaignStatus.Paused, "campaign pause persisted");
await campaigns.ResumeAsync(campaign);
Check((await repository.GetCampaignAsync(campaign.Id))?.Status == CampaignStatus.Scheduled, "campaign resume persisted");
var secondAccountCampaign = new WhatsAppCampaign { AccountId="personal_test", Name="second account draft", MessageTemplate="Hi {name}", StartsAt=DateTimeOffset.Now.AddHours(1) };
await campaigns.SaveDraftAsync(secondAccountCampaign);
Check((await repository.GetCampaignsAsync("personal_test")).Single().AccountId == "personal_test" && (await repository.GetCampaignsAsync(null)).Count == 2, "campaign queues isolated by WhatsApp account");

await using (var embeddedBridge = new WhatsAppConnectionManager())
{
    await embeddedBridge.StartAsync("embedded_smoke");
    var bridgePing = await embeddedBridge.PingAsync();
    Check(bridgePing.TryGetProperty("bridge", out var bridgeName) && bridgeName.GetString() == "WAFlow.WhatsApp.Bridge", "embedded bridge EXE extraction and startup");
    await embeddedBridge.LogoutAsync();
}

var analysisJson = WAFlow.Core.Infrastructure.Json.Serialize(new
{
    score=88, grade="A",
    factors=new[]
    {
        new { key="marketValue", score=15, maxScore=15, rationale="订单价值高" }, new { key="companyScale", score=8, maxScore=10, rationale="公司规模较好" },
        new { key="productFit", score=18, maxScore=20, rationale="产品匹配度高" }, new { key="purchasePower", score=13, maxScore=15, rationale="采购能力强" },
        new { key="replyEngagement", score=10, maxScore=15, rationale="回复积极" }, new { key="recency", score=9, maxScore=10, rationale="近期活跃" },
        new { key="explicitDemand", score=10, maxScore=10, rationale="需求明确" }, new { key="registeredOrConsulted", score=5, maxScore=5, rationale="已有咨询" }
    },
    stage="negotiation", confidence=.91,
    evidence=new[] { new { field="latestMessage", value="300 units", interpretation="数量信号明确" } },
    profileSummary="欧洲家具分销商，采购意图明确。", customerSegment="高价值经销商", nextAction="确认交期并提供阶梯报价。", risks=new[] { "需要人工核对交期" }
});
var draftJson = WAFlow.Core.Infrastructure.Json.Serialize(new { purpose="follow_up", language="en", body="Hi Elena, thank you for confirming 300 units. I will verify the lead time and share the next details with you.", rationale=new[] { "承接客户的数量与交期问题" }, assumptions=Array.Empty<string>(), risks=new[] { "交期需人工确认" } });
var invalidAnalysisJson = "{\"score\":99,\"grade\":\"A\",\"factors\":[],\"stage\":\"new\",\"confidence\":0.8,\"evidence\":[],\"profileSummary\":\"x\",\"customerSegment\":\"x\",\"nextAction\":\"x\",\"risks\":[]}";
var handler = new QueueHandler([Envelope(analysisJson), Envelope(draftJson), Envelope(invalidAnalysisJson)]);
var deepSeek = new DeepSeekService(repository, new FakeSecretStore("sk-test-redacted"), new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(5) });
await repository.SaveAppSettingsAsync(new AppSettings { DeepSeekBaseUrl="https://api.deepseek.com", DeepSeekModel="deepseek-chat" });
var salesProfile = new SalesProfile { CompanyName="WAFlow Test", Products=["Oak chair"], Advantages=["Flexible MOQ"], DefaultLanguage="en" };
var analyzed = await deepSeek.AnalyzeLeadAsync((await repository.GetLeadAsync("lead_elena"))!, salesProfile);
Check(analyzed.AnalysisStatus == AnalysisStatus.Succeeded && analyzed.Score == 88 && analyzed.Evidence.Count == 1, "DeepSeek structured analysis success");
var generated = await deepSeek.GenerateDraftAsync(analyzed, salesProfile, "follow_up", "en", "");
Check(generated.Body.StartsWith("Hi Elena") && generated.Status == DraftStatus.Draft, "DeepSeek structured draft success");
try
{
    await deepSeek.AnalyzeLeadAsync((await repository.GetLeadAsync("lead_ahmed"))!, salesProfile);
    Check(false, "DeepSeek invalid structure rejected");
}
catch (DeepSeekException error) { Check(error.Code == "invalid_structured_output" && error.Retryable, "DeepSeek invalid structure rejected"); }
Check(handler.Requests.All(x => x.Authorization == "Bearer sk-test-redacted" && x.Uri == "https://api.deepseek.com/chat/completions"), "DeepSeek request contract and server-side key");

try { File.Delete(database); Directory.Delete(root, true); } catch { }
Console.WriteLine(failures.Count == 0 ? "\nAI Sales OS native core smoke tests passed." : $"\n{failures.Count} smoke test(s) failed.");
return failures.Count == 0 ? 0 : 1;

static string Envelope(string content) => System.Text.Json.JsonSerializer.Serialize(new { choices=new[] { new { message=new { content } } } });

sealed class FakeSecretStore(string value) : ISecretStore
{
    public void Save(string secret) { }
    public string? Read() => value;
}

sealed class QueueHandler(IEnumerable<string> responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);
    public List<(string Uri, string Authorization)> Requests { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString() ?? ""));
        _ = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") };
    }
}
