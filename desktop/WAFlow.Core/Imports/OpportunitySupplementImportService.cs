using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClosedXML.Excel;
using WAFlow.Core.Domain;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Core.Imports;

public sealed class OpportunitySupplementImportService
{
    public const long MaxBytes = 200L * 1024 * 1024;
    private readonly LocalRepository _repository;

    private static readonly IReadOnlyDictionary<string, OpportunityEventKind> SupportedSheets =
        new Dictionary<string, OpportunityEventKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["1、支付失败"] = OpportunityEventKind.PaymentFailed,
            ["2、下单未付款"] = OpportunityEventKind.AwaitingPayment,
            ["3、纠纷订单"] = OpportunityEventKind.Dispute,
            ["5、支付成功"] = OpportunityEventKind.PaymentSucceeded
        };

    public OpportunitySupplementImportService(LocalRepository repository)
    {
        _repository = repository;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public async Task<OpportunityImportPreview> BuildPreviewAsync(
        string filePath,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(filePath);
        if (!file.Exists) throw new FileNotFoundException("商机补充数据文件不存在。", filePath);
        if (!file.Extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("商机补充数据导入仅支持 .xlsx 文件。");
        if (file.Length == 0 || file.Length > MaxBytes)
            throw new InvalidDataException("文件为空或超过 200MB 资源保护上限。");

        progress?.Report("正在建立客户 Buyer ID 白名单…");
        var leads = await _repository.GetLeadsAsync(cancellationToken: cancellationToken);
        var leadGroups = leads
            .Select(lead => (Buyer: BuyerIdentity.Normalize(lead.BuyerId), Lead: lead))
            .Where(item => item.Buyer.Length > 0)
            .GroupBy(item => item.Buyer, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Lead).ToList(), StringComparer.OrdinalIgnoreCase);
        var conflicts = leadGroups.Where(item => item.Value.Count != 1).Select(item => item.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var whitelist = leadGroups.Where(item => item.Value.Count == 1)
            .ToDictionary(item => item.Key, item => item.Value[0], StringComparer.OrdinalIgnoreCase);

        var fileHash = await ComputeFileHashAsync(file.FullName, cancellationToken);
        var preview = new OpportunityImportPreview
        {
            SourceFilePath = file.FullName,
            SourceFileName = file.Name,
            SourceFileHash = fileHash,
            SourceModifiedAt = file.LastWriteTimeUtc,
            IsPreviouslyImportedFile = await _repository.HasOpportunityImportFileAsync(fileHash, cancellationToken)
        };
        if (preview.IsPreviouslyImportedFile)
        {
            progress?.Report("该文件已成功导入，重复上传不会写入数据或消耗 Token。");
            return preview;
        }

        progress?.Report("正在解析四类交易明细…");
        var parsedEvents = ParseWorkbook(file.FullName, whitelist, conflicts, preview, cancellationToken);
        var existingKeys = await _repository.GetOpportunityEventKeysAsync(cancellationToken);
        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in parsedEvents)
        {
            if (!seenInFile.Add(item.EventKey) || existingKeys.Contains(item.EventKey))
            {
                preview.DuplicateEvents++;
                continue;
            }
            preview.NewEvents.Add(item);
        }

        preview.MatchedEvents = parsedEvents.Count;
        preview.MatchedCustomers = parsedEvents
            .Select(item => item.LeadId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        preview.ConflictBuyerIds = preview.ConflictBuyerIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        preview.BuyerIdConflicts = preview.ConflictBuyerIds.Count;
        preview.UnmatchedBuyerIds = preview.UnmatchedBuyerIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(500)
            .ToList();
        if (preview.NewEvents.Count == 0)
        {
            preview.UnchangedCustomers = preview.MatchedCustomers;
            progress?.Report("预览完成：没有新增交易事件，不会写入数据或消耗 Token。");
            return preview;
        }

        var affectedLeadIds = preview.NewEvents
            .Select(item => item.LeadId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        progress?.Report("正在本地汇总交易价值、购买意图、品类与风险…");
        var existingEvents = await _repository.GetOpportunityEventsAsync(affectedLeadIds, cancellationToken);
        var existingSnapshots = (await _repository.GetOpportunitySnapshotsAsync(cancellationToken))
            .ToDictionary(item => item.LeadId, StringComparer.OrdinalIgnoreCase);
        var allEvents = existingEvents.Concat(preview.NewEvents)
            .GroupBy(item => item.EventKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        foreach (var leadGroup in allEvents.GroupBy(item => item.LeadId, StringComparer.OrdinalIgnoreCase))
        {
            var snapshot = BuildSnapshot(leadGroup.ToList());
            if (!existingSnapshots.TryGetValue(snapshot.LeadId, out var existing)
                || !existing.DataFingerprint.Equals(snapshot.DataFingerprint, StringComparison.Ordinal))
                preview.ChangedSnapshots.Add(snapshot);
            else
                preview.UnchangedLeadIds.Add(snapshot.LeadId);
        }
        preview.ChangedCustomers = preview.ChangedSnapshots.Count;
        preview.UnchangedCustomers = preview.UnchangedLeadIds.Count;
        preview.ReanalysisCount = preview.ChangedCustomers;
        progress?.Report("预览完成，尚未写入任何客户或交易数据。");
        return preview;
    }

    public Task<OpportunityImportResult> CommitAsync(
        OpportunityImportPreview preview,
        CancellationToken cancellationToken = default) =>
        _repository.CommitOpportunityImportAsync(preview, cancellationToken);

    private static List<OpportunityTransactionEvent> ParseWorkbook(
        string filePath,
        IReadOnlyDictionary<string, Lead> whitelist,
        ISet<string> conflicts,
        OpportunityImportPreview preview,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(filePath);
        var missing = SupportedSheets.Keys.Where(name => !workbook.TryGetWorksheet(name, out _)).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"文件缺少必要工作表：{string.Join("、", missing)}。");

        var result = new List<OpportunityTransactionEvent>();
        foreach (var sheetContract in SupportedSheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sheet = workbook.Worksheet(sheetContract.Key);
            var headerRow = sheet.FirstRowUsed() ?? throw new InvalidDataException($"工作表“{sheet.Name}”没有表头。");
            var headers = headerRow.CellsUsed().ToDictionary(
                cell => NormalizeHeader(cell.GetString()),
                cell => cell.Address.ColumnNumber,
                StringComparer.OrdinalIgnoreCase);
            ValidateHeaders(sheetContract.Value, sheet.Name, headers);
            var lastRow = sheet.LastRowUsed()?.RowNumber() ?? headerRow.RowNumber();
            for (var rowNumber = headerRow.RowNumber() + 1; rowNumber <= lastRow; rowNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = sheet.Row(rowNumber);
                if (row.IsEmpty()) continue;
                preview.TotalRows++;
                var buyerRaw = Get(row, headers, "买家id");
                var buyer = BuyerIdentity.Normalize(buyerRaw);
                if (buyer.Length == 0)
                {
                    preview.InvalidBuyerIdRows++;
                    continue;
                }
                if (conflicts.Contains(buyer))
                {
                    preview.ConflictBuyerIds.Add(buyer);
                    continue;
                }
                if (!whitelist.TryGetValue(buyer, out var lead))
                {
                    preview.UnmatchedRows++;
                    preview.UnmatchedBuyerIds.Add(buyerRaw.Trim());
                    continue;
                }

                var item = BuildEvent(sheetContract.Value, row, headers);
                item.BuyerId = lead.BuyerId;
                item.LeadId = lead.Id;
                item.SourceFileHash = preview.SourceFileHash;
                item.SourceSheet = sheet.Name;
                item.SourceRow = rowNumber;
                item.ImportedAt = DateTimeOffset.Now;
                item.EventKey = BuildEventKey(item);
                result.Add(item);
            }
        }
        return result;
    }

    private static OpportunityTransactionEvent BuildEvent(
        OpportunityEventKind kind,
        IXLRow row,
        IReadOnlyDictionary<string, int> headers)
    {
        var item = new OpportunityTransactionEvent
        {
            Kind = kind,
            DataDate = ParseDate(Get(row, headers, "更新日期")),
            OrderId = Get(row, headers, kind == OpportunityEventKind.AwaitingPayment ? "订单编号" : "订单号"),
            TransactionId = Get(row, headers, "支付流水号"),
            BuyerLevel = Get(row, headers, "下单时买家等级", "下单时的买家级别"),
            Country = Get(row, headers, "国家", "收货国家"),
            Currency = Get(row, headers, "支付币种", "订单币种"),
            PaymentChannel = Get(row, headers, "支付通道"),
            PrimaryCategory = Get(row, headers, "下单的产品总价最高的一级发布类目id"),
            SecondaryCategory = Get(row, headers, "下单的产品总价最高的二级发布类目id"),
            ProductName = Get(row, headers, "价格最高的商品名称"),
            SellerId = Get(row, headers, "卖家ID"),
            PrimaryChannel = Get(row, headers, "订单一级渠道"),
            SecondaryChannel = Get(row, headers, "订单二级渠道"),
            FailureReason = Get(row, headers, "支付失败原因"),
            DisputePrimaryReason = Get(row, headers, "纠纷开启一级原因中文描述"),
            DisputeSecondaryReason = Get(row, headers, "纠纷开启二级原因中文描述"),
            DisputeSubtype = Get(row, headers, "dispute_subtype"),
            IsChargeback = ParseBoolean(Get(row, headers, "是否拒付订单"))
        };
        item.Amount = ParseDecimal(Get(row, headers, kind switch
        {
            OpportunityEventKind.AwaitingPayment or OpportunityEventKind.Dispute => "订单GMV",
            _ => "支付金额"
        }));
        item.OccurredAt = ParseDate(Get(row, headers, kind switch
        {
            OpportunityEventKind.PaymentSucceeded or OpportunityEventKind.PaymentFailed => "支付日期",
            OpportunityEventKind.AwaitingPayment => "下单时间",
            OpportunityEventKind.Dispute => "协议纠纷的开启时间",
            _ => "更新日期"
        })) ?? item.DataDate;
        var secure = Get(row, headers, "是否3D支付（1是，0否）");
        if (!string.IsNullOrWhiteSpace(secure)) item.Is3DSecure = ParseBoolean(secure);
        return item;
    }

    private static OpportunitySnapshot BuildSnapshot(IReadOnlyList<OpportunityTransactionEvent> events)
    {
        var now = DateTimeOffset.Now;
        var succeeded = events.Where(item => item.Kind == OpportunityEventKind.PaymentSucceeded).ToList();
        var failed = events.Where(item => item.Kind == OpportunityEventKind.PaymentFailed).ToList();
        var unpaid = events.Where(item => item.Kind == OpportunityEventKind.AwaitingPayment).ToList();
        var disputes = events.Where(item => item.Kind == OpportunityEventKind.Dispute).ToList();
        var snapshot = new OpportunitySnapshot
        {
            LeadId = events[0].LeadId,
            BuyerId = events[0].BuyerId,
            SuccessfulPaymentCount = succeeded.Count,
            SuccessfulPaymentTotal = succeeded.Sum(item => item.Amount),
            LatestSuccessfulPaymentAt = MaxDate(succeeded),
            PaidAmount30Days = SumSince(succeeded, now.AddDays(-30)),
            PaidAmount90Days = SumSince(succeeded, now.AddDays(-90)),
            PaidAmount365Days = SumSince(succeeded, now.AddDays(-365)),
            PaidCount30Days = CountSince(succeeded, now.AddDays(-30)),
            PaidCount90Days = CountSince(succeeded, now.AddDays(-90)),
            PaidCount365Days = CountSince(succeeded, now.AddDays(-365)),
            AwaitingPaymentCount = unpaid.Count,
            AwaitingPaymentTotal = unpaid.Sum(item => item.Amount),
            LatestAwaitingPaymentAt = MaxDate(unpaid),
            FailedPaymentCount = failed.Count,
            FailedPaymentTotal = failed.Sum(item => item.Amount),
            LatestFailedPaymentAt = MaxDate(failed),
            LatestFailureReason = LatestNonEmpty(failed, item => item.FailureReason),
            LatestPaymentChannel = LatestNonEmpty(events, item => item.PaymentChannel),
            DisputeCount = disputes.Count,
            DisputeTotal = disputes.Sum(item => item.Amount),
            LatestDisputeAt = MaxDate(disputes),
            HasChargeback = disputes.Any(item => item.IsChargeback),
            PrimaryDisputeReason = TopText(disputes, item => item.DisputePrimaryReason),
            PrimaryCategory = TopCategoryByAmount(succeeded, item => item.PrimaryCategory),
            SecondaryCategory = TopCategoryByAmount(succeeded, item => item.SecondaryCategory),
            FrequentProduct = TopText(succeeded, item => item.ProductName),
            LatestProduct = LatestNonEmpty(succeeded, item => item.ProductName),
            MainCountry = TopText(events, item => item.Country),
            MainChannel = TopText(events, item => item.PrimaryChannel),
            MainSeller = TopText(succeeded, item => item.SellerId),
            LatestBuyerLevel = LatestNonEmpty(events, item => item.BuyerLevel),
            LatestActivityAt = events.Max(item => item.OccurredAt ?? item.DataDate),
            UpdatedAt = DateTimeOffset.Now
        };
        snapshot.AverageOrderValue = snapshot.SuccessfulPaymentCount == 0 ? 0 : snapshot.SuccessfulPaymentTotal / snapshot.SuccessfulPaymentCount;
        snapshot.DisputeRate = snapshot.SuccessfulPaymentCount == 0 ? 0 : (decimal)snapshot.DisputeCount / snapshot.SuccessfulPaymentCount;
        snapshot.DataFingerprint = Fingerprint(new
        {
            snapshot.LeadId, snapshot.BuyerId, snapshot.SuccessfulPaymentCount, snapshot.SuccessfulPaymentTotal,
            snapshot.AverageOrderValue, snapshot.LatestSuccessfulPaymentAt, snapshot.PaidAmount30Days, snapshot.PaidAmount90Days,
            snapshot.PaidAmount365Days, snapshot.PaidCount30Days, snapshot.PaidCount90Days, snapshot.PaidCount365Days,
            snapshot.AwaitingPaymentCount, snapshot.AwaitingPaymentTotal, snapshot.LatestAwaitingPaymentAt,
            snapshot.FailedPaymentCount, snapshot.FailedPaymentTotal, snapshot.LatestFailedPaymentAt,
            snapshot.LatestFailureReason, snapshot.LatestPaymentChannel, snapshot.DisputeCount, snapshot.DisputeTotal,
            snapshot.LatestDisputeAt, snapshot.DisputeRate, snapshot.HasChargeback, snapshot.PrimaryDisputeReason,
            snapshot.PrimaryCategory, snapshot.SecondaryCategory, snapshot.FrequentProduct, snapshot.LatestProduct,
            snapshot.MainCountry, snapshot.MainChannel, snapshot.MainSeller, snapshot.LatestBuyerLevel, snapshot.LatestActivityAt
        });
        return snapshot;
    }

    private static void ValidateHeaders(
        OpportunityEventKind kind,
        string sheetName,
        IReadOnlyDictionary<string, int> headers)
    {
        var required = kind switch
        {
            OpportunityEventKind.PaymentSucceeded => new[] { "买家id", "支付日期", "支付流水号", "订单号", "支付金额" },
            OpportunityEventKind.PaymentFailed => new[] { "买家id", "支付日期", "支付流水号", "订单号", "支付金额" },
            OpportunityEventKind.AwaitingPayment => new[] { "买家id", "订单编号", "下单时间", "订单GMV" },
            OpportunityEventKind.Dispute => new[] { "买家id", "订单编号", "订单GMV", "协议纠纷的开启时间" },
            _ => []
        };
        var missing = required.Where(header => !headers.ContainsKey(NormalizeHeader(header))).ToList();
        if (missing.Count > 0)
            throw new InvalidDataException($"工作表“{sheetName}”缺少字段：{string.Join("、", missing)}。");
    }

    private static string BuildEventKey(OpportunityTransactionEvent item)
    {
        var identity = item.Kind switch
        {
            OpportunityEventKind.PaymentSucceeded when item.TransactionId.Length > 0 =>
                $"{item.BuyerId}|{item.Kind}|{item.TransactionId}|{item.OrderId}",
            OpportunityEventKind.PaymentFailed when item.TransactionId.Length > 0 =>
                $"{item.BuyerId}|{item.Kind}|{item.TransactionId}",
            OpportunityEventKind.AwaitingPayment =>
                $"{item.BuyerId}|{item.Kind}|{item.OrderId}",
            OpportunityEventKind.Dispute =>
                $"{item.BuyerId}|{item.Kind}|{item.OrderId}|{item.OccurredAt:O}",
            _ =>
                $"{item.BuyerId}|{item.Kind}|{item.OrderId}|{item.OccurredAt:O}|{item.Amount.ToString(CultureInfo.InvariantCulture)}"
        };
        return Fingerprint(identity);
    }

    private static string Get(IXLRow row, IReadOnlyDictionary<string, int> headers, params string[] names)
    {
        foreach (var name in names)
            if (headers.TryGetValue(NormalizeHeader(name), out var column))
                return row.Cell(column).GetFormattedString(CultureInfo.InvariantCulture).Trim();
        return "";
    }

    private static string NormalizeHeader(string value) =>
        value.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).ToLowerInvariant();

    private static DateTimeOffset? ParseDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var offset)) return offset;
        if (DateTimeOffset.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out offset)) return offset;
        if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var serial) && serial is > 1 and < 100000)
            return new DateTimeOffset(DateTime.FromOADate(serial));
        return null;
    }

    private static decimal ParseDecimal(string value)
    {
        var normalized = value.Replace(",", "", StringComparison.Ordinal).Trim();
        return decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            || decimal.TryParse(normalized, NumberStyles.Any, CultureInfo.CurrentCulture, out result)
            ? result
            : 0;
    }

    private static bool ParseBoolean(string value) =>
        value.Trim().Equals("1", StringComparison.OrdinalIgnoreCase)
        || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
        || value.Contains('是');

    private static DateTimeOffset? MaxDate(IEnumerable<OpportunityTransactionEvent> events) =>
        events.Select(item => item.OccurredAt ?? item.DataDate).Where(value => value is not null).Max();

    private static decimal SumSince(IEnumerable<OpportunityTransactionEvent> events, DateTimeOffset threshold) =>
        events.Where(item => (item.OccurredAt ?? item.DataDate) >= threshold).Sum(item => item.Amount);

    private static int CountSince(IEnumerable<OpportunityTransactionEvent> events, DateTimeOffset threshold) =>
        events.Count(item => (item.OccurredAt ?? item.DataDate) >= threshold);

    private static string LatestNonEmpty(
        IEnumerable<OpportunityTransactionEvent> events,
        Func<OpportunityTransactionEvent, string> selector) =>
        events.Where(item => !string.IsNullOrWhiteSpace(selector(item)))
            .OrderByDescending(item => item.OccurredAt ?? item.DataDate)
            .Select(selector)
            .FirstOrDefault() ?? "";

    private static string TopText(
        IEnumerable<OpportunityTransactionEvent> events,
        Func<OpportunityTransactionEvent, string> selector) =>
        events.Select(selector).Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Key)
            .FirstOrDefault() ?? "";

    private static string TopCategoryByAmount(
        IEnumerable<OpportunityTransactionEvent> events,
        Func<OpportunityTransactionEvent, string> selector) =>
        events.Where(item => !string.IsNullOrWhiteSpace(selector(item)))
            .GroupBy(item => selector(item).Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                Category = group.Key,
                Amount = group.Sum(item => item.Amount),
                Latest = group.Max(item => item.OccurredAt ?? item.DataDate)
            })
            .OrderByDescending(item => item.Amount)
            .ThenByDescending(item => item.Latest)
            .Select(item => item.Category)
            .FirstOrDefault() ?? "";

    private static async Task<string> ComputeFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static string Fingerprint<T>(T value)
    {
        var bytes = Encoding.UTF8.GetBytes(value is string text ? text : Json.Serialize(value));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
