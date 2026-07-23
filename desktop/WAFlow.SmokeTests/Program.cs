using System.Text;
using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Data.Sqlite;
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
var imports = new ImportService(repository);

if (args.Length >= 3 && args[0] == "--database-reimport")
{
    var upgradeRepository = new LocalRepository(args[2]);
    await upgradeRepository.InitializeAsync();
    var upgradeImports = new ImportService(upgradeRepository);
    var realParsed = upgradeImports.Parse(args[1]);
    var sherrySheet = realParsed.Sheets.FirstOrDefault(sheet => sheet.Name == "\u5ba2\u6237\u603b\u8868\uff08Sherry3\uff09") ?? realParsed.Sheets[0];
    var realPreview = await upgradeImports.BuildPreviewAsync(sherrySheet, upgradeImports.SuggestMapping(sherrySheet));
    var realCommit = await upgradeImports.CommitAsync(Path.GetFileName(args[1]), realPreview, allowStageChange:true, allowOwnerChange:true);
    await upgradeRepository.RemoveDemoLeadsIfRealDataExistsAsync();
    var leads = await upgradeRepository.GetLeadsAsync();
    Check(realCommit.Failed == 0 && realCommit.Created + realCommit.Updated == sherrySheet.Rows.Count, "existing partial database reimport processes every workbook row");
    Check(leads.Count >= sherrySheet.Rows.Count, "existing partial database retains one customer record for every workbook row");
    Console.WriteLine($"RESULT total={realCommit.Total} created={realCommit.Created} updated={realCommit.Updated} invalid={realCommit.InvalidPhones} failed={realCommit.Failed} leads={leads.Count}");
    try { File.Delete(database); Directory.Delete(root, true); } catch { }
    return failures.Count == 0 ? 0 : 1;
}

if (args.Length >= 2 && args[0] == "--workbook-only")
{
    var realParsed = imports.Parse(args[1]);
    var sherrySheet = realParsed.Sheets.FirstOrDefault(sheet => sheet.Name == "客户总表（Sherry3）") ?? realParsed.Sheets[0];
    Check(realParsed.PreferredSheetName == "客户总表（Sherry3）", "provided SP workbook active sheet selected by default");
    Check(sherrySheet.Rows.Count > 0 && sherrySheet.Headers.Count > 0, "provided SP workbook shape parsed");
    var realMapping = imports.SuggestMapping(sherrySheet);
    Check(realMapping.Any(row => row.Target == ImportField.Name) && realMapping.Any(row => row.Target == ImportField.WhatsApp), "provided SP workbook core fields inferred");
    var realPreview = await imports.BuildPreviewAsync(sherrySheet, realMapping);
    Check(realPreview.All(row => row.Errors.Count == 0), "provided SP workbook has no mandatory-field failures");
    var realCommit = await imports.CommitAsync(Path.GetFileName(args[1]), realPreview, allowStageChange:true, allowOwnerChange:true);
    var firstImported = (await repository.GetLeadsAsync()).FirstOrDefault(lead => lead.Name == realPreview[0].Name && lead.PhoneE164 == realPreview[0].PhoneE164);
    Check(realCommit.Failed == 0 && realCommit.Created == sherrySheet.Rows.Count && realCommit.Updated == 0, "provided SP workbook imports every row as one customer without collapsing repeated names or phones");
    Check(firstImported is not null && firstImported.CustomFields.Count == sherrySheet.Headers.Count, "provided SP workbook keeps every original dimension");
    Console.WriteLine($"RESULT total={realCommit.Total} created={realCommit.Created} updated={realCommit.Updated} invalid={realCommit.InvalidPhones} failed={realCommit.Failed}");
    try { File.Delete(database); Directory.Delete(root, true); } catch { }
    return failures.Count == 0 ? 0 : 1;
}

Check(LeadScoringService.GradeFromScore(80) == "A", "score boundary A=80");
Check(LeadScoringService.GradeFromScore(79) == "B", "score boundary B=79");
Check(LeadScoringService.GradeFromScore(40) == "C", "score boundary C=40");
Check(LeadScoringService.GradeFromScore(39) == "D", "score boundary D=39");
Check(LeadScoringService.Weights.SequenceEqual(new Dictionary<string, int>
{
    ["paid_marketing_willingness"] = 25, ["supply_stability"] = 20, ["ecommerce_foundation"] = 15,
    ["private_traffic"] = 15, ["existing_sales"] = 15, ["materials_readiness"] = 10
}), "Lead Intelligence V2 uses the six requested dimensions and 100 point total");

var phone = PhoneNormalizer.Normalize("447700 900123", "United Kingdom");
Check(phone.Valid && phone.E164 == "+447700900123" && !phone.CountryInferred, "phone normalization only adds plus without inferring a country code");
var localFormatPhone = PhoneNormalizer.Normalize("07700 900123", "United Kingdom");
Check(!localFormatPhone.Valid && localFormatPhone.E164 == "+07700900123" && !localFormatPhone.CountryInferred, "country field is ignored and a local leading zero is preserved");
var alreadyInternationalUsPhone = PhoneNormalizer.Normalize("13373224256", "美国");
Check(alreadyInternationalUsPhone.Valid && alreadyInternationalUsPhone.E164 == "+13373224256", "existing digits are preserved when adding plus");
Check(PhoneIdentity.IsMatch("+113373224256", "+13373224256"), "legacy duplicated country code still matches WhatsApp number by complete suffix");
var customPhoneLead = new Lead { Name="custom phone", CustomFields=new Dictionary<string, string> { ["WhatsApp号码"]="1-337-322-4256" } };
Check(PhoneIdentity.FindUniqueLead([customPhoneLead], "+13373224256")?.Id == customPhoneLead.Id, "WhatsApp custom column participates in customer matching");
var groupRequest = WhatsAppGroupCreateRequest.CreateValidated("Priority Buyers", ["+44 7700 900123", "447700900123", "+1 415 555 0103"]);
Check(groupRequest.Subject == "Priority Buyers" && groupRequest.ParticipantPhones.SequenceEqual(["+447700900123", "+14155550103"]), "WhatsApp group request validates and deduplicates international members");
try { WhatsAppGroupCreateRequest.CreateValidated("", ["+447700900123"]); Check(false, "WhatsApp group rejects empty subject"); }
catch (InvalidOperationException) { Check(true, "WhatsApp group rejects empty subject"); }
var ambiguousPhoneMatch = PhoneIdentity.FindUniqueLead([
    new Lead { Name="first", PhoneE164="+11234567890" },
    new Lead { Name="second", PhoneE164="+21234567890" }
], "1234567890");
Check(ambiguousPhoneMatch is null, "ambiguous suffix phone matches fail closed");
var badPhone = PhoneNormalizer.Normalize("12345", "Unknown");
Check(!badPhone.Valid && badPhone.E164 == "+12345", "invalid phone is retained with a leading plus for correction");
var wa = PhoneNormalizer.BuildWaMeUrl("+44 7700 900123", "Hello Elena & team");
Check(wa == "https://wa.me/447700900123?text=Hello%20Elena%20%26%20team", "wa.me encoding");
Check(StageParser.Parse("qualified") == WAFlow.Core.Domain.LeadStage.Interested && StageParser.Parse("won") == WAFlow.Core.Domain.LeadStage.Customer, "legacy stage migration");
Check(StageParser.Parse("requirement_confirmed") == LeadStage.RequirementConfirmed
    && StageParser.Parse("quotation") == LeadStage.Quotation
    && StageParser.Parse("repeat_purchase") == LeadStage.RepeatPurchase, "personal sales lifecycle stages parse without collapsing into legacy stages");

var baselineRoot = Path.Combine(root, "ai-baseline");
var baselineRepository = new LocalRepository(Path.Combine(baselineRoot, "baseline.db"));
await baselineRepository.InitializeAsync();
await baselineRepository.UpsertLeadAsync(new Lead { Id="legacy-rule-score", Name="Legacy Rule Score", Grade="B", Score=72, ScoreBreakdown=new Dictionary<string, int> { ["productFit"]=18 }, ScoreReasons=["旧规则评分"], AnalysisStatus=AnalysisStatus.NotRun });
await baselineRepository.UpsertLeadAsync(new Lead { Id="legacy-ai-score", Name="Legacy AI Score", Grade="A", Score=88, AnalysisContractVersion=1, AiScoreApplied=true, AnalysisStatus=AnalysisStatus.Succeeded, ProfileSummary="旧版 AI 画像" });
await baselineRepository.InitializeAsync();
var alignedBaseline = await baselineRepository.GetLeadAsync("legacy-rule-score");
Check(alignedBaseline is { Grade: "D", Score: 0, AiScoreApplied: false, AnalysisStatus: AnalysisStatus.NotRun } && alignedBaseline.ScoreBreakdown.Count == 0, "upgrade resets legacy non-AI scores to the D baseline");
var alignedLegacyAi = await baselineRepository.GetLeadAsync("legacy-ai-score");
Check(alignedLegacyAi is { Grade: "D", Score: 0, AiScoreApplied: false, AnalysisStatus: AnalysisStatus.NotRun, AnalysisContractVersion: 0 } && alignedLegacyAi.AnalysisError.Contains("旧评分契约"), "upgrade invalidates V1 AI scores without deleting CRM data");

var recoveryRoot = Path.Combine(root, "database-recovery");
var recoveryDatabase = Path.Combine(recoveryRoot, "recovery.db");
var recoverySeedRepository = new LocalRepository(recoveryDatabase);
await recoverySeedRepository.InitializeAsync();
var recoveryLead = new Lead { Id="recovery-customer", Name="Recovery Customer", PhoneE164="+14155550123", PhoneValid=true };
await recoverySeedRepository.UpsertLeadAsync(recoveryLead);
SqliteConnection.ClearAllPools();
int recoveryPageSize;
long damagedIndexRootPage;
await using (var recoveryConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=recoveryDatabase, Pooling=false }.ToString()))
{
    await recoveryConnection.OpenAsync();
    await using var pageSizeCommand = recoveryConnection.CreateCommand();
    pageSizeCommand.CommandText = "PRAGMA page_size";
    recoveryPageSize = Convert.ToInt32(await pageSizeCommand.ExecuteScalarAsync());
    await using var indexPageCommand = recoveryConnection.CreateCommand();
    indexPageCommand.CommandText = "SELECT rootpage FROM sqlite_schema WHERE type='index' AND name='ix_leads_filters'";
    damagedIndexRootPage = Convert.ToInt64(await indexPageCommand.ExecuteScalarAsync());
}
await using (var databaseBytes = new FileStream(recoveryDatabase, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
{
    databaseBytes.Position = (damagedIndexRootPage - 1) * recoveryPageSize;
    databaseBytes.WriteByte(0);
    databaseBytes.Flush(true);
}
var recoveredRepository = new LocalRepository(recoveryDatabase);
await recoveredRepository.InitializeAsync();
Check(recoveredRepository.LastRecoveryNotice is { LeadCount: 6 } notice && Directory.Exists(notice.BackupDirectory), "malformed SQLite database is backed up and recovered during startup");
Check((await recoveredRepository.GetLeadAsync(recoveryLead.Id))?.Name == recoveryLead.Name, "database recovery preserves readable CRM customer data");
SqliteConnection.ClearAllPools();
await using (var recoveredConnection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=recoveryDatabase, Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString()))
{
    await recoveredConnection.OpenAsync();
    await using var integrityCommand = recoveredConnection.CreateCommand();
    integrityCommand.CommandText = "PRAGMA integrity_check";
    Check(string.Equals(Convert.ToString(await integrityCommand.ExecuteScalarAsync()), "ok", StringComparison.OrdinalIgnoreCase), "recovered SQLite database passes integrity check");
}

var csvPath = Path.Combine(root, "sample.csv");
await File.WriteAllTextAsync(csvPath, "客户姓名,公司名称,国家,WhatsApp号码,意向产品,预计订单额,阶段,备注,门店数量,采购周期\r\nNew Buyer,North Star,United Kingdom,07700900999,Oak chair,12000,new,=HYPERLINK(\"bad\"),12,Quarterly\r\nElena Duplicate,Nordline Living,Italy,+393491234567,DC-18,26000,won,Needs quote,28,Monthly", new UTF8Encoding(true));
var parsed = imports.Parse(csvPath);
Check(parsed.Sheets.Count == 1 && parsed.Sheets[0].Rows.Count == 2, "CSV parser rows");
using (var writerHandle = new FileStream(csvPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
    Check(imports.Parse(csvPath).Sheets.Single().Rows.Count == 2, "spreadsheet can be imported while another process holds a write handle");
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
Check(newBuyer.PhoneE164 == "+07700900999" && !newBuyer.PhoneValid, "import never prepends a country dialing code and only adds plus");
Check(newBuyer.CustomFields.GetValueOrDefault("门店数量") == "12" && newBuyer.CustomFields.GetValueOrDefault("采购周期") == "Quarterly", "custom dimensions persisted on lead");
Check(newBuyer.CustomFields.Count == parsed.Sheets[0].Headers.Count && newBuyer.CustomFields.GetValueOrDefault("客户姓名") == "New Buyer", "every original spreadsheet column persisted");
Check(newBuyer is { Grade: "D", Score: 0, AnalysisStatus: AnalysisStatus.NotRun, AiScoreApplied: false }, "new imports stay D until an AI analysis succeeds");
var dashboard = await repository.GetDashboardAsync();
Check(dashboard.TotalLeads == 6, "SQLite persisted seed plus imported lead");

var assistantLead = new Lead { Id="assistant-lead", Name="Assistant Buyer", PhoneE164="+14155550999", PhoneValid=true, Grade="D", Score=0 };
await repository.UpsertLeadAsync(assistantLead);
var assistantResult = new ConversationAssistantResult
{
    ReplyText="Thanks for the details. I can prepare the next step for your monthly requirement.",
    ReplyLanguage="en",
    NeedsSummary="客户明确表示每月需要采购500件。",
    CustomerIntent="存在明确的周期性采购意向。",
    PurchaseSignals=["明确月采购数量"],
    Risks=["价格与交期尚未确认"],
    RecommendedNextAction="核对产品规格后提供报价与交期。",
    Confidence=0.92,
    Model="deepseek-test",
    FieldUpdates=
    [
        new ConversationFieldUpdate { Field="采购数量", Value="500件/月", EvidenceQuote="I need 500 pcs monthly", Reason="客户明确给出数量和周期" },
        new ConversationFieldUpdate { Field="stage", Value="interested", EvidenceQuote="I need 500 pcs monthly", Reason="客户表达明确采购需求" }
    ]
};
Check(ConversationAssistantService.Validate(assistantResult, ["采购数量", "stage"], ["I need 500 pcs monthly"]) is null, "AI conversation assistant accepts evidence-backed CRM field proposals");
var invalidAssistantResult = new ConversationAssistantResult
{
    ReplyText="Hello", NeedsSummary="需求待确认。", CustomerIntent="待确认。", RecommendedNextAction="继续沟通。", Confidence=0.5,
    FieldUpdates=[new ConversationFieldUpdate { Field="采购数量", Value="1000件", EvidenceQuote="I need 1000 pcs monthly", Reason="数量" }]
};
Check(ConversationAssistantService.Validate(invalidAssistantResult, ["采购数量"], ["I need 500 pcs monthly"])?.Contains("incoming 原话") == true, "AI conversation assistant rejects field updates without an exact customer quote");
var assistantService = new ConversationAssistantService(repository, new AlwaysInvalidStructuredReportProvider());
assistantLead = await assistantService.ApplyAsync(assistantLead, "14155550999", assistantLead.Name, assistantResult, assistantResult.FieldUpdates);
var assistantHistory = await repository.GetCustomerHistoryAsync(assistantLead.Id);
Check(assistantLead.CustomFields.GetValueOrDefault("采购数量") == "500件/月" && assistantLead.CustomFields.GetValueOrDefault("AI需求摘要")?.Contains("每月需要采购500件") == true && assistantLead.Stage == LeadStage.Interested, "approved AI conversation findings synchronize to the authoritative customer record");
Check(assistantLead is { Grade: "D", Score: 0, AiScoreApplied: false } && assistantHistory.Any(item => item.Type == "whatsapp_ai_assistant_crm_synced" && item.Detail.Contains("I need 500 pcs monthly")), "AI conversation assistant preserves D/0 until Lead Intelligence runs and stores an evidence audit trail");

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

var rowIdentityRoot = Path.Combine(root, "row-identity-import");
var rowIdentityRepository = new LocalRepository(Path.Combine(rowIdentityRoot, "row-identity.db"));
await rowIdentityRepository.InitializeAsync();
var rowIdentityImports = new ImportService(rowIdentityRepository);
var rowIdentitySheet = new ImportSheet
{
    Name="row-identity", Headers=["buyer_nickname", "电话", "国家/邮箱"],
    Rows=
    [
        new Dictionary<string, string> { ["buyer_nickname"]="same-name", ["电话"]="14155550101", ["国家/邮箱"]="美国" },
        new Dictionary<string, string> { ["buyer_nickname"]="same-name", ["电话"]="14155550102", ["国家/邮箱"]="美国" },
        new Dictionary<string, string> { ["buyer_nickname"]="shared-phone-one", ["电话"]="14155550103", ["国家/邮箱"]="美国" },
        new Dictionary<string, string> { ["buyer_nickname"]="shared-phone-two", ["电话"]="14155550103", ["国家/邮箱"]="美国" }
    ]
};
var rowIdentityMapping = rowIdentityImports.SuggestMapping(rowIdentitySheet);
var rowIdentityPreview = await rowIdentityImports.BuildPreviewAsync(rowIdentitySheet, rowIdentityMapping);
var rowIdentityCommit = await rowIdentityImports.CommitAsync("row-identity.xlsx", rowIdentityPreview, allowStageChange:true, allowOwnerChange:true);
Check(rowIdentityPreview.All(row => !row.IsDuplicate) && rowIdentityCommit.Created == 4, "every row in one workbook is imported even when names or phone numbers repeat");
Check(await rowIdentityRepository.GetLeadByPhoneAsync("14155550103") is null, "duplicate phone ownership fails closed instead of linking WhatsApp to the wrong customer");
var rowIdentityReimportPreview = await rowIdentityImports.BuildPreviewAsync(rowIdentitySheet, rowIdentityMapping);
var rowIdentityReimport = await rowIdentityImports.CommitAsync("row-identity.xlsx", rowIdentityReimportPreview, allowStageChange:true, allowOwnerChange:true);
Check(rowIdentityReimport.Created == 0 && rowIdentityReimport.Updated == 4 && rowIdentityReimportPreview.Select(row => row.DuplicateLeadId).Distinct().Count() == 4, "composite row identity keeps repeated-name and repeated-phone reimports idempotent");

var scientificPhonePath = Path.Combine(root, "scientific-phone.xlsx");
using (var scientificWorkbook = new XLWorkbook())
{
    var sheet = scientificWorkbook.AddWorksheet("customers");
    sheet.Cell(1, 1).Value = "buyer_nickname"; sheet.Cell(1, 2).Value = "电话"; sheet.Cell(1, 3).Value = "国家/邮箱";
    sheet.Cell(2, 1).Value = "scientific-phone";
    sheet.Cell(2, 2).Value = 525525000000d; sheet.Cell(2, 2).Style.NumberFormat.Format = "0.00E+00";
    sheet.Cell(2, 3).Value = 0;
    scientificWorkbook.SaveAs(scientificPhonePath);
}
var scientificParsed = imports.Parse(scientificPhonePath).Sheets.Single();
var scientificPreview = (await imports.BuildPreviewAsync(scientificParsed, imports.SuggestMapping(scientificParsed))).Single();
Check(scientificParsed.Rows.Single()["电话"] == "525525000000" && scientificPreview.PhoneE164 == "+525525000000", "numeric Excel phones bypass scientific display formatting and keep every digit");
Check(scientificPreview.Country == "" && scientificPreview.CustomValues["国家/邮箱"] == "0", "country placeholders stay in the original dimension but do not become an incorrect CRM country");
Check(PhoneNormalizer.Normalize("5.25525E+11", "").E164 == "+525525000000", "scientific phone text is expanded before normalization");

var legacyPhoneRoot = Path.Combine(root, "legacy-phone-reimport");
var legacyPhoneRepository = new LocalRepository(Path.Combine(legacyPhoneRoot, "legacy-phone.db"));
await legacyPhoneRepository.InitializeAsync();
await legacyPhoneRepository.UpsertLeadAsync(new Lead { Id="legacy-country-prefix", Name="Old Imported Name", PhoneE164="+113373224256", PhoneValid=true });
var legacyPhoneImports = new ImportService(legacyPhoneRepository);
var correctedPhoneSheet = new ImportSheet
{
    Name="corrected-phone",
    Headers=["客户姓名", "WhatsApp号码", "国家"],
    Rows=[new Dictionary<string, string> { ["客户姓名"]="Updated Imported Name", ["WhatsApp号码"]="13373224256", ["国家"]="美国" }]
};
var correctedPhonePreview = await legacyPhoneImports.BuildPreviewAsync(correctedPhoneSheet, legacyPhoneImports.SuggestMapping(correctedPhoneSheet));
Check(correctedPhonePreview.Single() is { IsDuplicate: true, DuplicateLeadId: "legacy-country-prefix", PhoneE164: "+13373224256" }, "reimport matches and corrects a legacy duplicated country prefix without creating a new customer");
directLead.CustomFields["任意业务维度"] = "人工修改后的值";
await repository.UpsertLeadAsync(directLead);
Check((await repository.GetLeadAsync(directLead.Id))?.CustomFields.GetValueOrDefault("任意业务维度") == "人工修改后的值", "manual custom-dimension edits persist");

const string protectedNameHeader = "buyer_nickname";
const string protectedStageHeader = "\u8ddf\u8fdb\u9636\u6bb5\uff08\u6bcf\u5468\u66f4\u65b0\uff09";
const string protectedDetailHeader = "\u8be6\u60c5\u8bb0\u5f55";
const string protectedBusinessHeader = "\u5ba2\u6237\u751f\u610f\u6a21\u5f0f";
const string protectedConnectionHeader = "\u5efa\u8054\u60c5\u51b5";
var protectedLead = new Lead
{
    Name="Protected Name", Company="Old Company", Country="US", PhoneE164="+14155550124", PhoneValid=true,
    Email="old@example.com", Stage=LeadStage.Negotiation, LatestMessage="human detail record",
    CustomFields=new Dictionary<string, string>
    {
        [protectedNameHeader]="Protected Name", [protectedStageHeader]="old follow-up", [protectedDetailHeader]="old detail",
        [protectedBusinessHeader]="old business", [protectedConnectionHeader]="old connection", ["overwrite"]="old", ["remove me"]="old"
    }
};
await repository.UpsertLeadAsync(protectedLead);
var replacementSheet = new ImportSheet
{
    Name="replacement",
    Headers=[protectedNameHeader,"\u516c\u53f8\u540d\u79f0","\u7535\u8bdd","\u90ae\u7bb1","\u9636\u6bb5","\u5907\u6ce8",protectedStageHeader,protectedDetailHeader,protectedBusinessHeader,protectedConnectionHeader,"overwrite","new field"],
    Rows=[new Dictionary<string, string>
    {
        [protectedNameHeader]="Changed Name", ["\u516c\u53f8\u540d\u79f0"]="New Company", ["\u7535\u8bdd"]="+14155550124", ["\u90ae\u7bb1"]="",
        ["\u9636\u6bb5"]="lost", ["\u5907\u6ce8"]="replacement note", [protectedStageHeader]="new follow-up", [protectedDetailHeader]="new detail",
        [protectedBusinessHeader]="new business", [protectedConnectionHeader]="new connection", ["overwrite"]="", ["new field"]="fresh"
    }]
};
var replacementPreview = await imports.BuildPreviewAsync(replacementSheet, imports.SuggestMapping(replacementSheet));
await imports.CommitAsync("replacement.xlsx", replacementPreview, allowStageChange:true, allowOwnerChange:true);
var protectedUpdated = (await repository.GetLeadAsync(protectedLead.Id))!;
Check(protectedUpdated.Name == "Protected Name" && protectedUpdated.Stage == LeadStage.Negotiation && protectedUpdated.LatestMessage == "human detail record", "duplicate import preserves customer name, stage and detail record");
Check(protectedUpdated.CustomFields[protectedStageHeader] == "old follow-up" && protectedUpdated.CustomFields[protectedDetailHeader] == "old detail" && protectedUpdated.CustomFields[protectedBusinessHeader] == "old business" && protectedUpdated.CustomFields[protectedConnectionHeader] == "old connection", "duplicate import preserves protected business dimensions");
Check(protectedUpdated.Company == "New Company" && protectedUpdated.Email == "" && protectedUpdated.CustomFields["overwrite"] == "" && protectedUpdated.CustomFields["new field"] == "fresh" && !protectedUpdated.CustomFields.ContainsKey("remove me"), "duplicate import replaces every unprotected field including blanks and removes stale dimensions");

var riskyPhoneSheet = new ImportSheet
{
    Name="risky-phone", Headers=["buyer_nickname", "电话", "国家"],
    Rows=[new Dictionary<string, string> { ["buyer_nickname"]="risky_buyer", ["电话"]="0", ["国家"]="美国" }]
};
var riskyPhonePreview = await imports.BuildPreviewAsync(riskyPhoneSheet, imports.SuggestMapping(riskyPhoneSheet));
Check(!riskyPhonePreview.Single().PhoneValid && riskyPhonePreview.Single().PhoneE164 == "+0", "invalid phone keeps its digits and receives only a leading plus for correction");

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
    Check(sherrySheet.Rows.Count > 0 && sherrySheet.Headers.Count > 0, "provided SP workbook shape parsed");
    var realPreview = await imports.BuildPreviewAsync(sherrySheet, imports.SuggestMapping(sherrySheet));
    var realCommit = await imports.CommitAsync(Path.GetFileName(workbookArgument), realPreview, allowStageChange:true, allowOwnerChange:true);
    Check(realCommit.Failed == 0 && realCommit.Created + realCommit.Updated == sherrySheet.Rows.Count, "provided SP workbook imports every row without mapping failures");
    var firstImported = (await repository.GetLeadsAsync()).FirstOrDefault(lead => lead.Name == realPreview[0].Name && lead.PhoneE164 == realPreview[0].PhoneE164);
    Check(firstImported is not null && firstImported.CustomFields.Count == sherrySheet.Headers.Count, "provided SP workbook keeps every original dimension");
}

var whatsappLead = (await repository.GetLeadAsync("lead_james"))!;
Check(WhatsAppTextEncodingRepair.Repair("I鈥檒l always be here") == "I’ll always be here" && WhatsAppTextEncodingRepair.Repair("正常中文消息") == "正常中文消息", "WhatsApp UTF-8 mojibake repair is selective");
whatsappLead.WhatsAppOptIn = true; whatsappLead.WhatsAppOptInAt = DateTimeOffset.Now; whatsappLead.WhatsAppOptInSource = "smoke-test";
await repository.UpsertLeadAsync(whatsappLead);
var whatsappContact = new WhatsAppContact { Id="primary:447700900123@s.whatsapp.net", AccountId="primary", Jid="447700900123@s.whatsapp.net", Phone="447700900123", DisplayName="James in WhatsApp", SavedName="James in WhatsApp", Source="history:recent" };
await repository.UpsertWhatsAppContactAsync(whatsappContact);
whatsappContact.NotifyName = "James updated";
await repository.UpsertWhatsAppContactAsync(whatsappContact);
var storedContact = (await repository.GetWhatsAppContactsAsync()).Single(x => x.Id == whatsappContact.Id);
Check(storedContact.Phone == "447700900123" && storedContact.NotifyName == "James updated", "WhatsApp contact history is persisted and updated idempotently");
var conversation = new WhatsAppConversation { Id="primary:447700900123", AccountId="primary", Phone="447700900123", LeadId=whatsappLead.Id, DisplayName=whatsappLead.DisplayName, LastMessage="Hello", LastMessageAt=DateTimeOffset.Now, UnreadCount=1 };
await repository.UpsertWhatsAppConversationAsync(conversation);
var whatsappMessage = new WhatsAppMessage { Id="primary:wamid-smoke", ProviderMessageId="wamid-smoke", AccountId="primary", ConversationId=conversation.Id, LeadId=whatsappLead.Id, Phone=conversation.Phone, Direction=WhatsAppMessageDirection.Incoming, Status=WhatsAppMessageStatus.Received, Body="Hello", Timestamp=DateTimeOffset.Now };
var messageInserted = await repository.UpsertWhatsAppMessageAsync(whatsappMessage);
var messageInsertedTwice = await repository.UpsertWhatsAppMessageAsync(whatsappMessage);
Check(messageInserted && !messageInsertedTwice && (await repository.GetWhatsAppMessagesAsync(conversation.Id)).Count == 1, "WhatsApp message idempotency");
await repository.UpdateWhatsAppMessageStatusAsync("primary", "wamid-smoke", WhatsAppMessageStatus.Read);
Check((await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single().Status == WhatsAppMessageStatus.Read, "WhatsApp message status persistence");
var quotedReply = new WhatsAppMessage
{
    Id="primary:wamid-reply", ProviderMessageId="wamid-reply", AccountId="primary", ConversationId=conversation.Id,
    LeadId=whatsappLead.Id, Phone=conversation.Phone, Direction=WhatsAppMessageDirection.Outgoing, Status=WhatsAppMessageStatus.Sent,
    Body="Here is the quotation", QuotedMessageId="wamid-smoke", QuotedText="Hello", QuotedFromMe=false, Timestamp=whatsappMessage.Timestamp.AddMinutes(-1)
};
await repository.UpsertWhatsAppMessageAsync(quotedReply);
var storedReply = (await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single(message => message.ProviderMessageId == "wamid-reply");
Check(storedReply.QuotedMessageId == "wamid-smoke" && storedReply.QuotedText == "Hello" && !storedReply.QuotedFromMe, "WhatsApp native reply context persists");
var revokedAt = DateTimeOffset.Now;
await repository.MarkWhatsAppMessageRevokedAsync("primary", "wamid-reply", revokedAt);
await repository.UpsertWhatsAppMessageAsync(quotedReply);
storedReply = (await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single(message => message.ProviderMessageId == "wamid-reply");
Check(storedReply.IsRevoked && storedReply.RevokedAt is not null && storedReply.QuotedMessageId == "wamid-smoke", "WhatsApp delete-for-everyone state persists and cannot regress");
await repository.MarkWhatsAppConversationReadAsync(conversation.Id);
var readConversation = (await repository.GetWhatsAppConversationsAsync()).Single(x => x.Id == conversation.Id);
Check(readConversation.UnreadCount == 0 && readConversation.LastReadAt is not null, "WhatsApp conversation unread cursor persistence");
var persistedReadCursor = readConversation.LastReadAt ?? throw new InvalidOperationException("WhatsApp read cursor was not persisted.");
var olderReadCursor = persistedReadCursor.AddMinutes(-5);
await repository.UpsertWhatsAppConversationAsync(new WhatsAppConversation
{
    Id=conversation.Id, AccountId=conversation.AccountId, Phone=conversation.Phone, LeadId=conversation.LeadId,
    DisplayName=conversation.DisplayName, LastMessage=conversation.LastMessage, LastMessageAt=conversation.LastMessageAt,
    UnreadCount=9, LastReadAt=olderReadCursor
});
readConversation = (await repository.GetWhatsAppConversationsAsync()).Single(x => x.Id == conversation.Id);
Check(readConversation.UnreadCount == 0 && readConversation.LastReadAt > olderReadCursor, "stale WhatsApp sync snapshots with older cursors cannot restore cleared unread badges");
await using (var unreadBridge = new WhatsAppConnectionManager())
{
    var unreadSync = new WhatsAppSyncService(repository, unreadBridge);
    using var lateMessageDocument = JsonDocument.Parse(JsonSerializer.Serialize(new
    {
        phone = conversation.Phone,
        id = "wamid-late-before-read-cursor",
        fromMe = false,
        timestamp = olderReadCursor.ToString("O"),
        source = "live",
        kind = "text",
        text = "Late bridge history item"
    }));
    var ingestMessage = typeof(WhatsAppSyncService).GetMethod("IngestMessageAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    await (Task)ingestMessage.Invoke(unreadSync, ["primary", lateMessageDocument.RootElement.Clone()])!;
}
readConversation = (await repository.GetWhatsAppConversationsAsync()).Single(x => x.Id == conversation.Id);
Check(readConversation.UnreadCount == 0, "late WhatsApp events older than the read cursor stay read after leaving and returning to Inbox");
var statusLead = new Lead { Id="status-lead", Name="Status Lead", PhoneE164="+14155550101", PhoneValid=true, LatestMessage="normal customer reply" };
await repository.UpsertLeadAsync(statusLead);
await repository.UpsertWhatsAppConversationAsync(new WhatsAppConversation
{
    Id="primary:14155550101", AccountId="primary", Phone="14155550101", LeadId=statusLead.Id,
    DisplayName=statusLead.DisplayName, LastMessage="normal customer reply", LastMessageAt=DateTimeOffset.Now
});
var statusUpdate = new WhatsAppMessage
{
    Id="primary:wamid-status", ProviderMessageId="wamid-status", AccountId="primary", ConversationId="primary:14155550101",
    LeadId=statusLead.Id, Phone="14155550101", Direction=WhatsAppMessageDirection.Incoming, Status=WhatsAppMessageStatus.Received,
    Body="https://example.com/status", IsStatusUpdate=true, StatusExpiresAt=DateTimeOffset.Now.AddHours(24), Timestamp=DateTimeOffset.Now
};
await repository.UpsertWhatsAppMessageAsync(statusUpdate);
var storedStatusUpdate = (await repository.GetWhatsAppMessagesAsync(statusUpdate.ConversationId)).Single();
Check(storedStatusUpdate.IsStatusUpdate && storedStatusUpdate.StatusExpiresAt is not null, "WhatsApp Status/update classification and 24-hour expiry persist");
Check(!LeadConnectionStatus.ApplyFromMessage(statusLead, statusUpdate) && statusLead.LatestMessage == "normal customer reply", "WhatsApp Status/update never becomes CRM reply evidence");
Check((await repository.GetLeadAsync("lead_james"))?.WhatsAppOptIn == true, "WhatsApp opt-in audit fields persisted");
await repository.SynchronizeLeadConnectionsFromInboxAsync([whatsappLead]);
var connectionLead = await repository.GetLeadAsync("lead_james");
Check(connectionLead?.CustomFields.Values.Any(value => value.Contains("\u5ba2\u6237\u5df2\u56de\u590d")) == true, "WhatsApp Inbox synchronizes latest connection status to customer dimensions");
var whatsappAccounts = await repository.GetWhatsAppAccountsAsync();
whatsappAccounts.Add(new WhatsAppAccount { Id="personal_test", Name="Personal Test" });
await repository.SaveWhatsAppAccountsAsync(whatsappAccounts);
Check((await repository.GetWhatsAppAccountsAsync()).Count == 2, "multiple personal WhatsApp accounts persisted");

var outgoingStatus = new WhatsAppMessage { Id="primary:wamid-out", ProviderMessageId="wamid-out", AccountId="primary", ConversationId=conversation.Id, LeadId=whatsappLead.Id, Phone=conversation.Phone, Direction=WhatsAppMessageDirection.Outgoing, Status=WhatsAppMessageStatus.Sent, Body="Status test", Timestamp=DateTimeOffset.Now };
await repository.UpsertWhatsAppMessageAsync(outgoingStatus);
outgoingStatus.Status = WhatsAppMessageStatus.Pending;
await repository.UpsertWhatsAppMessageAsync(outgoingStatus);
Check((await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single(x => x.Id == outgoingStatus.Id).Status == WhatsAppMessageStatus.Sent, "WhatsApp status cannot regress on duplicate event");
var deliveredAt = DateTimeOffset.Now.AddSeconds(2);
var readAt = deliveredAt.AddSeconds(3);
await repository.UpdateWhatsAppMessageStatusAsync("primary", "wamid-out", WhatsAppMessageStatus.Delivered, deliveredAt, deliveredAt);
await repository.UpdateWhatsAppMessageStatusAsync("primary", "wamid-out", WhatsAppMessageStatus.Read, readAt, deliveredAt, readAt);
var receiptedMessage = (await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single(x => x.Id == outgoingStatus.Id);
Check(receiptedMessage.Status == WhatsAppMessageStatus.Read && receiptedMessage.DeliveredAt == deliveredAt && receiptedMessage.ReadAt == readAt, "WhatsApp delivered/read receipt times persist");
var lateFailure = new WhatsAppMessage { Id="primary:wamid-late-failure", ProviderMessageId="wamid-late-failure", AccountId="primary", ConversationId=conversation.Id, LeadId=whatsappLead.Id, Phone=conversation.Phone, Direction=WhatsAppMessageDirection.Outgoing, Status=WhatsAppMessageStatus.Sent, Body="Late failure", Timestamp=DateTimeOffset.Now };
await repository.UpsertWhatsAppMessageAsync(lateFailure);
await repository.UpdateWhatsAppMessageStatusAsync("primary", lateFailure.ProviderMessageId, WhatsAppMessageStatus.Failed, DateTimeOffset.Now, failureReason:"WhatsApp returned an error");
Check((await repository.GetWhatsAppMessagesAsync(conversation.Id)).Single(x => x.Id == lateFailure.Id).Status == WhatsAppMessageStatus.Failed, "late WhatsApp transport errors correct an optimistic sent status");

var ipHandler = new IpMonitorHandler();
var ipMonitor = new PublicIpMonitor(repository, new HttpClient(ipHandler) { Timeout=TimeSpan.FromSeconds(2) });
var firstIp = await ipMonitor.CheckAsync("primary");
var changedIp = await ipMonitor.CheckAsync("primary", true);
var storedIp = await repository.GetWhatsAppIpStateAsync("primary");
Check(!firstIp.Changed && changedIp.Changed && storedIp?.PreviousIp == "198.51.100.10" && storedIp.CurrentIp == "203.0.113.20", "WhatsApp public IP baseline and change persist");

var suffixLead = new Lead
{
    Name="softsam", Country="美国", PhoneE164="+113373224256", PhoneValid=true,
    CustomFields=new Dictionary<string, string> { ["电话"]="13373224256" }
};
await repository.UpsertLeadAsync(suffixLead);
var suffixConversation = new WhatsAppConversation
{
    Id="primary:13373224256", AccountId="primary", Phone="13373224256", DisplayName="RI", LastMessage="Sure will",
    LastMessageAt=DateTimeOffset.Now, IsPinned=true, PinnedAt=DateTimeOffset.Now
};
await repository.UpsertWhatsAppConversationAsync(suffixConversation);
await repository.SynchronizeLeadConnectionsFromInboxAsync([suffixLead]);
var linkedSuffixConversation = await repository.GetWhatsAppConversationAsync("primary", "13373224256");
Check(linkedSuffixConversation?.LeadId == suffixLead.Id && linkedSuffixConversation.DisplayName == "softsam", "phone suffix match links CRM sidebar and prefers customer-list name");
Check(linkedSuffixConversation?.IsPinned == true && linkedSuffixConversation.PinnedAt is not null, "WhatsApp pinned conversation state persists");
var mediaMessage = new WhatsAppMessage
{
    Id="primary:wamid-media", ProviderMessageId="wamid-media", AccountId="primary", ConversationId=suffixConversation.Id,
    LeadId=suffixLead.Id, Phone=suffixConversation.Phone, Direction=WhatsAppMessageDirection.Outgoing, Status=WhatsAppMessageStatus.Sent,
    Kind="document", FileName="price-list.xlsx", MimeType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", MediaPath=Path.Combine(root, "price-list.xlsx"), Timestamp=DateTimeOffset.Now
};
await repository.UpsertWhatsAppMessageAsync(mediaMessage);
var storedMedia = (await repository.GetWhatsAppMessagesAsync(suffixConversation.Id)).Single(message => message.Id == mediaMessage.Id);
Check(storedMedia.Kind == "document" && storedMedia.FileName == "price-list.xlsx" && storedMedia.MediaPath == mediaMessage.MediaPath, "WhatsApp attachment metadata and local media path persist");
var repairDatabase = Path.Combine(root, "encoding-repair.db");
var repairRepository = new LocalRepository(repairDatabase);
await repairRepository.InitializeAsync();
var repairLead = (await repairRepository.GetLeadAsync("lead_james"))!;
var repairConversation = new WhatsAppConversation { Id="primary:encoding", AccountId="primary", Phone="14155550101", LeadId=repairLead.Id, DisplayName="Encoding", LastMessage="I鈥檒l send the file", LastMessageAt=DateTimeOffset.Now };
await repairRepository.UpsertWhatsAppConversationAsync(repairConversation);
await repairRepository.UpsertWhatsAppMessageAsync(new WhatsAppMessage { Id="primary:encoding", ProviderMessageId="encoding", AccountId="primary", ConversationId=repairConversation.Id, LeadId=repairLead.Id, Phone=repairConversation.Phone, Direction=WhatsAppMessageDirection.Incoming, Status=WhatsAppMessageStatus.Received, Body="I鈥檒l send the file", Timestamp=DateTimeOffset.Now });
await new LocalRepository(repairDatabase).InitializeAsync();
Check((await repairRepository.GetWhatsAppMessagesAsync(repairConversation.Id)).Single().Body == "I’ll send the file", "existing WhatsApp mojibake is repaired during database upgrade");

await using (var protocolClient = new WhatsAppBridgeClient())
{
    var protocolEventReceived = false;
    protocolClient.EventReceived += (_, bridgeEvent) => protocolEventReceived |= bridgeEvent.Name == "protocol_after_noise";
    var protocolLines = "Contaminating library output\n{\"type\":\"event\",\"event\":\"protocol_after_noise\",\"accountId\":\"primary\",\"data\":{}}\n";
    using var protocolStream = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(protocolLines)));
    var readerMethod = typeof(WhatsAppBridgeClient).GetMethod("ReadOutputAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    await (Task)readerMethod.Invoke(protocolClient, [protocolStream, CancellationToken.None])!;
    Check(protocolEventReceived && protocolClient.LastBridgeError.Contains("安全忽略"), "non-JSON bridge stdout no longer breaks successful send receipts");
}

var campaignBridge = new WhatsAppConnectionManager();
var campaignIpHandler = new MutableIpMonitorHandler("198.51.100.30");
var campaignIpMonitor = new PublicIpMonitor(repository, new HttpClient(campaignIpHandler) { Timeout=TimeSpan.FromSeconds(2) });
await using var campaigns = new CampaignAutomationService(repository, campaignBridge, campaignIpMonitor, new EmailService(repository));
var campaign = new WhatsAppCampaign
{
    Name="UK opt-in follow-up", TagFilter="UK", MessageTemplate="Hi {name}, following up about {product} for {company}.",
    SelectedLeadIds=[whatsappLead.Id], ScheduleMode=CampaignScheduleMode.Immediate,
    StartsAt=DateTimeOffset.Now.AddMinutes(10), IntervalValue=30, IntervalUnit=CampaignIntervalUnit.Seconds, IntervalMinutes=1, DailyLimit=25
};
var campaignPreview = await campaigns.PreviewAudienceAsync(campaign);
Check(campaignPreview.Count == 1 && campaignPreview.Single().Eligible && campaignPreview.Single().PreviewMessage.Contains("Reusable water bottles"), "campaign opt-in audience and template preview");
var importedNewLead = new Lead { Name="Imported new opportunity", PhoneE164="+14155550199", PhoneValid=true, Stage=LeadStage.New, WhatsAppOptIn=false };
await repository.UpsertLeadAsync(importedNewLead);
var importedAudience = await campaigns.PreviewAudienceAsync(new WhatsAppCampaign { Name="imported-new-check", SelectedLeadIds=[importedNewLead.Id], MessageTemplate="Hi {name}", StartsAt=DateTimeOffset.Now.AddHours(1) });
Check(importedAudience.Single().Eligible && importedAudience.Single().Reason.Contains("未记录营销同意"), "new imported opportunities with valid numbers are selectable for campaign");
await campaigns.SaveDraftAsync(campaign);
var scheduledCount = await campaigns.ApproveAndScheduleAsync(campaign, "smoke-test");
var campaignRecipients = await repository.GetCampaignRecipientsAsync(campaign.Id);
Check(scheduledCount == 1 && campaignRecipients.Single().Status == CampaignRecipientStatus.Queued && campaignRecipients.Single().ScheduledAt <= DateTimeOffset.Now.AddSeconds(10) && (await repository.GetCampaignAsync(campaign.Id)) is { Status: CampaignStatus.Scheduled, BaselinePublicIp: "198.51.100.30" }, "immediate campaign approval creates durable queue with IP baseline");
var uncertainRecipient = campaignRecipients.Single(); uncertainRecipient.Status = CampaignRecipientStatus.Sending;
await repository.SaveCampaignRecipientAsync(uncertainRecipient);
await repository.RecoverInterruptedCampaignRecipientsAsync();
Check((await repository.GetCampaignRecipientsAsync(campaign.Id)).Single().Status == CampaignRecipientStatus.Failed, "campaign uncertain send is never auto-retried");
whatsappLead.OptedOut = true;
await repository.UpsertLeadAsync(whatsappLead);
var optedOutPreview = await campaigns.PreviewAudienceAsync(new WhatsAppCampaign { Name="opt-out-check", TagFilter="UK", SelectedLeadIds=[whatsappLead.Id], MessageTemplate="Hi {name}", StartsAt=DateTimeOffset.Now.AddHours(1) });
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
secondAccountCampaign.SelectedLeadIds = [whatsappLead.Id];
secondAccountCampaign.ScheduleMode = CampaignScheduleMode.Immediate;
await campaigns.ApproveAndScheduleAsync(secondAccountCampaign, "smoke-test");
whatsappLead.CustomFields["采购周期"] = "Quarterly";
whatsappLead.CustomFields["name"] = "must-not-override-core-name";
await repository.UpsertLeadAsync(whatsappLead);
var templateFields = await campaigns.GetTemplateFieldsAsync();
var savedTemplate = await campaigns.SaveMessageTemplateAsync(new CampaignMessageTemplate { Name="custom field follow-up", Body="Hi {name}, next {采购周期}." });
Check(templateFields.Any(field => field.Key == "采购周期" && field.Source.Contains("客户列表")) && CampaignAutomationService.RenderTemplate(savedTemplate.Body, whatsappLead) == $"Hi {whatsappLead.Name}, next Quarterly." && (await repository.GetCampaignMessageTemplatesAsync()).Any(item => item.Id == savedTemplate.Id), "campaign templates use the same authoritative CRM field catalog and preserve imported fields");
CampaignSafetyStoppedEventArgs? safetyNotice = null;
campaigns.SafetyStopped += (_, args) => safetyNotice = args;
campaignIpHandler.CurrentIp = "203.0.113.31";
var safetyPassed = await campaigns.CheckSafetyValveAsync();
var safetyStoppedCampaign = await repository.GetCampaignAsync(campaign.Id);
var executionHistory = await campaigns.GetExecutionHistoryAsync();
Check(!safetyPassed && safetyStoppedCampaign is { Status: CampaignStatus.SafetyStopped, SafetyStopFromIp: "198.51.100.30", SafetyStopToIp: "203.0.113.31" } && (await repository.GetCampaignAsync(secondAccountCampaign.Id))?.Status == CampaignStatus.SafetyStopped && safetyNotice?.Campaigns.Count == 2 && safetyNotice.Campaigns.Sum(item => item.Failed) == 1 && executionHistory.Single(item => item.Campaign.Id == campaign.Id).StopOrNext.Contains("已处理"), "IP change safety valve stops all active outreach across accounts and preserves execution position");

var emailAccount = new EmailAccount
{
    Id="sales-email", DisplayName="Sales Team", EmailAddress="sales@example.com", UserName="sales@example.com",
    Provider=EmailProviderKind.Custom, ImapHost="imap.example.com", ImapPort=993, SmtpHost="smtp.example.com", SmtpPort=465,
    Status=EmailConnectionStatus.Connected
};
await repository.SaveEmailAccountAsync(emailAccount);
var emailLead = new Lead { Id="email-lead", Name="Email Buyer", Email="buyer@example.com", Stage=LeadStage.New, Grade="D", Score=0 };
await repository.UpsertLeadAsync(emailLead);
Check((await repository.GetLeadByEmailAsync(" BUYER@EXAMPLE.COM "))?.Id == emailLead.Id, "email address links inbox conversations to the authoritative CRM customer");
var emailConversation = new EmailConversation
{
    Id="sales-email:buyer@example.com", AccountId=emailAccount.Id, LeadId=emailLead.Id, PeerEmail=emailLead.Email,
    PeerName=emailLead.Name, Subject="Monthly order", LastMessage="Please quote 500 pcs monthly", LastMessageAt=DateTimeOffset.Now
};
await repository.UpsertEmailConversationAsync(emailConversation);
await repository.UpsertEmailMessageAsync(new EmailMessage
{
    Id="sales-email:mail-1", ProviderMessageId="mail-1", AccountId=emailAccount.Id, ConversationId=emailConversation.Id,
    LeadId=emailLead.Id, Direction=EmailMessageDirection.Incoming, Status=EmailMessageStatus.Received,
    FromAddress=emailLead.Email, ToAddresses=[emailAccount.EmailAddress], Subject=emailConversation.Subject,
    TextBody=emailConversation.LastMessage, Timestamp=DateTimeOffset.Now
});
Check((await repository.GetEmailMessagesForLeadAsync(emailLead.Id)).Single().TextBody.Contains("500 pcs"), "email history persists and remains linked to the customer record");
var emailCampaign = new WhatsAppCampaign
{
    Id="email-campaign", Channel=CampaignChannel.Email, AccountId=emailAccount.Id, Name="Email nurture",
    EmailSubjectTemplate="Follow-up for {name}", MessageTemplate="Hi {name}, we can support your monthly order.",
    SelectedLeadIds=[emailLead.Id], ScheduleMode=CampaignScheduleMode.Immediate, IntervalValue=30,
    IntervalUnit=CampaignIntervalUnit.Seconds, DailyLimit=20
};
var emailAudience = await campaigns.PreviewAudienceAsync(emailCampaign);
Check(emailAudience.Single().Eligible, "email campaign selects CRM customers with valid email addresses");
Check(await campaigns.ApproveAndScheduleAsync(emailCampaign, "smoke-test") == 1, "email campaign creates a durable recipient queue without requiring a WhatsApp IP baseline");
var storedEmailCampaign = await repository.GetCampaignAsync(emailCampaign.Id);
var storedEmailRecipient = (await repository.GetCampaignRecipientsAsync(emailCampaign.Id)).Single();
var channelHistory = (await campaigns.GetExecutionHistoryAsync()).Single(item => item.Campaign.Id == emailCampaign.Id);
Check(storedEmailCampaign is { Channel: CampaignChannel.Email, BaselinePublicIp: "" } && storedEmailRecipient.Email == emailLead.Email && storedEmailRecipient.RenderedSubject.Contains(emailLead.Name) && channelHistory.Channel.Length > 0, "campaign history distinguishes email from WhatsApp and stores rendered email subject/body");

var deliveryRoot = Path.Combine(root, "campaign-delivery");
var deliveryRepository = new LocalRepository(Path.Combine(deliveryRoot, "delivery.db"));
await deliveryRepository.InitializeAsync();
var deliveryLead = new Lead { Id="delivery-lead", Name="Delivery Lead", PhoneE164="+14155557777", PhoneValid=true };
await deliveryRepository.UpsertLeadAsync(deliveryLead);
var deliveryCampaign = new WhatsAppCampaign { Id="delivery-campaign", Name="Delivery accounting", Status=CampaignStatus.Completed, ApprovedAt=DateTimeOffset.Now, StartsAt=DateTimeOffset.Now };
await deliveryRepository.SaveCampaignAsync(deliveryCampaign);
await deliveryRepository.SaveCampaignRecipientAsync(new CampaignRecipient
{
    Id="delivery-recipient", CampaignId=deliveryCampaign.Id, LeadId=deliveryLead.Id, AccountId="primary", Phone=deliveryLead.PhoneE164,
    DisplayName=deliveryLead.Name, RenderedMessage="Hello", Status=CampaignRecipientStatus.Sent, ProviderMessageId="delivery-provider",
    ScheduledAt=DateTimeOffset.Now, NextAttemptAt=DateTimeOffset.Now, SentAt=DateTimeOffset.Now
});
await deliveryRepository.UpsertWhatsAppConversationAsync(new WhatsAppConversation
{
    Id="primary:14155557777", AccountId="primary", Phone="14155557777", LeadId=deliveryLead.Id,
    DisplayName=deliveryLead.Name, LastMessage="Hello", LastMessageAt=DateTimeOffset.Now
});
await deliveryRepository.UpsertWhatsAppMessageAsync(new WhatsAppMessage
{
    Id="primary:delivery-provider", ProviderMessageId="delivery-provider", AccountId="primary", ConversationId="primary:14155557777",
    LeadId=deliveryLead.Id, Phone="14155557777", Direction=WhatsAppMessageDirection.Outgoing, Status=WhatsAppMessageStatus.Failed,
    Body="Hello", FailureReason="WhatsApp 返回发送错误", Timestamp=DateTimeOffset.Now
});
var deliveryBridge = new WhatsAppConnectionManager();
var deliveryIpMonitor = new PublicIpMonitor(deliveryRepository, new HttpClient(new MutableIpMonitorHandler("198.51.100.40")) { Timeout=TimeSpan.FromSeconds(2) });
await using (var deliveryCampaigns = new CampaignAutomationService(deliveryRepository, deliveryBridge, deliveryIpMonitor, new EmailService(deliveryRepository)))
{
    await deliveryCampaigns.StartAsync();
    var repairedRecipient = (await deliveryRepository.GetCampaignRecipientsAsync(deliveryCampaign.Id)).Single();
    var repairedSummary = (await deliveryCampaigns.GetExecutionHistoryAsync()).Single();
    Check(repairedRecipient.Status == CampaignRecipientStatus.Failed && repairedRecipient.SentAt is null && repairedSummary.Sent == 0 && repairedSummary.Failed == 1 && repairedSummary.SuccessRate == "0%", "campaign history reconciles persisted WhatsApp failures instead of reporting false success");
}

var receiptCampaign = new WhatsAppCampaign { Id="receipt-campaign", Name="Receipt accounting", Status=CampaignStatus.Running, ApprovedAt=DateTimeOffset.Now, StartsAt=DateTimeOffset.Now };
await deliveryRepository.SaveCampaignAsync(receiptCampaign);
await deliveryRepository.SaveCampaignRecipientAsync(new CampaignRecipient
{
    Id="receipt-recipient", CampaignId=receiptCampaign.Id, LeadId=deliveryLead.Id, AccountId="primary", Phone=deliveryLead.PhoneE164,
    DisplayName=deliveryLead.Name, RenderedMessage="Pending", Status=CampaignRecipientStatus.Sending, ProviderMessageId="receipt-provider",
    ScheduledAt=DateTimeOffset.Now, NextAttemptAt=DateTimeOffset.Now
});
await using (var receiptCampaigns = new CampaignAutomationService(deliveryRepository, deliveryBridge, deliveryIpMonitor, new EmailService(deliveryRepository)))
{
    using var receiptJson = System.Text.Json.JsonDocument.Parse("{\"id\":\"receipt-provider\",\"status\":0,\"failureReason\":\"WhatsApp returned send error\"}");
    var receiptEvent = new WhatsAppBridgeEvent("message_status", "primary", receiptJson.RootElement.Clone());
    var receiptHandler = typeof(CampaignAutomationService).GetMethod("HandleDeliveryReceiptAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
    await (Task)receiptHandler.Invoke(receiptCampaigns, [receiptEvent, CancellationToken.None])!;
    var failedReceipt = (await deliveryRepository.GetCampaignRecipientsAsync(receiptCampaign.Id)).Single();
    var receiptSummary = (await receiptCampaigns.GetExecutionHistoryAsync()).Single(item => item.Campaign.Id == receiptCampaign.Id);
    Check(failedReceipt.Status == CampaignRecipientStatus.Failed && receiptSummary.Sent == 0 && receiptSummary.Failed == 1, "asynchronous WhatsApp error receipts update campaign recipient and aggregate quality statistics");
}
await repository.SaveOnboardingStateAsync(new OnboardingState { Completed=true, GuideVersion=6, ModuleGuideVersion=1, SeenModuleGuides=["dashboard", "customers", "settings"], CompletedAt=DateTimeOffset.Now });
var persistedOnboarding = await repository.GetOnboardingStateAsync();
Check(persistedOnboarding is { Completed: true, GuideVersion: 6, ModuleGuideVersion: 1 } && persistedOnboarding.SeenModuleGuides.SequenceEqual(["dashboard", "customers", "settings"]), "global and per-module onboarding completion persists");

await using (var embeddedBridge = new WhatsAppConnectionManager())
{
    await embeddedBridge.StartAsync("embedded_smoke");
    var bridgePing = await embeddedBridge.PingAsync();
    Check(bridgePing.TryGetProperty("bridge", out var bridgeName) && bridgeName.GetString() == "WAFlow.WhatsApp.Bridge", "embedded bridge EXE extraction and startup");
    await embeddedBridge.LogoutAsync();
}

var analysisJson = V2AnalysisJson("Could you quote 300 units?");
var draftJson = WAFlow.Core.Infrastructure.Json.Serialize(new { purpose="follow_up", language="en", body="Hi Elena, thank you for confirming 300 units. I will verify the lead time and share the next details with you.", rationale=new[] { "承接客户的数量与交期问题" }, assumptions=Array.Empty<string>(), risks=new[] { "交期需人工确认" } });
var invalidAnalysisJson = "{\"score\":99,\"grade\":\"A\",\"factors\":[],\"stage\":\"new\",\"confidence\":0.8,\"evidence\":[],\"profileSummary\":\"x\",\"customerSegment\":\"x\",\"nextAction\":\"x\",\"risks\":[]}";
var handler = new QueueHandler([Envelope(analysisJson), Envelope(draftJson), Envelope(invalidAnalysisJson), Envelope(invalidAnalysisJson)]);
var deepSeek = new DeepSeekService(repository, new FakeSecretStore("sk-test-redacted"), new HttpClient(handler) { Timeout=TimeSpan.FromSeconds(5) });
await repository.SaveAppSettingsAsync(new AppSettings { DeepSeekBaseUrl="https://api.deepseek.com", DeepSeekModel="deepseek-chat" });
var catalog = await deepSeek.DiscoverModelsAsync("https://api.deepseek.com");
Check(catalog.Models.SequenceEqual(["deepseek-chat", "deepseek-reasoner"]), "AI provider model catalog is fetched and sorted");
var analyzed = await deepSeek.AnalyzeLeadAsync((await repository.GetLeadAsync("lead_elena"))!);
Check(analyzed is { AnalysisStatus: AnalysisStatus.Succeeded, Score: 88, BaseProfileScore: 78, BehaviorSignalScore: 10, PurchaseProbability: 76, AnalysisContractVersion: 2, AiScoreApplied: true } && analyzed.ScoreFactors.Count == 6 && analyzed.Evidence.Count >= 7, "DeepSeek V2 structured analysis success");
var analyzedDashboard = await repository.GetDashboardAsync();
Check(analyzedDashboard.Grades["A"] >= 1 && analyzedDashboard.PriorityLeads.Any(lead => lead.Id == analyzed.Id), "Dashboard grade distribution reads validated V2 AI scores");
var generated = await deepSeek.GenerateDraftAsync(analyzed, "follow_up", "en", "");
Check(generated.Body.StartsWith("Hi Elena") && generated.Status == DraftStatus.Draft, "DeepSeek structured draft success");
try
{
    await deepSeek.AnalyzeLeadAsync((await repository.GetLeadAsync("lead_ahmed"))!);
    Check(false, "DeepSeek invalid structure rejected");
}
catch (DeepSeekException error) { Check(error.Code == "invalid_structured_output" && error.Retryable, "DeepSeek invalid structure rejected"); }
var failedAnalysisLead = await repository.GetLeadAsync("lead_ahmed");
Check(failedAnalysisLead is { Grade: "D", Score: 0, AnalysisStatus: AnalysisStatus.RetryableFailed, AiScoreApplied: false }, "AI analysis failure remains D/0 and is retryable");
Check(handler.Requests.All(x => x.Authorization == "Bearer sk-test-redacted") && handler.Requests.Count(x => x.Method == "GET" && x.Uri == "https://api.deepseek.com/models") == 1 && handler.Requests.Count(x => x.Method == "POST" && x.Uri == "https://api.deepseek.com/chat/completions") == 4, "AI model discovery and chat requests use the server-side key");
Check(handler.RequestBodies.Any(body => body.Contains("dimension_weights") && body.Contains("recentMessages")), "AI request includes the V2 contract, imported CRM fields and WhatsApp history");

var automationLead = new Lead { Id="reply-automation-lead", Name="Reply Buyer", PhoneE164="+8829990000123", PhoneValid=true };
await repository.UpsertLeadAsync(automationLead);
var automationConversation = new WhatsAppConversation { Id="primary:8829990000123", AccountId="primary", Phone="8829990000123", LeadId=automationLead.Id, DisplayName=automationLead.Name, LastMessage="Please quote 300 pcs", LastMessageAt=DateTimeOffset.Now };
await repository.UpsertWhatsAppConversationAsync(automationConversation);
var automationMessage = new WhatsAppMessage { Id="primary:reply-auto", ProviderMessageId="reply-auto", AccountId="primary", ConversationId=automationConversation.Id, LeadId=automationLead.Id, Phone=automationConversation.Phone, Direction=WhatsAppMessageDirection.Incoming, Status=WhatsAppMessageStatus.Received, Body="I need 500 pcs monthly", Timestamp=DateTimeOffset.Now };
await repository.UpsertWhatsAppMessageAsync(automationMessage);
var automationHandler = new QueueHandler([Envelope(V2AnalysisJson("I need 500 pcs monthly"))]);
var automationProvider = new DeepSeekService(repository, new FakeSecretStore("sk-automation"), new HttpClient(automationHandler) { Timeout=TimeSpan.FromSeconds(5) });
await using (var automationBridge = new WhatsAppConnectionManager())
{
    var automationSync = new WhatsAppSyncService(repository, automationBridge);
    await using var automation = new LeadIntelligenceAutomationService(repository, automationProvider, automationSync);
    var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    automation.AnalysisChanged += (_, change) => { if (change.LeadId == automationLead.Id && change.Status == AnalysisStatus.Succeeded) completed.TrySetResult(); };
    await automation.QueueLeadForReplyAsync(automationMessage);
    var queuedLead = await repository.GetLeadAsync(automationLead.Id);
    Check(queuedLead is { Grade: "D", Score: 0, AnalysisStatus: AnalysisStatus.Queued, AiScoreApplied: false } && queuedLead.LatestReplySignals.Count == 0, "WhatsApp reply queues AI analysis at D/0 without local keyword scoring");
    await automation.StartAsync();
    await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var automaticallyAnalyzed = await repository.GetLeadAsync(automationLead.Id);
    Check(automaticallyAnalyzed is { Grade: "A", Score: 88, BehaviorSignalScore: 10, AnalysisStatus: AnalysisStatus.Succeeded, AiScoreApplied: true, AnalysisContractVersion: 2 } && automaticallyAnalyzed.LastAnalyzedAt is not null && automaticallyAnalyzed.BehaviorSignals.Any(signal => signal.Evidence == "I need 500 pcs monthly"), "AI recognizes the 500 pcs monthly purchase signal and updates lead intelligence");
    Check(automationHandler.RequestBodies.Any(body => body.Contains("I need 500 pcs monthly")), "the exact new WhatsApp message is supplied to the AI Provider");
}

var bulkRoot = Path.Combine(root, "bulk-lead-analysis");
var bulkRepository = new LocalRepository(Path.Combine(bulkRoot, "bulk.db"));
await bulkRepository.InitializeAsync();
await bulkRepository.UpsertLeadAsync(new Lead { Id="bulk-one", Name="Bulk One", PhoneE164="+14155550101", PhoneValid=true });
await bulkRepository.UpsertLeadAsync(new Lead { Id="bulk-two", Name="Bulk Two", PhoneE164="+14155550102", PhoneValid=true });
await bulkRepository.RemoveDemoLeadsIfRealDataExistsAsync();
await bulkRepository.SaveAppSettingsAsync(new AppSettings { DeepSeekBaseUrl="https://api.deepseek.com", DeepSeekModel="deepseek-chat" });
var bulkHandler = new QueueHandler([Envelope(invalidAnalysisJson), Envelope(invalidAnalysisJson), Envelope(V2AnalysisJson("Please send a quotation for 500 pcs"))]);
var bulkProvider = new DeepSeekService(bulkRepository, new FakeSecretStore("sk-bulk"), new HttpClient(bulkHandler) { Timeout=TimeSpan.FromSeconds(5) });
await using (var bulkBridge = new WhatsAppConnectionManager())
{
    var bulkSync = new WhatsAppSyncService(bulkRepository, bulkBridge);
    await using var bulkAutomation = new LeadIntelligenceAutomationService(bulkRepository, bulkProvider, bulkSync);
    var bulkResult = await bulkAutomation.AnalyzeAllLeadsAsync();
    var bulkDashboard = await bulkRepository.GetDashboardAsync();
    Check(bulkResult is { Total: 2, Succeeded: 1, Failed: 1 } && bulkHandler.RequestBodies.Count == 3, "bulk lead analysis continues after one customer fails");
    Check(bulkDashboard.Grades["A"] == 1 && bulkDashboard.Grades["D"] == 1, "bulk AI results update Dashboard while failed customers remain D/0");
}

var reportRoot = Path.Combine(root, "customer-intelligence-report");
var reportRepository = new LocalRepository(Path.Combine(reportRoot, "reports.db"));
await reportRepository.InitializeAsync();
var reportLead = new Lead
{
    Id="report-customer", Name="Monthly Buyer", Country="美国", PhoneE164="+14155558888", PhoneValid=true,
    ProductInterest="家居用品", Owner="Frank", Stage=LeadStage.Negotiation, Grade="A", Score=86,
    AnalysisContractVersion=LeadIntelligenceContract.Version, AiScoreApplied=true, AnalysisStatus=AnalysisStatus.Succeeded,
    ScoreFactors=
    [
        new LeadFactor { Key="paid_marketing_willingness", Score=20, MaxScore=25, Rationale="有增长意愿", Evidence=["历史分析"] },
        new LeadFactor { Key="supply_stability", Score=18, MaxScore=20, Rationale="采购稳定", Evidence=["月度需求"] },
        new LeadFactor { Key="ecommerce_foundation", Score=15, MaxScore=15, Rationale="Amazon 渠道", Evidence=["CRM"] },
        new LeadFactor { Key="private_traffic", Score=12, MaxScore=15, Rationale="WhatsApp 社群", Evidence=["CRM"] },
        new LeadFactor { Key="existing_sales", Score=12, MaxScore=15, Rationale="已有销售", Evidence=["CRM"] },
        new LeadFactor { Key="materials_readiness", Score=9, MaxScore=10, Rationale="素材较完整", Evidence=["CRM"] }
    ],
    CustomFields=new Dictionary<string, string> { ["销售渠道"]="Amazon", ["采购周期"]="每月", ["原始需求"]="500 pcs monthly" }
};
await reportRepository.UpsertLeadAsync(reportLead);
var reportConversation = new WhatsAppConversation { Id="primary:14155558888", AccountId="primary", Phone="14155558888", LeadId=reportLead.Id, DisplayName=reportLead.Name, LastMessage="I need 500 pcs monthly.", LastMessageAt=DateTimeOffset.Now };
await reportRepository.UpsertWhatsAppConversationAsync(reportConversation);
for (var index = 0; index < 85; index++)
    await reportRepository.UpsertWhatsAppMessageAsync(new WhatsAppMessage
    {
        Id=$"primary:report-{index}", ProviderMessageId=$"report-{index}", AccountId="primary", ConversationId=reportConversation.Id,
        LeadId=reportLead.Id, Phone=reportConversation.Phone, Direction=index % 2 == 0 ? WhatsAppMessageDirection.Incoming : WhatsAppMessageDirection.Outgoing,
        Status=index % 2 == 0 ? WhatsAppMessageStatus.Received : WhatsAppMessageStatus.Read,
        Body=index == 84 ? "I need 500 pcs monthly." : index % 2 == 0 ? $"Customer message {index}" : $"Sales reply {index}", Timestamp=DateTimeOffset.Now.AddMinutes(index - 85)
    });
var reportCampaign = new WhatsAppCampaign { Id="report-campaign", Name="月度采购跟进", Status=CampaignStatus.Completed, StartsAt=DateTimeOffset.Now.AddDays(-1) };
await reportRepository.SaveCampaignAsync(reportCampaign);
await reportRepository.SaveCampaignRecipientAsync(new CampaignRecipient { Id="report-recipient", CampaignId=reportCampaign.Id, LeadId=reportLead.Id, Phone=reportLead.PhoneE164, DisplayName=reportLead.Name, RenderedMessage="Hi, checking your monthly plan.", Status=CampaignRecipientStatus.Sent, ScheduledAt=DateTimeOffset.Now.AddDays(-1), SentAt=DateTimeOffset.Now.AddDays(-1).AddMinutes(1) });
await reportRepository.LogEventAsync("lead_stage_changed", reportLead.Id, null, "new -> negotiation");
await reportRepository.SaveAnalysisRunAsync("report-analysis-run", reportLead.Id, "succeeded", "deepseek-test", new LeadAnalysis { Score=86, Grade="A", ProfileSummary="成熟 Amazon 买家" }, null);
var reportProvider = new FakeStructuredReportProvider();
var customerAnalysis = new CustomerAnalysisService(reportRepository, reportProvider);
var firstReport = await customerAnalysis.GenerateAsync(reportLead.Id);
var reportSteps = await reportRepository.GetCustomerAnalysisStepsAsync(firstReport.Id);
Check(firstReport is { Status: CustomerReportStatus.Succeeded, Version: 1 } && firstReport.SourceSnapshot.WhatsAppMessages.Count == 85 && firstReport.SourceSnapshot.CampaignTouches.Count == 1 && firstReport.SourceSnapshot.LeadAnalysisHistory.Count == 1, "customer intelligence report snapshots CRM, all WhatsApp history, automation and Lead Intelligence history");
Check(reportSteps.Count(step => step.Status == CustomerReportStepStatus.Succeeded) == 5 && reportProvider.FactExtractionCalls == 2, "customer intelligence report persists every multi-stage result and batches all 85 messages without truncation");
Check(firstReport.Report.ManagementSummary.Length is >= 300 and <= 500 && firstReport.Report.WhatsAppAnalysis.Quotes.Single().Original == "I need 500 pcs monthly." && firstReport.Report.WhatsAppAnalysis.Quotes.Single().ChineseMeaning.Contains("每月采购500件"), "customer intelligence report is Chinese-first while preserving and explaining the original customer quote");
var reportExports = new CustomerReportExportService(reportRepository);
var wordReportPath = Path.Combine(reportRoot, "Monthly Buyer 客户背景调查报告.docx");
var pdfReportPath = Path.Combine(reportRoot, "Monthly Buyer 客户背景调查报告.pdf");
await reportExports.ExportWordAsync(firstReport, wordReportPath);
await reportExports.ExportPdfAsync(firstReport, pdfReportPath);
Check(File.Exists(wordReportPath) && new FileInfo(wordReportPath).Length > 5_000 && File.ReadAllBytes(wordReportPath).Take(2).SequenceEqual(new byte[] { 0x50, 0x4B }), "professional customer report exports a valid non-empty DOCX package");
Check(File.Exists(pdfReportPath) && new FileInfo(pdfReportPath).Length > 10_000 && Encoding.ASCII.GetString(File.ReadAllBytes(pdfReportPath), 0, 5) == "%PDF-", "professional customer report exports a valid non-empty PDF document");
var secondReport = await customerAnalysis.GenerateAsync(reportLead.Id);
var reportHistory = await customerAnalysis.GetHistoryAsync(reportLead.Id);
Check(secondReport.Version == 2 && reportHistory.Select(report => report.Version).SequenceEqual([2, 1]) && reportHistory.All(report => report.CustomerId == reportLead.Id), "customer intelligence reports support re-analysis, immutable versions and history comparison");
Check((await reportRepository.GetLeadAsync(reportLead.Id)) is { Score: 86, Grade: "A" }, "customer report generation never overwrites authoritative CRM or Lead Intelligence data");
var fallbackAnalysis = new CustomerAnalysisService(reportRepository, new AlwaysInvalidStructuredReportProvider());
var fallbackReport = await fallbackAnalysis.GenerateAsync(reportLead.Id);
var fallbackSteps = await reportRepository.GetCustomerAnalysisStepsAsync(fallbackReport.Id);
Check(fallbackReport.Status == CustomerReportStatus.Succeeded && fallbackReport.Version == 3 && fallbackReport.Error.Contains("当前全部可用资料") && fallbackReport.Report.ManagementSummary.Length is >= 300 and <= 500, "customer report falls back to current verified data when AI structured output remains invalid");
Check(fallbackSteps.Count == 5 && fallbackSteps.All(step => step.Status == CustomerReportStepStatus.Succeeded) && fallbackReport.Report.EvidenceLedger.Count > 0, "partial-data customer report preserves a complete auditable pipeline and evidence ledger");
var customerBrain = new CustomerBrainService(reportRepository);
var firstBrain = await customerBrain.RefreshAsync(reportLead.Id);
var firstBrainAgain = await customerBrain.RefreshAsync(reportLead.Id);
var firstRecommendations = await reportRepository.GetAiRecommendationHistoryAsync(reportLead.Id);
var behaviorTimeline = await reportRepository.GetCustomerBehaviorTimelineAsync(reportLead.Id);
Check(firstBrain is { Version: 1, CustomerId: "report-customer" } && firstBrain.Coverage.HasCrmData && firstBrain.Coverage.HasWhatsAppHistory && firstBrain.Coverage.HasLeadAnalysis && firstBrain.Coverage.HasCustomerReport && firstBrain.Coverage.HasCampaignHistory, "Customer Brain materializes one cross-channel profile with explicit data coverage");
Check(firstBrain.Statements.Any(item => item.Nature == IntelligenceStatementNature.Fact && item.Source == "CRM") && firstBrain.Statements.Any(item => item.Nature == IntelligenceStatementNature.Inference) && firstBrain.Statements.Any(item => item.Nature == IntelligenceStatementNature.Recommendation), "Customer Brain keeps facts, AI inference and sales recommendations distinct");
Check(firstBrainAgain.Version == firstBrain.Version && firstRecommendations.Count == 1, "Customer Brain refresh is idempotent and does not duplicate versions or recommendations");
Check(behaviorTimeline.Count >= 88 && behaviorTimeline.Any(item => item.SourceType == "whatsapp_message") && behaviorTimeline.Any(item => item.SourceType == "campaign_recipient") && behaviorTimeline.Any(item => item.SourceType == "customer_analysis_report"), "Customer Brain builds an idempotent behavior timeline from conversations, campaigns and reports");
var stagedBrainService = new CustomerBrainService(reportRepository, new FakeCustomerBrainProvider());
var decisionBrain = await stagedBrainService.AnalyzeAsync(reportLead.Id);
var brainRuns = await reportRepository.GetCustomerBrainRunsAsync(reportLead.Id);
var followUpTasks = await reportRepository.GetFollowUpTasksAsync(reportLead.Id);
var customerEvents = await reportRepository.GetCustomerEventsAsync(reportLead.Id);
var leadAfterDecision = await reportRepository.GetLeadAsync(reportLead.Id);
Check(decisionBrain is { DecisionStatus: CustomerBrainDecisionStatus.Current, PurchaseProbability: 74, SuggestedStage: LeadStage.RequirementConfirmed }
    && decisionBrain.HasCurrentDecision && decisionBrain.Confidence == .82, "Customer Brain staged AI pipeline produces a current evidence-bound opportunity decision");
Check(brainRuns.First() is { Status: CustomerBrainRunStatus.Succeeded }
    && !string.IsNullOrWhiteSpace(brainRuns.First().UnderstandingJson)
    && !string.IsNullOrWhiteSpace(brainRuns.First().OpportunityJson)
    && !string.IsNullOrWhiteSpace(brainRuns.First().RecommendationJson), "Customer Brain persists structured intermediate results for understanding, opportunity and recommendation stages");
Check(followUpTasks.Single() is { Status: FollowUpTaskStatus.Proposed, Priority: FollowUpPriority.High }
    && customerEvents.Any(item => item.EventType == "follow_up_proposed")
    && customerEvents.Any(item => item.EventType == "customer_brain_analyzed"), "Customer Brain turns its recommendation into an auditable personal follow-up task and customer event timeline");
Check(leadAfterDecision is { Score: 86, Grade: "A", Stage: LeadStage.Negotiation, PurchaseProbability: 0 }, "Customer Brain decision remains advisory and never overwrites CRM stage or Lead Intelligence output");
var dashboardAfterBrain = await reportRepository.GetDashboardAsync();
Check(dashboardAfterBrain.PendingFollowUps >= 1, "personal sales command center counts due Customer Brain follow-up tasks");
try
{
    await new CustomerBrainService(reportRepository, new AlwaysInvalidStructuredReportProvider()).AnalyzeAsync(reportLead.Id);
}
catch (DeepSeekException)
{
}
var preservedDecision = await reportRepository.GetCustomerIntelligenceProfileAsync(reportLead.Id);
var failedBrainRun = (await reportRepository.GetCustomerBrainRunsAsync(reportLead.Id)).First();
Check(preservedDecision is { DecisionStatus: CustomerBrainDecisionStatus.Current, PurchaseProbability: 74 }
    && failedBrainRun.Status == CustomerBrainRunStatus.RetryableFailed, "Customer Brain provider failure is retryable and preserves the last valid decision");
reportLead.CustomFields["目标价格状态"] = "待确认";
await reportRepository.UpsertLeadAsync(reportLead);
var changedBrain = await stagedBrainService.RefreshAsync(reportLead.Id);
var unchangedLeadAfterBrain = await reportRepository.GetLeadAsync(reportLead.Id);
Check(changedBrain.Version == decisionBrain.Version + 1 && changedBrain.SourceSnapshotHash != decisionBrain.SourceSnapshotHash
    && changedBrain.CreatedAt == firstBrain.CreatedAt && changedBrain.DecisionStatus == CustomerBrainDecisionStatus.Stale
    && changedBrain.PurchaseProbability == decisionBrain.PurchaseProbability, "Customer Brain marks the previous AI decision stale when source semantics change without discarding it");
Check(unchangedLeadAfterBrain is { Score: 86, Grade: "A", AnalysisStatus: AnalysisStatus.Succeeded } && unchangedLeadAfterBrain.CustomFields["目标价格状态"] == "待确认", "Customer Brain never overwrites authoritative CRM or Lead Intelligence fields");
var brainRecommendation = (await reportRepository.GetAiRecommendationHistoryAsync(reportLead.Id)).First();
var brainAction = new SalesActionRecord
{
    CustomerId=reportLead.Id, RecommendationId=brainRecommendation.Id, ActionType="follow_up",
    Description="确认SKU、价格和交期", Owner="Frank", Status=SalesActionStatus.Completed, CompletedAt=DateTimeOffset.Now, Outcome="客户已回复"
};
await reportRepository.SaveSalesActionAsync(brainAction);
await reportRepository.SaveAiLearningFeedbackAsync(new AiLearningFeedback
{
    CustomerId=reportLead.Id, RecommendationId=brainRecommendation.Id, ActionId=brainAction.Id,
    Outcome="客户回复并补充采购条件", Helpful=true, Note="建议有助于推进需求确认"
});
await reportRepository.InitializeAsync();
Check((await reportRepository.GetCustomerIntelligenceProfileAsync(reportLead.Id))?.Version == changedBrain.Version
    && (await reportRepository.GetSalesActionsAsync(reportLead.Id)).Single().Status == SalesActionStatus.Completed
    && (await reportRepository.GetAiLearningFeedbackAsync(reportLead.Id)).Single().Helpful
    && (await reportRepository.GetFollowUpTasksAsync(reportLead.Id)).Single().Priority == FollowUpPriority.High, "Customer Brain migration is additive and preserves tasks, actions and outcome learning across restarts");
var keepArtifactIndex = Array.IndexOf(args, "--keep-report-artifacts");
if (keepArtifactIndex >= 0 && keepArtifactIndex + 1 < args.Length)
{
    var artifactDirectory = Path.GetFullPath(args[keepArtifactIndex + 1]);
    Directory.CreateDirectory(artifactDirectory);
    File.Copy(wordReportPath, Path.Combine(artifactDirectory, "Customer Intelligence Report QA.docx"), true);
    File.Copy(pdfReportPath, Path.Combine(artifactDirectory, "Customer Intelligence Report QA.pdf"), true);
}

var lifecycleRoot = Path.Combine(root, "lifecycle");
var lifecycleRepository = new LocalRepository(Path.Combine(lifecycleRoot, "lifecycle.db"));
await lifecycleRepository.InitializeAsync();
var lifecycleLead = new Lead { Id="real-customer", Name="Real Customer", PhoneE164="+14155550999", PhoneValid=true };
await lifecycleRepository.UpsertLeadAsync(lifecycleLead);
var lifecycleConversation = new WhatsAppConversation { Id="primary:14155550999", AccountId="primary", Phone="14155550999", LeadId=lifecycleLead.Id, DisplayName=lifecycleLead.Name, LastMessage="hello", LastMessageAt=DateTimeOffset.Now };
await lifecycleRepository.UpsertWhatsAppConversationAsync(lifecycleConversation);
await lifecycleRepository.UpsertWhatsAppMessageAsync(new WhatsAppMessage { Id="primary:lifecycle", ProviderMessageId="lifecycle", AccountId="primary", ConversationId=lifecycleConversation.Id, LeadId=lifecycleLead.Id, Phone=lifecycleConversation.Phone, Direction=WhatsAppMessageDirection.Incoming, Status=WhatsAppMessageStatus.Received, Body="hello" });
await lifecycleRepository.InitializeAsync();
Check((await lifecycleRepository.GetLeadsAsync()).Select(lead => lead.Id).SequenceEqual([lifecycleLead.Id]), "demo customers are removed automatically once real customer data exists");
Check(await lifecycleRepository.DeleteLeadAsync(lifecycleLead.Id) && await lifecycleRepository.GetLeadAsync(lifecycleLead.Id) is null, "customer can be deleted manually");
Check((await lifecycleRepository.GetWhatsAppConversationsAsync()).Single().LeadId == "" && (await lifecycleRepository.GetWhatsAppMessagesAsync(lifecycleConversation.Id)).Single().LeadId == "", "customer deletion retains WhatsApp history and removes customer links");
var bulkDeleteOne = new Lead { Id="bulk-delete-one", Name="Bulk Delete One", PhoneE164="+14155551001", PhoneValid=true };
var bulkDeleteTwo = new Lead { Id="bulk-delete-two", Name="Bulk Delete Two", PhoneE164="+14155551002", PhoneValid=true };
await lifecycleRepository.UpsertLeadAsync(bulkDeleteOne);
await lifecycleRepository.UpsertLeadAsync(bulkDeleteTwo);
var bulkDeleted = await lifecycleRepository.DeleteLeadsAsync([bulkDeleteOne.Id, bulkDeleteTwo.Id, bulkDeleteOne.Id, "missing-customer"]);
Check(bulkDeleted == 2 && await lifecycleRepository.GetLeadAsync(bulkDeleteOne.Id) is null && await lifecycleRepository.GetLeadAsync(bulkDeleteTwo.Id) is null, "checkbox bulk deletion is transactional, distinct and ignores missing customers");

try { File.Delete(database); Directory.Delete(root, true); } catch { }
Console.WriteLine(failures.Count == 0 ? "\nAI Sales OS native core smoke tests passed." : $"\n{failures.Count} smoke test(s) failed.");
return failures.Count == 0 ? 0 : 1;

static string Envelope(string content) => System.Text.Json.JsonSerializer.Serialize(new { choices=new[] { new { message=new { content } } } });

static string V2AnalysisJson(string behaviorEvidence) => WAFlow.Core.Infrastructure.Json.Serialize(new
{
    contract_version=2,
    lead_score=88,
    base_profile_score=78,
    behavior_signal_score=10,
    grade="A",
    dimension_scores=new
    {
        paid_marketing_willingness=20, supply_stability=18, ecommerce_foundation=15,
        private_traffic=12, existing_sales=8, materials_readiness=5
    },
    dimension_evidence=new
    {
        paid_marketing_willingness=new { reason="有明确增长投入意向", evidence=new[] { "客户资料显示付费增长需求" } },
        supply_stability=new { reason="品类与采购方向清晰", evidence=new[] { "客户提供了持续采购背景" } },
        ecommerce_foundation=new { reason="已有成熟电商渠道", evidence=new[] { "客户资料包含 Amazon 渠道" } },
        private_traffic=new { reason="具备一定私域触达能力", evidence=new[] { "客户资料包含 WhatsApp 社群" } },
        existing_sales=new { reason="已有销售记录但规模需核实", evidence=new[] { "导入资料包含历史销售记录" } },
        materials_readiness=new { reason="已有部分营销素材", evidence=new[] { "客户资料提及产品图片" } }
    },
    behavior_signals=new[] { "提供明确采购数量" },
    behavior_signal_details=new[] { new { signal="提供明确采购数量", score=10, evidence=behaviorEvidence } },
    customer_profile="美国 Amazon 卖家，正在寻找稳定供应商。",
    customer_segment="高潜力电商买家",
    stage="negotiation",
    confidence=.91,
    purchase_probability=76,
    next_action="发送报价与历史客户案例。",
    risk_warning="价格敏感，报价需说明价值差异。"
});

sealed class FakeSecretStore(string value) : ISecretStore
{
    public void Save(string secret) { }
    public string? Read() => value;
}

sealed class QueueHandler(IEnumerable<string> responses) : HttpMessageHandler
{
    private readonly Queue<string> _responses = new(responses);
    public List<(string Method, string Uri, string Authorization)> Requests { get; } = [];
    public List<string> RequestBodies { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.Method.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString() ?? ""));
        if (request.Method == HttpMethod.Get)
            return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent("{\"data\":[{\"id\":\"deepseek-reasoner\"},{\"id\":\"deepseek-chat\"}]}", Encoding.UTF8, "application/json") };
        RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json") };
    }
}

sealed class IpMonitorHandler : HttpMessageHandler
{
    private int _ipCalls;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.ToString();
        string json;
        if (uri.Contains("api64.ipify.org", StringComparison.OrdinalIgnoreCase))
            json = System.Text.Json.JsonSerializer.Serialize(new { ip = ++_ipCalls == 1 ? "198.51.100.10" : "203.0.113.20" });
        else
            json = System.Text.Json.JsonSerializer.Serialize(new { success=true, country_code="US", country="United States", region="California", city="Los Angeles", connection=new { isp="Example ISP" } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

sealed class MutableIpMonitorHandler(string initialIp) : HttpMessageHandler
{
    public string CurrentIp { get; set; } = initialIp;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!.ToString();
        var json = uri.Contains("api64.ipify.org", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Json.JsonSerializer.Serialize(new { ip = CurrentIp })
            : System.Text.Json.JsonSerializer.Serialize(new { success=true, country_code="US", country="United States", region="Virginia", city="Ashburn", connection=new { isp="Example ISP" } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content=new StringContent(json, Encoding.UTF8, "application/json") });
    }
}

sealed class FakeStructuredReportProvider : IStructuredAiProvider
{
    public int FactExtractionCalls { get; private set; }
    public bool HasApiKey() => true;
    public Task<string> GetSelectedModelAsync(CancellationToken cancellationToken = default) => Task.FromResult("deepseek-report-test");

    public Task<T> CompleteStructuredAsync<T>(string instructions, object payload, Func<T, string?> validate, CancellationToken cancellationToken = default) where T : class
    {
        object result;
        if (typeof(T) == typeof(CustomerFactSet))
        {
            FactExtractionCalls++;
            result = new CustomerFactSet
            {
                Facts=[new ReportStatement { Nature="事实", Topic="采购需求", Statement="客户明确表达每月采购500件。", Evidence="I need 500 pcs monthly.", Source="WhatsApp report-84", Confidence=.98 }],
                Quotes=[new CustomerQuote { Original="I need 500 pcs monthly.", ChineseMeaning="客户明确表达每月采购500件需求。", AiAnalysis="这是明确的持续采购数量信号。", Timestamp=DateTimeOffset.Now }],
                InformationGaps=["尚未确认目标价格。"]
            };
        }
        else if (typeof(T) == typeof(CustomerBusinessAnalysisResult))
        {
            result = new CustomerBusinessAnalysisResult
            {
                ExecutiveSummary=new CustomerExecutiveSummary { OneLinePositioning="该客户是具有明确月度采购需求的美国 Amazon 卖家。", CustomerType="跨境电商卖家", BusinessStage="供应商评估与报价阶段", OverallValueJudgment="待最终综合", CurrentSalesRecommendation="待最终综合" },
                BasicProfile=new CustomerBasicProfile { CustomerType="跨境电商卖家", BusinessModels=["Amazon"], ProductDirection="家居用品", OperatingScale="已表达每月500件采购需求，其他规模待验证", DevelopmentStage="成熟采购需求验证阶段" },
                BusinessBackground=new CustomerBusinessBackground { CurrentBusinessModel="通过 Amazon 销售家居用品并按月补充供应链。", CoreAdvantages=["采购数量明确", "已有线上销售渠道"], CurrentLimitations=["目标价格尚未确认"], GrowthOpportunities=["建立稳定月度供货合作"] },
                PainAnalysis=new CustomerPainAnalysis { SurfacePains=["需要稳定供应商"], DeepBusinessProblems=["持续补货能力与供应链确定性仍需验证"] },
                PurchaseMotivation=new CustomerPurchaseMotivation { InterestReasons=["需要满足月度采购计划"], TriggerEvents=["主动提出500件月度需求"], DecisionFactors=["价格", "交期", "供货稳定性"] },
                WhatsAppAnalysis=new CustomerWhatsAppAnalysis { EngagementLevel="积极，已提供明确采购数量", FocusTopics=["月度采购数量"], PurchaseSignals=["每月500件"], Concerns=["价格与交期尚未确认"] },
                OpportunityJudgment=new CustomerOpportunityJudgment { Grade="A", AiScore=86, DealProbability=72, PositiveFactors=["明确采购数量", "已有 Amazon 渠道"], NegativeFactors=["价格敏感度待确认"] },
                ProductFit=new CustomerProductFit { HighMatchPoints=["家居用品方向一致"], LowMatchPoints=["尚无卖方具体产品参数"], QuestionsToValidate=["目标 SKU 与规格是什么"] },
                RiskAnalysis=new CustomerRiskAnalysis { DealRisks=["价格与交期未确认"], AdoptionRisks=["需求规格可能变化"], ChurnRisks=["供应响应不及时可能转向其他供应商"] }
            };
        }
        else if (typeof(T) == typeof(CustomerSalesStrategy))
        {
            result = new CustomerSalesStrategy
            {
                Actions=
                [
                    new CustomerSalesAction { Timeframe="24小时", Action="确认SKU、规格、目标价格和交期。", Rationale="客户已给出数量但缺少成交条件。", SuccessCriterion="获得完整询价参数。" },
                    new CustomerSalesAction { Timeframe="7天", Action="发送匹配报价与供货案例。", Rationale="用可核验信息降低供应链顾虑。", SuccessCriterion="客户确认报价评估或样品计划。" },
                    new CustomerSalesAction { Timeframe="30天", Action="推动首单或月度供货计划。", Rationale="把月度需求转为可执行合作节奏。", SuccessCriterion="形成首单或明确采购时间表。" }
                ],
                RecommendedTalkTrack="感谢您确认每月500件的需求。为了给出准确方案，请确认目标SKU、规格、目标价格与期望交期。",
                PendingQuestions=["目标SKU与规格是什么", "可接受价格区间是多少", "首次交付时间是什么"]
            };
        }
        else if (typeof(T) == typeof(CustomerReportSynthesisResult))
        {
            var sentence = "事实方面，客户资料显示其位于美国并经营 Amazon 渠道，WhatsApp 原话明确提出每月采购500件，系统也记录了既往自动化触达和商机分析。AI判断方面，该客户具备持续采购潜力，当前最关键的不确定因素是目标SKU、规格、价格区间、交付周期和最终决策流程，这些信息尚未被证据确认，不能视为既定事实。销售建议方面，应在24小时内完成询价参数确认，在7天内提供与需求匹配的报价及供货案例，并在30天内推动首单或月度采购计划。沟通中应围绕供货稳定性、价格构成和交付能力建立信任，同时避免在库存、折扣或交期未经核实前作出承诺。管理层可将其列为优先跟进客户，但仍需由销售人员复核所有AI判断并持续记录客户反馈。";
            result = new CustomerReportSynthesisResult { ManagementSummary=sentence, OverallValueJudgment="高潜力月度采购客户，具备明确数量信号但成交条件仍需确认。", CurrentSalesRecommendation="优先补齐询价参数并发送匹配报价与供货案例。", DealProbability=72 };
        }
        else throw new InvalidOperationException($"Unsupported report stage type: {typeof(T).Name}");
        var typed = (T)result;
        var error = validate(typed);
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
        return Task.FromResult(typed);
    }
}

sealed class AlwaysInvalidStructuredReportProvider : IStructuredAiProvider
{
    public bool HasApiKey() => true;
    public Task<string> GetSelectedModelAsync(CancellationToken cancellationToken = default) => Task.FromResult("invalid-structured-test");
    public Task<T> CompleteStructuredAsync<T>(string instructions, object payload, Func<T, string?> validate, CancellationToken cancellationToken = default) where T : class =>
        throw new DeepSeekException("invalid_structured_output", "测试模型返回的结构化 JSON 无法解析。", true);
}

sealed class FakeCustomerBrainProvider : IStructuredAiProvider
{
    public bool HasApiKey() => true;
    public Task<string> GetSelectedModelAsync(CancellationToken cancellationToken = default) => Task.FromResult("customer-brain-test");

    public Task<T> CompleteStructuredAsync<T>(
        string instructions,
        object payload,
        Func<T, string?> validate,
        CancellationToken cancellationToken = default) where T : class
    {
        object result;
        if (typeof(T) == typeof(CustomerUnderstandingResult))
        {
            result = new CustomerUnderstandingResult
            {
                CustomerDna = "美国 Amazon 家居用品买家，已明确表达持续月度采购需求。",
                ProfileSummary = "客户经营 Amazon 家居用品业务，并通过 WhatsApp 明确提出每月采购500件，目标价格和交期仍待确认。",
                CustomerType = "跨境电商卖家",
                BusinessModels = ["Amazon"],
                PainPoints = ["需要稳定的月度供货能力"],
                PurchaseMotivations = ["补充每月500件的持续采购需求"],
                InformationGaps = ["目标SKU、价格区间和交期尚未确认"],
                Statements =
                [
                    new CustomerIntelligenceStatement
                    {
                        Nature = IntelligenceStatementNature.Inference,
                        Topic = "需求成熟度",
                        Text = "客户具备较明确的持续采购意向。",
                        Evidence = "I need 500 pcs monthly.",
                        Source = "WhatsApp report-84",
                        Confidence = .88
                    }
                ]
            };
        }
        else if (typeof(T) == typeof(CustomerOpportunityEvaluation))
        {
            result = new CustomerOpportunityEvaluation
            {
                PurchaseProbability = 74,
                Confidence = .82,
                SuggestedStage = LeadStage.RequirementConfirmed,
                PositiveSignals = ["客户明确提出每月500件采购数量", "已有 Amazon 销售渠道"],
                RiskSignals = ["目标价格、SKU和交期尚未确认"],
                Evidence = ["I need 500 pcs monthly.", "CRM 销售渠道：Amazon"],
                Rationale = "明确数量和已有销售渠道构成正向信号，但成交条件仍需销售人员核实。"
            };
        }
        else if (typeof(T) == typeof(CustomerSalesRecommendation))
        {
            result = new CustomerSalesRecommendation
            {
                NextBestAction = "24小时内确认目标SKU、价格区间和期望交期。",
                Rationale = "客户已经给出持续采购数量，当前最影响报价和成交的是关键询价参数缺失。",
                SuggestedTalkTrack = "感谢您确认每月500件需求。为了给出准确报价，请确认目标SKU、价格区间和期望交期。",
                QuestionsToVerify = ["目标SKU是什么", "可接受价格区间是多少", "期望交期是什么"],
                Evidence = ["I need 500 pcs monthly.", "目标价格状态待确认"],
                DueInHours = 24,
                Priority = FollowUpPriority.High
            };
        }
        else
        {
            throw new InvalidOperationException($"Unsupported Customer Brain stage type: {typeof(T).Name}");
        }

        var typed = (T)result;
        var error = validate(typed);
        if (!string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error);
        return Task.FromResult(typed);
    }
}
