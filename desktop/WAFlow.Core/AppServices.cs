using WAFlow.Core.Imports;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Core;

public sealed class AppServices
{
    public LocalRepository Repository { get; }
    public LeadScoringService Scoring { get; }
    public ImportService Imports { get; }
    public WindowsCredentialStore Secrets { get; }
    public DeepSeekService DeepSeek { get; }
    public WhatsAppConnectionManager WhatsApp { get; }
    public WhatsAppSyncService WhatsAppSync { get; }
    public EmailService Email { get; }
    public LeadIntelligenceAutomationService LeadAutomation { get; }
    public PublicIpMonitor PublicIp { get; }
    public CampaignAutomationService Campaigns { get; }
    public CustomerAnalysisService CustomerAnalysis { get; }
    public CustomerReportExportService CustomerReportExports { get; }

    public AppServices(LocalRepository? repository = null)
    {
        Repository = repository ?? CreateDefaultRepository();
        Scoring = new LeadScoringService();
        Secrets = new WindowsCredentialStore();
        Imports = new ImportService(Repository);
        DeepSeek = new DeepSeekService(Repository, Secrets);
        WhatsApp = new WhatsAppConnectionManager();
        WhatsAppSync = new WhatsAppSyncService(Repository, WhatsApp);
        Email = new EmailService(Repository);
        LeadAutomation = new LeadIntelligenceAutomationService(Repository, DeepSeek, WhatsAppSync);
        PublicIp = new PublicIpMonitor(Repository);
        Campaigns = new CampaignAutomationService(Repository, WhatsApp, PublicIp, Email);
        CustomerAnalysis = new CustomerAnalysisService(Repository, DeepSeek);
        CustomerReportExports = new CustomerReportExportService(Repository);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Repository.InitializeAsync(cancellationToken);

    private static LocalRepository CreateDefaultRepository()
    {
        var overridePath = Environment.GetEnvironmentVariable("WAFLOW_DATABASE_PATH");
        return new LocalRepository(string.IsNullOrWhiteSpace(overridePath) ? null : overridePath);
    }
}
