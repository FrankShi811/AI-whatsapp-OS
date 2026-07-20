using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ClosedXML.Excel;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Core.Imports;

public sealed class ImportService
{
    public const long MaxBytes = 200L * 1024 * 1024;
    public const long MaxCells = 5_000_000;
    public const int WriteBatchSize = 500;
    private readonly LocalRepository _repository;

    public ImportService(LocalRepository repository)
    {
        _repository = repository;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ParsedImport Parse(string filePath)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("导入文件不存在。", filePath);
        if (file.Length == 0) throw new InvalidDataException("导入文件为空。");
        if (file.Length > MaxBytes) throw new InvalidDataException("文件超过 200MB 资源保护上限。");
        var extension = file.Extension.ToLowerInvariant();
        if (extension is not (".xlsx" or ".csv")) throw new InvalidDataException("仅支持 .xlsx 或 .csv 文件。");
        using var snapshot = OpenSharedReadSnapshot(filePath, file.Length);
        var result = extension == ".xlsx" ? ParseXlsx(snapshot) : ParseCsv(snapshot);
        result.FilePath = filePath;
        if (result.Sheets.Count == 0) throw new InvalidDataException("文件中没有非空工作表或数据行。");
        return result;
    }

    public List<MappingRow> SuggestMapping(ImportSheet sheet)
    {
        var seen = new HashSet<ImportField>();
        return sheet.Headers.Select(header =>
        {
            var target = FieldAliases.Suggest(header);
            if (target is not (ImportField.Ignore or ImportField.Custom) && !seen.Add(target)) target = ImportField.Custom;
            return new MappingRow { Header = header, Sample = sheet.Rows.FirstOrDefault()?.GetValueOrDefault(header) ?? "", Target = target };
        }).ToList();
    }

    public async Task<List<ImportPreviewRow>> BuildPreviewAsync(ImportSheet sheet, IEnumerable<MappingRow> mapping, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var selected = mapping.Where(m => m.Target != ImportField.Ignore).ToList();
        var coreMap = selected.Where(m => m.Target != ImportField.Custom).ToDictionary(m => m.Header, m => m.Target);
        // Every source column is retained under its original header. Recognized columns are
        // additionally projected into CRM core fields so the rest of the product can use them.
        var customHeaders = sheet.Headers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        progress?.Report(new("正在读取已有客户", 0, sheet.Rows.Count));
        var existing = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var byPhone = existing.Where(l => l.PhoneValid && !string.IsNullOrWhiteSpace(l.PhoneE164)).ToDictionary(l => l.PhoneE164, StringComparer.OrdinalIgnoreCase);
        var byIdentity = existing
            .Select(lead => (Key: BuildImportIdentity(lead.CustomFields, lead.Name), Lead: lead))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Lead, StringComparer.OrdinalIgnoreCase);
        var incomingByPhone = new Dictionary<string, ImportPreviewRow>(StringComparer.OrdinalIgnoreCase);
        var incomingByIdentity = new Dictionary<string, ImportPreviewRow>(StringComparer.OrdinalIgnoreCase);
        var output = new List<ImportPreviewRow>(sheet.Rows.Count);
        for (var i = 0; i < sheet.Rows.Count; i++)
        {
            var values = new Dictionary<ImportField, string>();
            foreach (var pair in coreMap)
            {
                var value = sheet.Rows[i].GetValueOrDefault(pair.Key, "").Trim();
                values[pair.Value] = value;
            }
            var customValues = customHeaders.ToDictionary(header => header, header => sheet.Rows[i].GetValueOrDefault(header, "").Trim(), StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(values.GetValueOrDefault(ImportField.Name)) && string.IsNullOrWhiteSpace(values.GetValueOrDefault(ImportField.Company)))
            {
                var fallback = customHeaders
                    .Select(header => customValues.GetValueOrDefault(header, ""))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 160)
                    ?? $"导入行 {i + 2}";
                values[ImportField.Name] = fallback;
            }
            var country = values.GetValueOrDefault(ImportField.Country, "");
            var rawPhone = values.GetValueOrDefault(ImportField.WhatsApp, "");
            var normalized = PhoneNormalizer.Normalize(rawPhone, country);
            var item = new ImportPreviewRow
            {
                RowNumber = i + 2, Values = values, CustomValues = customValues, Name = values.GetValueOrDefault(ImportField.Name, ""),
                Company = values.GetValueOrDefault(ImportField.Company, ""), Country = country,
                PhoneE164 = normalized.Valid ? normalized.E164 : rawPhone, PhoneValid = normalized.Valid
            };
            var importIdentity = BuildImportIdentity(customValues);
            if (!normalized.Valid) item.Warnings.Add(string.IsNullOrWhiteSpace(values.GetValueOrDefault(ImportField.WhatsApp))
                ? "未提供 WhatsApp 号码"
                : normalized.Reason == "country_code_required" ? "号码缺少国家区号，且国家字段无法推断" : "WhatsApp 号码格式无效");
            if (item.PhoneValid && byPhone.TryGetValue(item.PhoneE164, out var duplicate))
            {
                item.IsDuplicate = true; item.DuplicateLeadId = duplicate.Id; item.Changes = BuildChanges(duplicate, values, customValues, normalized);
            }
            else if (importIdentity is not null && byIdentity.TryGetValue(importIdentity, out duplicate))
            {
                item.IsDuplicate = true; item.DuplicateLeadId = duplicate.Id; item.Changes = BuildChanges(duplicate, values, customValues, normalized);
            }
            else if (item.PhoneValid && incomingByPhone.TryGetValue(item.PhoneE164, out var importDuplicate))
            {
                item.IsDuplicate = true; item.DuplicateRowNumber = importDuplicate.RowNumber;
                item.Changes = $"与导入表第 {importDuplicate.RowNumber} 行号码重复；将合并到同一客户";
            }
            else if (importIdentity is not null && incomingByIdentity.TryGetValue(importIdentity, out importDuplicate))
            {
                item.IsDuplicate = true; item.DuplicateRowNumber = importDuplicate.RowNumber;
                item.Changes = $"与导入表第 {importDuplicate.RowNumber} 行客户标识重复；将合并到同一客户";
            }
            else
            {
                if (item.PhoneValid) incomingByPhone[item.PhoneE164] = item;
                if (importIdentity is not null) incomingByIdentity[importIdentity] = item;
            }
            output.Add(item);
            if ((i + 1) % 250 == 0 || i + 1 == sheet.Rows.Count) progress?.Report(new("正在生成重复与风险预览", i + 1, sheet.Rows.Count));
        }
        return output;
    }

    private static string? BuildImportIdentity(IReadOnlyDictionary<string, string> fields, string? fallbackName = null)
    {
        foreach (var pair in fields)
        {
            if (FieldAliases.Suggest(pair.Key) != ImportField.Name || string.IsNullOrWhiteSpace(pair.Value)) continue;
            var normalized = new string(pair.Value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (normalized.Length >= 2) return "name:" + normalized;
        }
        if (string.IsNullOrWhiteSpace(fallbackName)) return null;
        var fallback = new string(fallbackName.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return fallback.Length >= 2 ? "name:" + fallback : null;
    }

    public async Task<ImportCommitResult> CommitAsync(string fileName, IReadOnlyList<ImportPreviewRow> preview, bool allowStageChange, bool allowOwnerChange, IProgress<ImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var created = 0; var updated = 0; var failed = 0;
        progress?.Report(new("正在准备批量写入", 0, preview.Count));
        var existing = (await _repository.GetLeadsAsync(cancellationToken: cancellationToken)).ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var importedRows = new Dictionary<int, Lead>();
        var pending = new Dictionary<string, Lead>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < preview.Count; index++)
        {
            var row = preview[index];
            if (row.Errors.Count > 0) { failed++; continue; }
            try
            {
                Lead lead;
                if (row.DuplicateRowNumber is int duplicateRow && importedRows.TryGetValue(duplicateRow, out var importedLead))
                {
                    lead = importedLead;
                    ApplyImportedValues(lead, row.Values, row.CustomValues, row.PhoneE164, row.PhoneValid, allowStageChange, allowOwnerChange, isNew:false);
                    updated++;
                }
                else if (row.IsDuplicate)
                {
                    if (!existing.TryGetValue(row.DuplicateLeadId, out lead!)) throw new InvalidOperationException("重复客户已不存在。");
                    ApplyImportedValues(lead, row.Values, row.CustomValues, row.PhoneE164, row.PhoneValid, allowStageChange, allowOwnerChange, isNew:false);
                    updated++;
                }
                else
                {
                    lead = new Lead();
                    ApplyImportedValues(lead, row.Values, row.CustomValues, row.PhoneE164, row.PhoneValid, true, true, isNew:true);
                    created++;
                }
                if (!row.IsDuplicate && row.DuplicateRowNumber is null)
                {
                    lead.Score = 0;
                    lead.Grade = "D";
                    lead.ScoreBreakdown = [];
                    lead.ScoreReasons = [];
                    lead.AiScoreApplied = false;
                    lead.AnalysisStatus = AnalysisStatus.NotRun;
                    lead.ProfileSummary = "等待 AI 分析";
                    lead.CustomerSegment = "未分析";
                    lead.NextAction = row.PhoneValid ? "等待客户回复或手动运行 AI 分析。" : "核对 WhatsApp 号码后再触达。";
                }
                importedRows[row.RowNumber] = lead;
                pending[lead.Id] = lead;
            }
            catch { failed++; }
            if ((index + 1) % 250 == 0 || index + 1 == preview.Count) progress?.Report(new("正在准备批量写入", index + 1, preview.Count));
        }
        var writeProgress = new Progress<int>(completed => progress?.Report(new("正在分批写入本地数据库", completed, pending.Count)));
        await _repository.UpsertLeadsAsync(pending.Values.ToList(), WriteBatchSize, writeProgress, cancellationToken);
        progress?.Report(new("\u6b63\u5728\u540c\u6b65 WhatsApp Inbox \u5efa\u8054\u60c5\u51b5", 0, pending.Count));
        await _repository.SynchronizeLeadConnectionsFromInboxAsync(pending.Values.ToList(), cancellationToken);
        var invalid = preview.Count(x => !x.PhoneValid);
        await _repository.SaveImportSummaryAsync(fileName, preview.Count, created, updated, invalid, cancellationToken);
        await _repository.LogEventAsync("import_committed", null, null, $"{fileName}; total={preview.Count}; created={created}; updated={updated}; invalid={invalid}", cancellationToken);
        return new(preview.Count, created, updated, invalid, failed);
    }

    private static string BuildChanges(Lead lead, IReadOnlyDictionary<ImportField, string> values, IReadOnlyDictionary<string, string> customValues, NormalizedPhone normalized)
    {
        var changes = new List<string>();
        Add(ImportField.Name, "姓名", lead.Name); Add(ImportField.Company, "公司", lead.Company); Add(ImportField.Country, "国家", lead.Country);
        Add(ImportField.Email, "邮箱", lead.Email); Add(ImportField.ProductInterest, "意向产品", lead.ProductInterest);
        if (normalized.E164.Length > 0 && normalized.E164 != lead.PhoneE164) changes.Add("号码");
        var customChanges = customValues.Count(pair => !string.IsNullOrWhiteSpace(pair.Value) && (!lead.CustomFields.TryGetValue(pair.Key, out var current) || current != pair.Value));
        if (customChanges > 0) changes.Add($"自定义维度 {customChanges} 项");
        return changes.Count == 0 ? "无字段变化" : string.Join("、", changes);
        void Add(ImportField field, string label, string current) { if (values.TryGetValue(field, out var value) && value.Length > 0 && value != current) changes.Add(label); }
    }

    private static void ApplyImportedValues(Lead lead, IReadOnlyDictionary<ImportField, string> values, IReadOnlyDictionary<string, string> customValues, string phone, bool phoneValid, bool allowStageChange, bool allowOwnerChange, bool isNew)
    {
        if (isNew) SetExact(ImportField.Name, x => lead.Name = x);
        SetExact(ImportField.Company, x => lead.Company = x); SetExact(ImportField.Country, x => lead.Country = x);
        SetExact(ImportField.Email, x => lead.Email = x); SetExact(ImportField.ProductInterest, x => lead.ProductInterest = x); SetExact(ImportField.Source, x => lead.Source = x);
        if (values.ContainsKey(ImportField.WhatsApp)) { lead.PhoneE164 = phone; lead.PhoneValid = phoneValid; }
        if (values.TryGetValue(ImportField.EstimatedOrderValue, out var amount))
            lead.EstimatedOrderValue = decimal.TryParse(amount.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedAmount) ? Math.Max(0, parsedAmount) : 0;
        if (values.TryGetValue(ImportField.CompanyScale, out var scale)) lead.CompanyScale = LeadScoringService.ParseSignal(scale);
        if (values.TryGetValue(ImportField.PurchasePower, out var power)) lead.PurchasePower = LeadScoringService.ParseSignal(power);
        if (values.TryGetValue(ImportField.ExplicitDemand, out var explicitDemand)) lead.ExplicitDemand = ParseBool(explicitDemand);
        if (values.TryGetValue(ImportField.Tags, out var tags)) lead.Tags = tags.Split([',','，',';','；','|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
        if (isNew && allowStageChange && values.TryGetValue(ImportField.Stage, out var stage)) lead.Stage = StageParser.Parse(stage);
        if (allowOwnerChange) SetExact(ImportField.Owner, x => lead.Owner = x);
        ReplaceCustomDimensions(lead, customValues, isNew);
        lead.RegisteredOrConsulted = lead.ExplicitDemand || !string.IsNullOrWhiteSpace(lead.ProductInterest);
        if (isNew) SetExact(ImportField.Notes, x => lead.LatestMessage = x);
        return;
        void SetExact(ImportField field, Action<string> apply) { if (values.TryGetValue(field, out var value)) apply(value.Trim()); }
    }

    private static void ReplaceCustomDimensions(Lead lead, IReadOnlyDictionary<string, string> incoming, bool isNew)
    {
        if (isNew)
        {
            lead.CustomFields = incoming.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return;
        }

        var protectedValues = lead.CustomFields
            .Where(pair => IsProtectedDimension(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        lead.CustomFields.Clear();
        foreach (var pair in incoming)
        {
            var retained = protectedValues.FirstOrDefault(item => item.Key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase));
            lead.CustomFields[pair.Key] = retained.Key is not null && !string.IsNullOrWhiteSpace(retained.Value) ? retained.Value : pair.Value;
        }
        foreach (var pair in protectedValues)
            if (!lead.CustomFields.Keys.Any(key => key.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)))
                lead.CustomFields[pair.Key] = pair.Value;
    }

    internal static bool IsProtectedDimension(string header)
    {
        if (FieldAliases.Suggest(header) is ImportField.Name or ImportField.Stage) return true;
        var normalized = new string(header.Where(char.IsLetterOrDigit).ToArray());
        return normalized.Contains("\u8be6\u60c5\u8bb0\u5f55", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\u8be6\u7ec6\u8bb0\u5f55", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\u5ba2\u6237\u751f\u610f\u6a21\u5f0f", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\u751f\u610f\u6a21\u5f0f", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("\u5546\u4e1a\u6a21\u5f0f", StringComparison.OrdinalIgnoreCase)
            || LeadConnectionStatus.IsDimension(header);
    }

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "是" or "有" or "明确";

    private static ParsedImport ParseCsv(MemoryStream snapshot)
    {
        var bytes = snapshot.ToArray();
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.GetEncoding("GB18030").GetString(bytes); }
        text = text.TrimStart('\uFEFF');
        var matrix = Csv.Parse(text);
        var sanitized = 0;
        for (var row = 0; row < matrix.Count; row++)
            for (var column = 0; column < matrix[row].Count; column++)
                matrix[row][column] = Sanitize(matrix[row][column], ref sanitized);
        var sheet = BuildSheet("CSV", matrix);
        if (sheet is not null) sheet.SanitizedFormulaCount = sanitized;
        return new ParsedImport { PreferredSheetName = sheet?.Name ?? "", Sheets = sheet is null ? [] : [sheet] };
    }

    private static ParsedImport ParseXlsx(MemoryStream snapshot)
    {
        snapshot.Position = 0;
        if (snapshot.ReadByte() != 0x50 || snapshot.ReadByte() != 0x4B) throw new InvalidDataException("\u6269\u5c55\u540d\u4e3a .xlsx\uff0c\u4f46\u6587\u4ef6\u5185\u5bb9\u4e0d\u662f\u6709\u6548\u7684 XLSX\u3002");
        AssertSafeXlsx(snapshot);
        var preferredSheetName = ReadActiveSheetName(snapshot) ?? "";
        snapshot.Position = 0;
        using var workbook = new XLWorkbook(snapshot);
        var sheets = new List<ImportSheet>();
        foreach (var worksheet in workbook.Worksheets)
        {
            var range = worksheet.RangeUsed();
            if (range is null) continue;
            var matrix = new List<List<string>>();
            var formulaCount = 0;
            foreach (var row in range.Rows())
            {
                var values = new List<string>();
                foreach (var cell in row.Cells())
                {
                    if (cell.HasFormula) { values.Add("'=" + cell.FormulaA1); formulaCount++; }
                    else values.Add(Sanitize(cell.GetFormattedString(), ref formulaCount));
                }
                matrix.Add(values);
            }
            if (BuildSheet(worksheet.Name, matrix) is { } parsed) { parsed.SanitizedFormulaCount += formulaCount; sheets.Add(parsed); }
        }
        return new ParsedImport { PreferredSheetName = preferredSheetName, Sheets = sheets };
    }

    private static string? ReadActiveSheetName(Stream snapshot)
    {
        snapshot.Position = 0;
        using var archive = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen:true);
        var entry = archive.GetEntry("xl/workbook.xml");
        if (entry is null) return null;
        using var entryStream = entry.Open();
        var document = XDocument.Load(entryStream);
        XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var activeTab = (int?)document.Descendants(ns + "workbookView").FirstOrDefault()?.Attribute("activeTab") ?? 0;
        var sheets = document.Descendants(ns + "sheet").ToList();
        return activeTab >= 0 && activeTab < sheets.Count ? (string?)sheets[activeTab].Attribute("name") : null;
    }

    private static void AssertSafeXlsx(Stream snapshot)
    {
        snapshot.Position = 0;
        using var archive = new ZipArchive(snapshot, ZipArchiveMode.Read, leaveOpen:true);
        if (archive.Entries.Count is 0 or > 2000) throw new InvalidDataException("XLSX 压缩包条目数量异常。");
        long compressed = 0; long uncompressed = 0;
        foreach (var entry in archive.Entries)
        {
            compressed += Math.Max(0, entry.CompressedLength); uncompressed += Math.Max(0, entry.Length);
            if (uncompressed > 512L * 1024 * 1024 || uncompressed / (double)Math.Max(1, compressed) > 200d) throw new InvalidDataException("XLSX 解压体积或压缩比超过资源保护上限。");
        }
    }

    private static MemoryStream OpenSharedReadSnapshot(string filePath, long expectedLength)
    {
        IOException? lastError = null;
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var source = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 128 * 1024,
                    FileOptions.SequentialScan);
                var snapshot = new MemoryStream((int)Math.Min(Math.Max(expectedLength, 0), int.MaxValue));
                source.CopyTo(snapshot);
                snapshot.Position = 0;
                return snapshot;
            }
            catch (IOException error)
            {
                lastError = error;
                if (attempt == 4) break;
                Thread.Sleep(150 * attempt);
            }
        }
        throw new IOException("\u65e0\u6cd5\u8bfb\u53d6\u8868\u683c\u3002\u8bf7\u7b49\u5f85 WPS/Excel \u4fdd\u5b58\u5b8c\u6210\u540e\u91cd\u8bd5\uff1b\u8868\u683c\u4fdd\u6301\u6253\u5f00\u4e0d\u5f71\u54cd\u5bfc\u5165\u3002", lastError);
    }

    private static ImportSheet? BuildSheet(string name, IReadOnlyList<List<string>> matrix)
    {
        var nonEmpty = matrix.Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (nonEmpty.Count == 0) return null;
        var headers = UniqueHeaders(nonEmpty[0]);
        var rows = nonEmpty.Skip(1).Select(row => headers.Select((h, i) => new { h, v = i < row.Count ? row[i].Trim() : "" }).ToDictionary(x => x.h, x => x.v)).ToList();
        if ((long)Math.Max(1, rows.Count) * Math.Max(1, headers.Count) > MaxCells) throw new InvalidDataException($"工作表 {name} 超过 {MaxCells:N0} 个单元格资源保护上限；请拆分为多个文件导入。");
        return new ImportSheet { Name = name, Headers = headers, Rows = rows };
    }

    private static List<string> UniqueHeaders(IReadOnlyList<string> input)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return input.Select((value, index) =>
        {
            var baseName = string.IsNullOrWhiteSpace(value) ? $"Column {index + 1}" : value.Trim();
            counts[baseName] = counts.GetValueOrDefault(baseName) + 1;
            return counts[baseName] == 1 ? baseName : $"{baseName} ({counts[baseName]})";
        }).ToList();
    }

    private static string Sanitize(string value, ref int count)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0 && "=+@-".Contains(trimmed[0])) { count++; return "'" + trimmed; }
        return trimmed;
    }
}

internal static class FieldAliases
{
    private static readonly Dictionary<ImportField, string[]> Aliases = new()
    {
        [ImportField.Name]=["name","fullname","contactname","buyername","buyernickname","姓名","联系人","客户姓名","买家姓名","买家昵称"],
        [ImportField.Company]=["company","companyname","business","organization","公司","公司名称","企业名称"],
        [ImportField.Country]=["country","market","region","国家","国家地区","市场","地区"],
        [ImportField.WhatsApp]=["whatsapp","whatsappnumber","whatsapp号码","phone","mobile","tel","电话","电话号码","手机号","手机","联系电话","号码"],
        [ImportField.Email]=["email","emailaddress","mail","邮箱","电子邮箱"],
        [ImportField.ProductInterest]=["productinterest","interestedproduct","product","sku","产品兴趣","意向产品","产品","询盘产品"],
        [ImportField.EstimatedOrderValue]=["estimatedordervalue","estimatedvalue","ordervalue","dealvalue","budget","采购金额","订单金额","预计订单额","预计金额","预算"],
        [ImportField.CompanyScale]=["companyscale","employees","companysize","公司规模","员工人数","企业规模"],
        [ImportField.PurchasePower]=["purchasepower","buyingpower","采购能力","购买力"],
        [ImportField.ExplicitDemand]=["explicitdemand","demand","requirement","明确需求","需求","采购需求"],
        [ImportField.Source]=["source","leadsource","channel","来源","线索来源","渠道"],
        [ImportField.Owner]=["owner","现owner","currentowner","assignee","salesowner","负责人","销售负责人","跟进人"],
        [ImportField.Stage]=["stage","leadstage","status","阶段","商机阶段","跟进阶段","状态"],
        [ImportField.Tags]=["tags","tag","labels","标签","客户标签"],
        [ImportField.Notes]=["notes","note","remark","comments","备注","说明"]
    };
    private static readonly Dictionary<string, ImportField> Lookup = Aliases.SelectMany(p => p.Value.Select(v => (key: Normalize(v), p.Key))).ToDictionary(x => x.key, x => x.Key);
    public static ImportField Suggest(string header)
    {
        var normalized = Normalize(header);
        if (Lookup.TryGetValue(normalized, out var field)) return field;
        foreach (var segment in header.Split(['/','|','｜',':','：'])) if (Lookup.TryGetValue(Normalize(segment), out field)) return field;
        var prefix = Lookup
            .Where(pair => pair.Key.Length >= 3 && normalized.StartsWith(pair.Key, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(pair => pair.Key.Length)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(prefix.Key)) return prefix.Value;
        return ImportField.Custom;
    }
    private static string Normalize(string value) => new(value.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && !"_-./\\()（）[]【】".Contains(c)).ToArray());
}

internal static class Csv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (cell.Length > 1024 * 1024) throw new InvalidDataException("CSV 单个字段超过 1MB 安全限制。");
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); if (row.Any(x => x.Length > 0)) rows.Add(row); row = [];
            }
            else cell.Append(c);
        }
        row.Add(cell.ToString()); if (row.Any(x => x.Length > 0)) rows.Add(row);
        if (quoted) throw new InvalidDataException("CSV 引号未闭合。");
        return rows;
    }
}
