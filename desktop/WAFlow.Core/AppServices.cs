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
    public PublicIpMonitor PublicIp { get; }
    public CampaignAutomationService Campaigns { get; }

    public AppServices(LocalRepository? repository = null)
    {
        Repository = repository ?? new LocalRepository();
        Scoring = new LeadScoringService();
        Secrets = new WindowsCredentialStore();
        Imports = new ImportService(Repository, Scoring);
        DeepSeek = new DeepSeekService(Repository, Secrets);
        WhatsApp = new WhatsAppConnectionManager();
        WhatsAppSync = new WhatsAppSyncService(Repository, WhatsApp);
        PublicIp = new PublicIpMonitor(Repository);
        Campaigns = new CampaignAutomationService(Repository, WhatsApp);
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Repository.InitializeAsync(cancellationToken);
}
