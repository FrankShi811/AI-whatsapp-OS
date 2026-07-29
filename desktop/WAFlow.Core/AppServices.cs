using WAFlow.Core.Imports;
using WAFlow.Core.Infrastructure;
using WAFlow.Core.Services;

namespace WAFlow.Core;

public sealed class AppServices
{
    public DataWorkspaceManager DataWorkspaceManager { get; }
    public DataWorkspaceLocation DataWorkspace { get; }
    public LocalRepository Repository { get; }
    public LeadScoringService Scoring { get; }
    public ImportService Imports { get; }
    public ISecretStore Secrets { get; }
    public DeepSeekService DeepSeek { get; }
    public WhatsAppConnectionManager WhatsApp { get; }
    public WhatsAppNumberValidationService WhatsAppNumberValidation { get; }
    public WhatsAppSyncService WhatsAppSync { get; }
    public EmailService Email { get; }
    public EmailAssistantService EmailAssistant { get; }
    public MessagingSyncService MessagingSync { get; }
    public LeadIntelligenceAutomationService LeadAutomation { get; }
    public PublicIpMonitor PublicIp { get; }
    public CampaignAutomationService Campaigns { get; }
    public CustomerAnalysisService CustomerAnalysis { get; }
    public CustomerReportExportService CustomerReportExports { get; }
    public ConversationAssistantService ConversationAssistant { get; }
    public CustomerIdentityService CustomerIdentity { get; }
    public SourcingRequestService SourcingRequests { get; }
    public CustomerSuccessAgentService CustomerSuccessAgent { get; }
    public CustomerSuccessAgentCoordinator CustomerSuccessCoordinator { get; }
    public CustomerBrainService CustomerBrain { get; }
    public CustomerActionLifecycleService CustomerActions { get; }
    public PersonalSalesLearningService SalesLearning { get; }
    public TodayBriefService TodayBrief { get; }
    public KnowledgeBaseService KnowledgeBase { get; }
    public KnowledgeRetrievalService KnowledgeRetrieval { get; }
    public KnowledgeLearningService KnowledgeLearning { get; }

    public AppServices(
        LocalRepository? repository = null,
        DataWorkspaceManager? dataWorkspaceManager = null,
        Func<string, ISecretStore>? secretStoreFactory = null)
    {
        DataWorkspaceManager = dataWorkspaceManager ?? new DataWorkspaceManager();
        DataWorkspace = repository is null
            ? DataWorkspaceManager.Resolve()
            : DataWorkspaceManager.FromDatabasePath(repository.DatabasePath);
        Repository = repository ?? new LocalRepository(DataWorkspace.DatabasePath);
        secretStoreFactory ??= target => new WindowsCredentialStore(target);
        Scoring = new LeadScoringService();
        Secrets = secretStoreFactory("WAFlow/DeepSeekApiKey");
        KnowledgeRetrieval = new KnowledgeRetrievalService(Repository);
        DeepSeek = new DeepSeekService(
            Repository,
            Secrets,
            knowledgeRetrieval: KnowledgeRetrieval,
            providerSecretResolver: providerId => secretStoreFactory($"WAFlow/AiProvider/{providerId}"));
        KnowledgeBase = new KnowledgeBaseService(
            Repository,
            new CompositeKnowledgeDocumentParser(new AiProviderImageTextExtractor(DeepSeek)));
        Imports = new ImportService(Repository);
        WhatsApp = new WhatsAppConnectionManager(
            DataWorkspace.RootDirectory,
            secretStoreFactory);
        WhatsAppNumberValidation = new WhatsAppNumberValidationService(Repository, WhatsApp);
        WhatsAppSync = new WhatsAppSyncService(Repository, WhatsApp);
        Email = new EmailService(Repository, secretStoreFactory);
        EmailAssistant = new EmailAssistantService(Repository, DeepSeek, KnowledgeRetrieval);
        MessagingSync = new MessagingSyncService(Repository, WhatsApp, Email);
        LeadAutomation = new LeadIntelligenceAutomationService(Repository, DeepSeek, WhatsAppSync);
        PublicIp = new PublicIpMonitor(Repository);
        Campaigns = new CampaignAutomationService(Repository, WhatsApp, PublicIp, Email);
        CustomerAnalysis = new CustomerAnalysisService(Repository, DeepSeek, KnowledgeRetrieval);
        CustomerReportExports = new CustomerReportExportService(Repository);
        CustomerBrain = new CustomerBrainService(Repository, DeepSeek, KnowledgeRetrieval);
        CustomerActions = new CustomerActionLifecycleService(Repository);
        SalesLearning = new PersonalSalesLearningService(Repository);
        ConversationAssistant = new ConversationAssistantService(Repository, DeepSeek, SalesLearning, KnowledgeRetrieval);
        CustomerIdentity = new CustomerIdentityService(Repository);
        SourcingRequests = new SourcingRequestService(Repository);
        KnowledgeLearning = new KnowledgeLearningService(Repository, SalesLearning);
        CustomerSuccessAgent = new CustomerSuccessAgentService(
            Repository,
            DeepSeek,
            CustomerIdentity,
            SourcingRequests,
            KnowledgeRetrieval);
        CustomerSuccessCoordinator = new CustomerSuccessAgentCoordinator(Repository, WhatsAppSync, WhatsApp, CustomerSuccessAgent);
        TodayBrief = new TodayBriefService(Repository, SalesLearning);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Repository.InitializeAsync(cancellationToken);
        await CustomerIdentity.RepairOwnedAccountBindingsAsync(cancellationToken);
    }
}
