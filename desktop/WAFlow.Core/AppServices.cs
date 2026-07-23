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
    public ConversationAssistantService ConversationAssistant { get; }
    public CustomerBrainService CustomerBrain { get; }
    public CustomerActionLifecycleService CustomerActions { get; }
    public PersonalSalesLearningService SalesLearning { get; }
    public TodayBriefService TodayBrief { get; }

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
        CustomerBrain = new CustomerBrainService(Repository, DeepSeek);
        CustomerActions = new CustomerActionLifecycleService(Repository);
        SalesLearning = new PersonalSalesLearningService(Repository);
        ConversationAssistant = new ConversationAssistantService(Repository, DeepSeek, SalesLearning);
        TodayBrief = new TodayBriefService(Repository, SalesLearning);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Repository.InitializeAsync(cancellationToken);

    private static LocalRepository CreateDefaultRepository()
    {
        var overridePath = Environment.GetEnvironmentVariable("WAFLOW_DATABASE_PATH");
        return new LocalRepository(string.IsNullOrWhiteSpace(overridePath) ? null : overridePath);
    }
}
