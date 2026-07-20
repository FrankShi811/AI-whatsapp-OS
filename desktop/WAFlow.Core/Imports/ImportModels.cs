using WAFlow.Core.Domain;

namespace WAFlow.Core.Imports;

public enum ImportField
{
    Ignore, Custom, Name, Company, Country, WhatsApp, Email, ProductInterest, EstimatedOrderValue,
    CompanyScale, PurchasePower, ExplicitDemand, Source, Owner, Stage, Tags, Notes
}

public sealed class ImportSheet
{
    public string Name { get; set; } = "";
    public List<string> Headers { get; set; } = [];
    public List<Dictionary<string, string>> Rows { get; set; } = [];
    public int SanitizedFormulaCount { get; set; }
}

public sealed class ParsedImport
{
    public string FilePath { get; set; } = "";
    public string PreferredSheetName { get; set; } = "";
    public List<ImportSheet> Sheets { get; set; } = [];
}

public sealed class MappingRow
{
    public string Header { get; set; } = "";
    public string Sample { get; set; } = "";
    public ImportField Target { get; set; }
    public string DestinationLabel => Target == ImportField.Custom ? $"自定义维度：{Header}" : Target.ToString();
}

public sealed class ImportPreviewRow
{
    public int RowNumber { get; set; }
    public Dictionary<ImportField, string> Values { get; set; } = [];
    public Dictionary<string, string> CustomValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string Name { get; set; } = "";
    public string Company { get; set; } = "";
    public string Country { get; set; } = "";
    public string PhoneE164 { get; set; } = "";
    public bool PhoneValid { get; set; }
    public bool IsDuplicate { get; set; }
    public string DuplicateLeadId { get; set; } = "";
    public int? DuplicateRowNumber { get; set; }
    public string Changes { get; set; } = "";
    public List<string> Errors { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string StatusLabel => Errors.Count > 0 ? "错误" : IsDuplicate ? "重复·待更新" : PhoneValid ? "可导入" : "号码风险";
    public string WarningsLabel => string.Join("；", Warnings);
    public string ErrorsLabel => string.Join("；", Errors);
}

public sealed record ImportCommitResult(int Total, int Created, int Updated, int InvalidPhones, int Failed);

public sealed record ImportProgress(string Phase, int Completed, int Total)
{
    public int Percent => Total <= 0 ? 0 : Math.Clamp((int)Math.Round(Completed * 100d / Total), 0, 100);
    public string Label => Total <= 0 ? Phase : $"{Phase} {Completed:N0} / {Total:N0}";
}
